using System.Security.Claims;
using Chronicle.API.DTOs;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.API.Controllers;

[ApiController]
[Route("api/v1/scan")]
[Authorize]
public class FileScanController : ControllerBase
{
    private readonly IFileScanService _scanService;
    private readonly ScanProgressService _progress;
    private readonly ImportProgressService _importProgress;
    private readonly IScanFolderService _scanFolderService;
    private readonly ChronicleDbContext _context;
    private readonly ILogger<FileScanController> _logger;

    public FileScanController(IFileScanService scanService, ScanProgressService progress,
        ImportProgressService importProgress, IScanFolderService scanFolderService,
        ChronicleDbContext context, ILogger<FileScanController> logger)
    {
        _scanService = scanService;
        _progress = progress;
        _importProgress = importProgress;
        _scanFolderService = scanFolderService;
        _context = context;
        _logger = logger;
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
    /// Validates that the given path exists and is accessible by Chronicle.
    /// Returns {valid: true} if the path is usable, or {valid: false, error: "..."} otherwise.
    /// </summary>
    [HttpPost("validate-path")]
    public async Task<IActionResult> ValidatePath([FromBody] ValidatePathDto dto, CancellationToken ct)
    {
        var result = await _scanFolderService.ValidatePathAsync(dto.Path, ct);
        return Ok(ApiResponse<PathValidationResultDto>.Ok(new(result.Valid, result.Error)));
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
        catch (OperationCanceledException)
        {
            return StatusCode(499, ApiResponse<FileScanSummaryDto>.Fail("SCAN_CANCELLED", "Scan was cancelled."));
        }
        catch (Exception ex)
        {
            return StatusCode(500, ApiResponse<FileScanSummaryDto>.Fail("SCAN_ERROR", ex.Message));
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
        catch (OperationCanceledException)
        {
            return StatusCode(499, ApiResponse<ScanPreviewDto>.Fail("SCAN_CANCELLED", "Scan was cancelled."));
        }
        catch (Exception ex)
        {
            return StatusCode(500, ApiResponse<ScanPreviewDto>.Fail("SCAN_ERROR", ex.Message));
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
                        c.Overview, c.Rating, c.MatchScore, c.Source, c.Genres, c.Cast, c.Sources, c.ContributingExternalIds
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

            // Check which results are already in the current user's library by matching external IDs.
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            Dictionary<string, int> libraryByExternalId = [];
            if (int.TryParse(userIdStr, out var userId) && results.Count > 0)
            {
                var allExternalIds = results
                    .SelectMany(r => (r.ContributingExternalIds ?? []).Prepend(r.ExternalId))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // media_external_ids joined with user_libraries for this user.
                // Use GroupBy+First to avoid ArgumentException when two items share the same ExternalId string.
                var libraryRows = await _context.MediaExternalIds
                    .Where(x => allExternalIds.Contains(x.ExternalId)
                             && _context.UserLibraries.Any(l => l.MediaItemId == x.MediaItemId && l.UserId == userId))
                    .ToListAsync(ct);

                var grouped = libraryRows
                    .GroupBy(x => x.ExternalId, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var dup in grouped.Where(g => g.Count() > 1))
                    _logger.LogWarning("Duplicate library entries for ExternalId {ExternalId}: item IDs [{ItemIds}]",
                        dup.Key, string.Join(", ", dup.Select(x => x.MediaItemId)));

                libraryByExternalId = grouped
                    .ToDictionary(g => g.Key, g => g.First().MediaItemId, StringComparer.OrdinalIgnoreCase);
            }

            int? ResolveLibraryItemId(string primaryId, List<string>? contributing)
            {
                if (libraryByExternalId.TryGetValue(primaryId, out var id)) return id;
                foreach (var c in contributing ?? [])
                    if (libraryByExternalId.TryGetValue(c, out var cid)) return cid;
                return null;
            }

            var dtos = results
                .Select(r => new MetadataCandidateDto(r.ExternalId, r.Title, r.Year, r.PosterUrl, r.Overview, r.Rating, r.MatchScore, r.Source, r.Genres, r.Cast, r.Sources, r.ContributingExternalIds,
                    LibraryItemId: ResolveLibraryItemId(r.ExternalId, r.ContributingExternalIds)))
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
            var item = await _scanService.AddFromSearchAsync(dto.ExternalId, dto.MediaTypeId, userId, ct,
                dto.ContributingExternalIds);

            var fs = ParseFileScannerMeta(item.MetadataJson);
            var itemDto = new MediaItemDto(
                item.Id, item.MediaTypeId,
                item.MediaType?.DisplayName ?? string.Empty,
                item.ParentId, item.Name, item.Year, item.Overview, item.PosterUrl,
                item.RuntimeMinutes, item.HierarchyLevel, item.Number,
                item.CreatedAt, item.UpdatedAt,
                item.ExternalIds.Select(e => new ExternalIdDto(e.Source, e.ExternalId)).ToList(),
                FileScannerMeta: fs,
                ResolvedMetadata: null);

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

    /// <summary>
    /// Imports scanned files directly from scanner metadata (title, year, file path)
    /// without calling a metadata provider first. Chronicle's background refresh
    /// service will enrich each item with TMDB data automatically.
    /// </summary>
    [HttpPost("import-direct")]
    public async Task<IActionResult> ImportDirect(
        [FromBody] DirectImportRequestDto dto,
        CancellationToken ct)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier)
                       ?? User.FindFirstValue("sub");
        if (!int.TryParse(userIdClaim, out var userId))
            return Unauthorized(ApiResponse<ImportSummaryDto>.Fail("UNAUTHORIZED", "User identity could not be determined."));

        try
        {
            var files = dto.Files
                .Select(f => new DirectImportFile(
                    f.FilePath, f.ParsedTitle, f.ParsedYear, f.SuggestedExternalId, f.MediaTypeHint,
                    f.ShowTitle, f.SeasonNumber, f.EpisodeNumber, f.EpisodeTitle, f.AudioTrackNumber))
                .ToList();

            var request = new DirectImportRequest(files, dto.MediaTypeId, userId);
            var summary = await _scanService.ImportDirectAsync(request, ct);

            return Ok(ApiResponse<ImportSummaryDto>.Ok(
                new ImportSummaryDto(summary.Imported, summary.Failed, summary.Failures)));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<ImportSummaryDto>.Fail("IMPORT_ERROR", ex.Message));
        }
    }

    /// <summary>
    /// Scans a directory and returns files grouped into a candidate hierarchy
    /// (Artist→Album→Track, Show→Season→Episode) with confidence scores. Read-only.
    /// </summary>
    [HttpPost("preview-grouped")]
    public async Task<IActionResult> PreviewGrouped(
        [FromBody] ScanPreviewRequestDto request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
            return BadRequest(ApiResponse<object>.Fail("INVALID_PATH", "Path is required."));

        try
        {
            var result = await _scanService.PreviewGroupedAsync(
                new ScanPreviewRequest(request.Path, request.Recursive, request.MediaTypeId), ct);

            return Ok(ApiResponse<ScanGroupResultDto>.Ok(ToGroupResultDto(result)));
        }
        catch (DirectoryNotFoundException ex)
        {
            return BadRequest(ApiResponse<object>.Fail("PATH_NOT_FOUND", ex.Message));
        }
    }

    /// <summary>
    /// Starts persisting accepted ScanGroups as a MediaItem hierarchy in a background task.
    /// Returns 202 Accepted immediately. Poll GET /scan/import-progress for status.
    /// Returns 409 Conflict if an import is already running.
    /// </summary>
    [HttpPost("import-groups")]
    public IActionResult ImportGroups(
        [FromBody] ImportGroupsRequestDto request)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier)
                       ?? User.FindFirstValue("sub");
        if (!int.TryParse(userIdClaim, out var userId))
            return Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "User identity could not be determined."));

        var current = _importProgress.GetState();
        if (current.IsRunning)
            return Conflict(ApiResponse<object>.Fail("IMPORT_RUNNING", "An import is already in progress."));

        _importProgress.Reset();

        var groups = request.Groups.Select(ToGroupImport).ToList();
        var importRequest = new ImportGroupsRequest(groups, request.MediaTypeId);

        // Capture the service provider so the background task can create its own scope
        // (FileScanService is scoped — it cannot be used across requests without a scope).
        var sp = HttpContext.RequestServices;

        _ = Task.Run(async () =>
        {
            // Create a DI scope so EF Core DbContext is not shared across threads.
            await using var scope = sp.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<IFileScanService>();
            try
            {
                // Pass the requesting user so they get an eager library row.
                // Other users get rows auto-created by GetForUserAsync on their next library view.
                await svc.ImportGroupsAsync(importRequest, [userId], CancellationToken.None);
            }
            catch (Exception ex)
            {
                _importProgress.Fail(ex.Message);
            }
        });

        return Accepted(ApiResponse<object>.Ok(new { started = true }));
    }

    /// <summary>
    /// Returns the current state of the import-groups background task.
    /// Poll every 500 ms while IsRunning is true; stop when IsComplete is true.
    /// </summary>
    [HttpGet("import-progress")]
    [AllowAnonymous]
    public IActionResult GetImportProgress()
    {
        var state = _importProgress.GetState();
        ImportSummaryDto? result = null;
        if (state.Result is not null)
        {
            result = new ImportSummaryDto(
                state.Result.Imported,
                state.Result.Failed,
                state.Result.Failures,
                state.Result.Duplicates);
        }

        return Ok(ApiResponse<ImportProgressDto>.Ok(new ImportProgressDto(
            state.IsRunning,
            state.IsComplete,
            state.Total,
            state.Processed,
            state.CurrentItemName,
            state.StatusMessage,
            state.Error,
            result)));
    }

    private static ScanGroupResultDto ToGroupResultDto(Chronicle.Core.Models.Scan.ScanGroupResult r) => new(
        r.Groups.Select(ToGroupDto).ToList(),
        r.Ungrouped,
        r.TotalFiles);

    private static ScanGroupDto ToGroupDto(Chronicle.Core.Models.Scan.ScanGroup g) => new(
        g.GroupKey, g.Name, g.HierarchyLevel, g.Year, g.Number,
        g.PosterPath, (int)Math.Round(g.ConfidenceScore * 100),
        g.SignalSources, g.HasConflicts,
        g.Children.Select(ToGroupDto).ToList(),
        g.Files, g.FolderPath, g.Author, g.Series);

    private static Chronicle.Services.ScanGroupImport ToGroupImport(ImportGroupDto g) =>
        new(g.Name, g.Year, g.PosterPath,
            g.Children.Select(ToGroupImport).ToList(),
            g.Files, g.FolderPath, g.Number);

    /// <summary>
    /// Returns a snapshot of the currently-running preview scan (folder being scanned,
    /// how many folders have been processed, how many files found so far).
    /// Returns IsScanning=false when no scan is in progress.
    /// Polled by the frontend every 500 ms while the "Scan Directory" request is pending.
    /// </summary>
    [HttpGet("progress")]
    [AllowAnonymous]
    public IActionResult GetProgress()
    {
        var snap = _progress.GetSnapshot();
        return Ok(ApiResponse<ScanProgressDto>.Ok(new ScanProgressDto(
            snap.IsScanning,
            snap.CurrentFolder,
            snap.FoldersScanned,
            snap.TotalFolders,
            snap.FilesFound)));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static readonly System.Text.Json.JsonSerializerOptions _jsonOpts =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    private static FileScannerMetaDto? ParseFileScannerMeta(string? json)
    {
        if (json is null) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            if (!root.TryGetProperty("fileScanner", out var fsEl)) return null;
            return System.Text.Json.JsonSerializer.Deserialize<FileScannerMetaDto>(fsEl.GetRawText(), _jsonOpts);
        }
        catch { return null; }
    }
}
