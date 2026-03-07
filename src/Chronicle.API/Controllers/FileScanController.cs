using System.Security.Claims;
using Chronicle.API.DTOs;
using Chronicle.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chronicle.API.Controllers;

[ApiController]
[Route("api/v1/scan")]
[Authorize]
public class FileScanController : ControllerBase
{
    private readonly IFileScanService _scanService;

    public FileScanController(IFileScanService scanService)
    {
        _scanService = scanService;
    }

    /// <summary>
    /// Returns whether a file scanner plugin is loaded and which media types it supports.
    /// The frontend uses this to conditionally show the Scan page in the navigation.
    /// </summary>
    [HttpGet("status")]
    [AllowAnonymous]
    public async Task<IActionResult> GetStatus()
    {
        var (available, names) = await _scanService.GetStatusAsync();
        return Ok(ApiResponse<FileScanStatusDto>.Ok(new FileScanStatusDto(available, names)));
    }

    /// <summary>
    /// Scans a local directory for media files and adds matching items to the user's library.
    /// Files with a confidence score below the threshold are reported but not added.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> RunScan(
        [FromBody] FileScanRequestDto dto,
        CancellationToken ct)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier)
                       ?? User.FindFirstValue("sub");
        if (!int.TryParse(userIdClaim, out var userId))
            return Unauthorized(ApiResponse<FileScanSummaryDto>.Fail("UNAUTHORIZED", "User identity could not be determined."));

        var request = new FileScanRequest(
            dto.Path,
            dto.Recursive,
            dto.MediaTypeId,
            dto.ConfidenceThreshold
        );

        try
        {
            var summary = await _scanService.ScanAsync(request, userId, ct);
            var result = new FileScanSummaryDto(
                summary.Added,
                summary.Skipped,
                summary.AlreadyInLibrary,
                summary.SkippedFiles.Select(f => new SkippedFileDto(f.FilePath, f.ParsedTitle, f.ConfidenceScore)).ToList()
            );
            return Ok(ApiResponse<FileScanSummaryDto>.Ok(result));
        }
        catch (DirectoryNotFoundException ex)
        {
            return BadRequest(ApiResponse<FileScanSummaryDto>.Fail("DIRECTORY_NOT_FOUND", ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<FileScanSummaryDto>.Fail("SCAN_ERROR", ex.Message));
        }
    }

    /// <summary>
    /// Scans a directory and returns all discovered files without importing anything.
    /// Use this first step to let the user review before committing to the library.
    /// </summary>
    [HttpPost("preview")]
    public async Task<IActionResult> Preview(
        [FromBody] ScanPreviewRequestDto dto,
        CancellationToken ct)
    {
        try
        {
            var request = new ScanPreviewRequest(dto.Path, dto.Recursive, dto.MediaTypeId);
            var preview = await _scanService.PreviewAsync(request, ct);

            var result = new ScanPreviewDto(
                preview.Files.Select(f => new ScannedFileDto(
                    f.FilePath, f.ParsedTitle, f.ParsedYear,
                    f.ConfidenceScore, f.SuggestedExternalId, f.MediaTypeHint
                )).ToList()
            );
            return Ok(ApiResponse<ScanPreviewDto>.Ok(result));
        }
        catch (DirectoryNotFoundException ex)
        {
            return BadRequest(ApiResponse<ScanPreviewDto>.Fail("DIRECTORY_NOT_FOUND", ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<ScanPreviewDto>.Fail("SCAN_ERROR", ex.Message));
        }
    }

    /// <summary>
    /// Queries the active metadata provider (e.g. TMDB) for each scanned file
    /// and returns ranked candidates. The user selects one per file, then calls /import.
    /// </summary>
    [HttpPost("identify")]
    public async Task<IActionResult> Identify(
        [FromBody] IdentifyRequestDto dto,
        CancellationToken ct)
    {
        try
        {
            var files = dto.Files.Select(f => new ScannedFileResult(
                f.FilePath, f.ParsedTitle, f.ParsedYear,
                f.ConfidenceScore, f.SuggestedExternalId, f.MediaTypeHint
            )).ToList();

            var result = await _scanService.IdentifyAsync(new IdentifyRequest(files, dto.MediaTypeId), ct);

            var dto2 = new IdentifyResultDto(
                result.Results.Select(r => new FileIdentificationDto(
                    new ScannedFileDto(r.File.FilePath, r.File.ParsedTitle, r.File.ParsedYear,
                        r.File.ConfidenceScore, r.File.SuggestedExternalId, r.File.MediaTypeHint),
                    r.Candidates.Select(c => new MetadataCandidateDto(
                        c.ExternalId, c.Title, c.Year, c.PosterUrl,
                        c.Overview, c.Rating, c.MatchScore
                    )).ToList()
                )).ToList()
            );
            return Ok(ApiResponse<IdentifyResultDto>.Ok(dto2));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<IdentifyResultDto>.Fail("IDENTIFY_ERROR", ex.Message));
        }
    }

    /// <summary>
    /// Queries the active metadata provider (e.g. TMDB) for a free-text query.
    /// Used by the Add Media UI to let the user search without having a local file.
    /// </summary>
    [HttpGet("search")]
    public async Task<IActionResult> SearchMetadata(
        [FromQuery] string query,
        [FromQuery] string mediaTypeHint = "movie",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return BadRequest(ApiResponse<List<MetadataCandidateDto>>.Fail("QUERY_REQUIRED", "query is required."));

        try
        {
            var results = await _scanService.SearchMetadataAsync(query.Trim(), mediaTypeHint, ct);
            var dtos = results
                .Select(r => new MetadataCandidateDto(r.ExternalId, r.Title, r.Year, r.PosterUrl, r.Overview, r.Rating, r.MatchScore))
                .ToList();
            return Ok(ApiResponse<List<MetadataCandidateDto>>.Ok(dtos));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<List<MetadataCandidateDto>>.Fail("SEARCH_ERROR", ex.Message));
        }
    }

    /// <summary>
    /// Fetches full metadata for <paramref name="externalId"/>, creates a MediaItem,
    /// and adds it to the user's library. Returns the created MediaItem DTO.
    /// </summary>
    [HttpPost("add")]
    public async Task<IActionResult> AddFromSearch(
        [FromBody] AddFromSearchDto dto,
        CancellationToken ct)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier)
                       ?? User.FindFirstValue("sub");
        if (!int.TryParse(userIdClaim, out var userId))
            return Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "User identity could not be determined."));

        try
        {
            var item = await _scanService.AddFromSearchAsync(dto.ExternalId, dto.MediaTypeId, userId, ct);

            var (tmdb, fs) = ParseMetaJson(item.MetadataJson);
            var itemDto = new MediaItemDto(
                item.Id, item.MediaTypeId,
                item.MediaType?.DisplayName ?? string.Empty,
                item.ParentId, item.Name, item.Year, item.Overview, item.PosterUrl,
                item.RuntimeMinutes, item.HierarchyLevel, item.Number,
                item.CreatedAt, item.UpdatedAt,
                item.ExternalIds.Select(e => new ExternalIdDto(e.Source, e.ExternalId)).ToList(),
                TmdbMeta: tmdb, FileScannerMeta: fs);

            return Ok(ApiResponse<MediaItemDto>.Ok(itemDto));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<MediaItemDto>.Fail("ADD_ERROR", ex.Message));
        }
    }

    /// <summary>
    /// Imports user-approved (filePath, externalId) pairs:
    /// fetches full metadata, creates MediaItems, and adds them to the user's library.
    /// </summary>
    [HttpPost("import")]
    public async Task<IActionResult> ImportApproved(
        [FromBody] ImportRequestDto dto,
        CancellationToken ct)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier)
                       ?? User.FindFirstValue("sub");
        if (!int.TryParse(userIdClaim, out var userId))
            return Unauthorized(ApiResponse<ImportSummaryDto>.Fail("UNAUTHORIZED", "User identity could not be determined."));

        try
        {
            var approvals = dto.Approvals
                .Select(a => new ImportApproval(a.FilePath, a.ExternalId))
                .ToList();

            var request = new ImportApprovedRequest(approvals, dto.MediaTypeId, userId);
            var summary = await _scanService.ImportApprovedAsync(request, ct);

            return Ok(ApiResponse<ImportSummaryDto>.Ok(
                new ImportSummaryDto(summary.Imported, summary.Failed, summary.Failures)));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<ImportSummaryDto>.Fail("IMPORT_ERROR", ex.Message));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed record MediaMetaJsonRoot(TmdbMetaDto? Tmdb, FileScannerMetaDto? FileScanner);

    private static readonly System.Text.Json.JsonSerializerOptions _jsonOpts =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    private static (TmdbMetaDto? tmdb, FileScannerMetaDto? fs) ParseMetaJson(string? json)
    {
        if (json is null) return (null, null);
        try
        {
            var root = System.Text.Json.JsonSerializer.Deserialize<MediaMetaJsonRoot>(json, _jsonOpts);
            if (root?.Tmdb is not null) return (root.Tmdb, root.FileScanner);
            var flat = System.Text.Json.JsonSerializer.Deserialize<TmdbMetaDto>(json, _jsonOpts);
            return (flat, null);
        }
        catch { return (null, null); }
    }
}
