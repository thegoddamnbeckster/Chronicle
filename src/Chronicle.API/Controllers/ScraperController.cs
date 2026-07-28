using Chronicle.API.DTOs;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Chronicle.API.Controllers;

/// <summary>
/// Backs the Chronicle Scraper Kodi addon -- a real xbmc.metadata.scraper.movies
/// addon (distinct from Chronicle_Scrobbler, which is a service/script addon and
/// can't appear in Kodi's "Change Content" scraper list at all).
///
/// Deliberately does NOT talk to TMDB or any other upstream source directly: it only
/// ever searches Chronicle's own library, and when an item is missing, resolves-and-
/// creates it through Chronicle's already-configured metadata provider plugins (the
/// same pipeline MetadataEnrichmentService already runs for every other import path).
/// Kodi therefore always ends up seeing exactly what Chronicle itself would show for
/// that title, never an independent raw upstream answer.
/// </summary>
[ApiController]
[Route("api/v1/scraper")]
[Authorize]
public class ScraperController : ControllerBase
{
    private readonly ChronicleDbContext _context;
    private readonly IMediaService _mediaService;
    private readonly IMetadataEnrichmentService _enrichment;

    public ScraperController(ChronicleDbContext context, IMediaService mediaService,
        IMetadataEnrichmentService enrichment)
    {
        _context    = context;
        _mediaService = mediaService;
        _enrichment = enrichment;
    }

    /// <summary>
    /// Kodi's "find" step. Looks for an existing movie by title/year in Chronicle's
    /// own library; if none exists, creates a stub and resolves it through Chronicle's
    /// configured metadata providers before returning. Always returns at most one
    /// candidate -- Chronicle has already committed to one answer via its own
    /// confidence-scored resolution, so there's nothing for Kodi's user to disambiguate.
    /// </summary>
    [HttpGet("movies/search")]
    public async Task<IActionResult> SearchMovies([FromQuery] string? title, [FromQuery] int? year, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title))
            return BadRequest(ApiResponse<object>.Fail("TITLE_REQUIRED", "title is required."));

        var movieTypeId = await _context.MediaTypes
            .Where(t => t.Name == "movies" && t.IsActive)
            .Select(t => t.Id)
            .FirstOrDefaultAsync(ct);
        if (movieTypeId == 0)
            return NotFound(ApiResponse<object>.Fail("MEDIA_TYPE_NOT_FOUND", "No active 'movies' media type is configured."));

        var item = await _context.MediaItems.FirstOrDefaultAsync(m =>
            m.MediaTypeId == movieTypeId && m.HierarchyLevel == 0 &&
            m.Name == title && (!year.HasValue || m.Year == year), ct);

        if (item is null)
        {
            item = await _mediaService.CreateAsync(new CreateMediaRequest(
                movieTypeId, null, title, year, null, null, null, 0, null), ct);

            await _enrichment.EnrichItemAsync(item.Id,
                new EnrichmentOptions(EnrichmentMode.Force, Cascade: false), ct);

            // EnrichItemAsync commits its own changes through a separately-tracked
            // instance -- this context's copy of `item` won't reflect the written
            // MetadataJson until it's re-fetched.
            item = await _context.MediaItems.FindAsync([item.Id], ct);
        }

        if (item is null)
            return NotFound(ApiResponse<object>.Fail("RESOLVE_FAILED", "Could not resolve or create this title."));

        var resolved = ParseResolvedMetadata(item.MetadataJson);
        return Ok(ApiResponse<object>.Ok(new
        {
            id        = item.Id,
            title     = resolved?.Title ?? item.Name,
            year      = resolved?.Year ?? item.Year,
            posterUrl = resolved?.PosterUrl ?? item.PosterUrl,
        }));
    }

    /// <summary>Kodi's "getdetails" step: full resolved metadata for a Chronicle media_item id.</summary>
    [HttpGet("movies/details")]
    public async Task<IActionResult> GetMovieDetails([FromQuery] int id, CancellationToken ct)
    {
        var item = await _context.MediaItems.FindAsync([id], ct);
        if (item is null)
            return NotFound(ApiResponse<object>.Fail("MEDIA_NOT_FOUND", $"Media item {id} not found."));

        var resolved = ParseResolvedMetadata(item.MetadataJson);
        return Ok(ApiResponse<ResolvedMetadataDto?>.Ok(resolved));
    }

    // ── Helpers -- mirrors MediaController's own _resolved-JSON parsing exactly ────

    private static ResolvedMetadataDto? ParseResolvedMetadata(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (!doc.RootElement.TryGetProperty("_resolved", out var r) || r.ValueKind != JsonValueKind.Object)
                return null;

            return new ResolvedMetadataDto(
                Title:          TryGetString(r, "title"),
                Overview:       TryGetString(r, "overview"),
                Year:           TryGetInt(r,    "year"),
                PosterUrl:      TryGetString(r, "posterUrl"),
                BackdropUrl:    TryGetString(r, "backdropUrl"),
                RuntimeMinutes: TryGetInt(r,    "runtimeMinutes"),
                Rating:         TryGetDouble(r, "rating"),
                Genres:         TryGetStringList(r, "genres"),
                Cast:           TryGetStringList(r, "cast"),
                Directors:      TryGetStringList(r, "directors"),
                Tags:           TryGetStringList(r, "tags"),
                LogoUrl:        TryGetString(r, "logoUrl"),
                BannerUrl:      TryGetString(r, "bannerUrl"),
                ClearartUrl:    TryGetString(r, "clearartUrl"),
                DiscUrl:        TryGetString(r, "discUrl")
            );
        }
        catch { return null; }
    }

    private static string? TryGetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? TryGetInt(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static double? TryGetDouble(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static List<string>? TryGetStringList(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString()!);
        return list.Count > 0 ? list : null;
    }
}
