using Chronicle.API.DTOs;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Chronicle.API.Controllers;

/// <summary>
/// Backs the Chronicle Scraper Kodi addon -- both its xbmc.metadata.scraper.movies
/// and xbmc.metadata.scraper.tvshows extension points (distinct from Chronicle_Scrobbler,
/// which is a service/script addon and can't appear in Kodi's "Change Content" list at all).
///
/// Deliberately does NOT talk to TMDB or any other upstream source directly: it only
/// ever searches Chronicle's own library, and when an item is missing, resolves-and-
/// creates it through Chronicle's already-configured metadata provider plugins (the
/// same pipeline MetadataEnrichmentService already runs for every other import path).
/// Kodi therefore always ends up seeing exactly what Chronicle itself would show for
/// that title, never an independent raw upstream answer.
///
/// Every field this controller surfaces is read from data Chronicle already has --
/// mined across every "chronicle.plugin.*" partition in MetadataJson (per the
/// lossless-ingestion architecture rule), not fetched fresh or invented. Fields Kodi
/// supports but no configured provider currently populates (writers, movie studios,
/// sort title) are simply omitted rather than faked.
/// </summary>
[ApiController]
[Route("api/v1/scraper")]
[Authorize]
public class ScraperController : ControllerBase
{
    private readonly ChronicleDbContext _context;
    private readonly IMediaService _mediaService;
    private readonly IMetadataEnrichmentService _enrichment;
    private readonly ILogger<ScraperController> _logger;

    public ScraperController(ChronicleDbContext context, IMediaService mediaService,
        IMetadataEnrichmentService enrichment, ILogger<ScraperController> logger)
    {
        _context    = context;
        _mediaService = mediaService;
        _enrichment = enrichment;
        _logger     = logger;
    }

    // ── Movies ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Kodi's "find" step for movies. Looks for an existing movie by title/year in
    /// Chronicle's own library; if none exists, creates a stub and resolves it through
    /// Chronicle's configured metadata providers before returning. Always returns at
    /// most one candidate -- Chronicle has already committed to one answer via its own
    /// confidence-scored resolution, so there's nothing for Kodi's user to disambiguate.
    /// </summary>
    [HttpGet("movies/search")]
    public async Task<IActionResult> SearchMovies([FromQuery] string? title, [FromQuery] int? year, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title))
            return BadRequest(ApiResponse<object>.Fail("TITLE_REQUIRED", "title is required."));

        var movieTypeId = await GetMediaTypeIdAsync("movies", ct);
        if (movieTypeId == 0)
            return NotFound(ApiResponse<object>.Fail("MEDIA_TYPE_NOT_FOUND", "No active 'movies' media type is configured."));

        var candidates = await _context.MediaItems
            .Where(m => m.MediaTypeId == movieTypeId && m.HierarchyLevel <= 1 && (!year.HasValue || m.Year == year))
            .ToListAsync(ct);
        var existing = FindByNormalizedTitle(candidates, title);

        var item = await ResolveOrCreateAsync(existing, movieTypeId, title, year, ct);
        if (item is null)
        {
            _logger.LogWarning("scraper/movies/search: title={Title} year={Year} -- resolve-or-create failed, returning 404", title, year);
            return NotFound(ApiResponse<object>.Fail("RESOLVE_FAILED", "Could not resolve or create this title."));
        }

        var resolved = ParseResolvedCore(item.MetadataJson);
        var posterUrl = resolved?.PosterUrl ?? item.PosterUrl;
        _logger.LogInformation(
            "scraper/movies/search: title={Title} year={Year} -> item {ItemId} ({Existing}) posterUrl={PosterUrl}",
            title, year, item.Id, existing is null ? "newly resolved" : "existing", posterUrl ?? "(none)");

        return Ok(ApiResponse<object>.Ok(new
        {
            id        = item.Id,
            title     = resolved?.Title ?? item.Name,
            year      = resolved?.Year ?? item.Year,
            posterUrl,
        }));
    }

    /// <summary>Kodi's "getdetails" step for movies: the full richness Chronicle has for this item.</summary>
    [HttpGet("movies/details")]
    public async Task<IActionResult> GetMovieDetails([FromQuery] int id, CancellationToken ct)
    {
        var item = await _context.MediaItems.FindAsync([id], ct);
        if (item is null)
        {
            _logger.LogWarning("scraper/movies/details: item {ItemId} not found", id);
            return NotFound(ApiResponse<object>.Fail("MEDIA_NOT_FOUND", $"Media item {id} not found."));
        }

        ScraperCollectionDto? collection = null;
        if (item.ParentId.HasValue)
        {
            var parent = await _context.MediaItems.FindAsync([item.ParentId.Value], ct);
            if (parent is not null)
            {
                var parentResolved = ParseResolvedCore(parent.MetadataJson);
                var collectionPoster = parentResolved?.PosterUrl ?? parent.PosterUrl;
                var usedFallback = false;

                if (string.IsNullOrEmpty(collectionPoster))
                {
                    // Confirmed directly (2026-07-30): 35 collections in this library have no
                    // dedicated set-level art from ANY configured provider at all (not a stale
                    // registration like the earlier Video3/Video7 bug -- Chronicle genuinely has
                    // nothing for the collection itself), most with several real member movies,
                    // not just one-off single-movie groupings. Rather than leave Kodi's set card
                    // permanently blank, fall back to any member movie's own poster -- better
                    // than nothing, and a common convention other media managers already use.
                    collectionPoster = await FindFallbackCollectionPosterAsync(parent.Id, ct);
                    usedFallback = collectionPoster is not null;
                }

                collection = new ScraperCollectionDto(
                    parent.Id,
                    parentResolved?.Title ?? parent.Name,
                    parentResolved?.Overview ?? parent.Overview,
                    collectionPoster,
                    parentResolved?.BackdropUrl);

                if (string.IsNullOrEmpty(collectionPoster))
                    _logger.LogWarning(
                        "scraper/movies/details: item {ItemId} \"{Title}\" belongs to collection {CollectionId} \"{CollectionName}\" " +
                        "which has NO posterUrl in Chronicle (and no member movie has one either) -- Kodi's set poster will stay blank",
                        id, item.Name, parent.Id, collection.Name);
                else if (usedFallback)
                    _logger.LogInformation(
                        "scraper/movies/details: collection {CollectionId} \"{CollectionName}\" has no poster of its own -- " +
                        "using a member movie's poster instead", parent.Id, collection.Name);
            }
            else
            {
                _logger.LogWarning(
                    "scraper/movies/details: item {ItemId} \"{Title}\" has ParentId {ParentId} but that item does not exist -- dangling parent reference",
                    id, item.Name, item.ParentId.Value);
            }
        }

        var dto = BuildMovieDetails(item, collection);

        var artworkSummary = dto.Artwork is null
            ? "(none)"
            : string.Join(", ", dto.Artwork.Select(kv => $"{kv.Key}={kv.Value.Count}"));
        _logger.LogInformation(
            "scraper/movies/details: item {ItemId} \"{Title}\" -> artwork[{ArtworkSummary}] collection={Collection}",
            id, dto.Title, artworkSummary, collection is null ? "(none)" : $"\"{collection.Name}\" poster={(string.IsNullOrEmpty(collection.PosterUrl) ? "(none)" : "set")}");

        if (dto.Artwork is null || !dto.Artwork.TryGetValue("poster", out var posterCandidates) || posterCandidates.Count == 0)
            _logger.LogWarning(
                "scraper/movies/details: item {ItemId} \"{Title}\" has NO poster candidates at all -- " +
                "Kodi will show a blank/title-only thumbnail for this movie", id, dto.Title);

        return Ok(ApiResponse<ScraperMovieDetailsDto>.Ok(dto));
    }

    // ── TV shows ────────────────────────────────────────────────────────────

    /// <summary>Kodi's "find" step for TV shows -- same resolve-or-create pattern as movies.</summary>
    [HttpGet("tv/search")]
    public async Task<IActionResult> SearchShows([FromQuery] string? title, [FromQuery] int? year, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title))
            return BadRequest(ApiResponse<object>.Fail("TITLE_REQUIRED", "title is required."));

        var tvTypeId = await GetMediaTypeIdAsync("tv", ct);
        if (tvTypeId == 0)
            return NotFound(ApiResponse<object>.Fail("MEDIA_TYPE_NOT_FOUND", "No active 'tv' media type is configured."));

        var showCandidates = await _context.MediaItems
            .Where(m => m.MediaTypeId == tvTypeId && m.HierarchyLevel == 0 && (!year.HasValue || m.Year == year))
            .ToListAsync(ct);
        var item = FindByNormalizedTitle(showCandidates, title);

        item = await ResolveOrCreateAsync(item, tvTypeId, title, year, ct);
        if (item is null)
            return NotFound(ApiResponse<object>.Fail("RESOLVE_FAILED", "Could not resolve or create this show."));

        var resolved = ParseResolvedCore(item.MetadataJson);
        return Ok(ApiResponse<object>.Ok(new
        {
            id        = item.Id,
            title     = resolved?.Title ?? item.Name,
            year      = resolved?.Year ?? item.Year,
            posterUrl = resolved?.PosterUrl ?? item.PosterUrl,
        }));
    }

    /// <summary>Kodi's "getdetails" step for TV shows: show-level info plus every season Chronicle already has.</summary>
    [HttpGet("tv/details")]
    public async Task<IActionResult> GetShowDetails([FromQuery] int id, CancellationToken ct)
    {
        var item = await _context.MediaItems.FindAsync([id], ct);
        if (item is null)
            return NotFound(ApiResponse<object>.Fail("MEDIA_NOT_FOUND", $"Media item {id} not found."));

        var seasons = await _context.MediaItems
            .Where(m => m.ParentId == id && m.HierarchyLevel == 1)
            .OrderBy(m => m.Number)
            .Select(m => new ScraperSeasonDto(m.Id, m.Number ?? 0, m.Name, m.PosterUrl))
            .ToListAsync(ct);

        var dto = BuildShowDetails(item, seasons);
        return Ok(ApiResponse<ScraperShowDetailsDto>.Ok(dto));
    }

    /// <summary>
    /// Kodi's "getepisodelist" step: every episode Chronicle already has under this show,
    /// walking through season containers when present (see class remarks on the one show
    /// in dev data where episodes attach directly to the show, skipping the season level).
    /// Does not create anything -- episodes are expected to already exist from Chronicle's
    /// own file-scanner/import pipeline. A show with no episodes yet in Chronicle simply
    /// returns an empty list; Kodi's own filename matching has nothing to resolve against
    /// until Chronicle's backend populates them.
    /// </summary>
    [HttpGet("tv/episodes")]
    public async Task<IActionResult> GetEpisodes([FromQuery] int showId, CancellationToken ct)
    {
        var show = await _context.MediaItems.FindAsync([showId], ct);
        if (show is null)
            return NotFound(ApiResponse<object>.Fail("MEDIA_NOT_FOUND", $"Media item {showId} not found."));

        var seasonIds = await _context.MediaItems
            .Where(m => m.ParentId == showId && m.HierarchyLevel == 1)
            .Select(m => m.Id)
            .ToListAsync(ct);

        var episodes = await _context.MediaItems
            .Where(m => m.HierarchyLevel == 2 && (m.ParentId == showId || seasonIds.Contains(m.ParentId!.Value)))
            .Select(m => new { m.Id, m.Number, m.ParentId, m.Name })
            .ToListAsync(ct);

        var seasonNumberById = await _context.MediaItems
            .Where(m => seasonIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m.Number ?? 0, ct);

        var result = episodes.Select(e => new ScraperEpisodeSummaryDto(
            e.Id,
            Season: e.ParentId.HasValue && seasonNumberById.TryGetValue(e.ParentId.Value, out var sn) ? sn : 1,
            Episode: e.Number ?? 0,
            e.Name)).ToList();

        return Ok(ApiResponse<List<ScraperEpisodeSummaryDto>>.Ok(result));
    }

    /// <summary>Kodi's "getepisodedetails" step: full details for one already-known episode.</summary>
    [HttpGet("tv/episode-details")]
    public async Task<IActionResult> GetEpisodeDetails([FromQuery] int id, CancellationToken ct)
    {
        var item = await _context.MediaItems.FindAsync([id], ct);
        if (item is null)
            return NotFound(ApiResponse<object>.Fail("MEDIA_NOT_FOUND", $"Media item {id} not found."));

        var season = 1;
        if (item.ParentId.HasValue)
        {
            var parent = await _context.MediaItems.FindAsync([item.ParentId.Value], ct);
            if (parent is not null && parent.HierarchyLevel == 1)
                season = parent.Number ?? 1;
        }

        var dto = BuildEpisodeDetails(item, season);
        return Ok(ApiResponse<ScraperEpisodeDetailsDto>.Ok(dto));
    }

    // ── Shared resolve-or-create ────────────────────────────────────────────

    private async Task<MediaItem?> ResolveOrCreateAsync(
        MediaItem? existing, int mediaTypeId, string title, int? year, CancellationToken ct)
    {
        var item = existing;
        if (item is null)
        {
            item = await _mediaService.CreateAsync(new CreateMediaRequest(
                mediaTypeId, null, title, year, null, null, null, 0, null), ct);

            await _enrichment.EnrichItemAsync(item.Id,
                new EnrichmentOptions(EnrichmentMode.Force, Cascade: false), ct);

            // EnrichItemAsync commits its own changes through a separately-tracked
            // instance -- this context's copy of `item` won't reflect the written
            // MetadataJson until it's re-fetched.
            item = await _context.MediaItems.FindAsync([item.Id], ct);
        }
        return item;
    }

    /// <summary>Any member movie's own poster, resolved metadata first, for use only when the
    /// collection itself has none. Returns the first one found, not necessarily the "best".</summary>
    private async Task<string?> FindFallbackCollectionPosterAsync(int collectionId, CancellationToken ct)
    {
        var children = await _context.MediaItems
            .Where(m => m.ParentId == collectionId)
            .ToListAsync(ct);

        foreach (var child in children)
        {
            var poster = ParseResolvedCore(child.MetadataJson)?.PosterUrl ?? child.PosterUrl;
            if (!string.IsNullOrEmpty(poster))
                return poster;
        }
        return null;
    }

    private async Task<int> GetMediaTypeIdAsync(string name, CancellationToken ct) =>
        await _context.MediaTypes.Where(t => t.Name == name && t.IsActive).Select(t => t.Id).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Matches Kodi's title against candidates ignoring punctuation/case/whitespace, not an
    /// exact string match. Root-caused directly (2026-07-30): Kodi's folder-derived title for
    /// "Alien: Romulus" arrives as "Alien Romulus" (no colon -- colons aren't valid in Windows
    /// folder names), so the previous exact `m.Name == title` comparison never matched the
    /// already-fully-enriched existing item and ResolveOrCreateAsync created a second, blank
    /// stub instead every single time Kodi asked. Mirrors Chronicle_Scraper's own
    /// movie_art_sync.py `_normalize()` helper (strip everything but letters/digits, lowercase)
    /// so both sides of this exact same problem -- matching a folder-safe title back to
    /// Chronicle's own punctuated one -- use the same rule.
    ///
    /// When MULTIPLE candidates normalize to the same title -- confirmed directly for
    /// "Titan A.E." (a real, fully-enriched item from months ago) vs "Titan A.E" (a blank
    /// stub created during the 2026-07-30 timeout storm, before this normalization fix
    /// existed) -- prefers whichever one actually has data. Without this, the plain
    /// `FirstOrDefault` below picked whichever row the database happened to return first,
    /// which is unspecified without an explicit ORDER BY and was intermittently landing on
    /// the empty stub, dropping the overview/cast/etc. for titles that already had every bit
    /// of that data on a different, older row.
    /// </summary>
    private static MediaItem? FindByNormalizedTitle(List<MediaItem> candidates, string title)
    {
        var target = NormalizeTitle(title);
        if (target.Length == 0) return null;
        return candidates
            .Where(m => NormalizeTitle(m.Name) == target)
            .OrderByDescending(m => m.MetadataJson?.Length ?? 0)
            .FirstOrDefault();
    }

    private static string NormalizeTitle(string? text) =>
        text is null ? "" : new string(text.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    // ── DTO builders ─────────────────────────────────────────────────────────

    private static ScraperMovieDetailsDto BuildMovieDetails(MediaItem item, ScraperCollectionDto? collection)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrEmpty(item.MetadataJson) ? "{}" : item.MetadataJson);
        var root = doc.RootElement;
        var core = ParseResolvedCore(item.MetadataJson);

        return new ScraperMovieDetailsDto(
            Title:          core?.Title ?? item.Name,
            Overview:       core?.Overview ?? item.Overview,
            Tagline:        FirstExtended(root, ext => TryGetString(ext, "tagline")),
            Year:           core?.Year ?? item.Year,
            Premiered:      FirstExtended(root, ext => TryGetString(ext, "released")),
            Mpaa:           FirstExtended(root, ext => TryGetString(ext, "certification")),
            Country:        FirstExtended(root, ext => TryGetString(ext, "country")),
            Studio:         FirstExtended(root, ext => TryGetString(ext, "network") ?? TryGetString(ext, "studio")),
            RuntimeMinutes: core?.RuntimeMinutes ?? item.RuntimeMinutes,
            Genres:         core?.Genres,
            Cast:           core?.Cast,
            Directors:      core?.Directors,
            Tags:           core?.Tags,
            Ratings:        CollectRatings(root),
            TrailerUrl:     FirstExtended(root, ext => TryGetString(ext, "trailer")),
            ExternalIds:    CollectExternalIds(root),
            Artwork:        CollectArtwork(root, core),
            Collection:     collection
        );
    }

    private static ScraperShowDetailsDto BuildShowDetails(MediaItem item, List<ScraperSeasonDto> seasons)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrEmpty(item.MetadataJson) ? "{}" : item.MetadataJson);
        var root = doc.RootElement;
        var core = ParseResolvedCore(item.MetadataJson);

        return new ScraperShowDetailsDto(
            Title:      core?.Title ?? item.Name,
            Overview:   core?.Overview ?? item.Overview,
            Tagline:    FirstExtended(root, ext => TryGetString(ext, "tagline")),
            Year:       core?.Year ?? item.Year,
            Premiered:  FirstExtended(root, ext => TryGetString(ext, "first_aired") ?? TryGetString(ext, "released")),
            Mpaa:       FirstExtended(root, ext => TryGetString(ext, "certification")),
            Country:    FirstExtended(root, ext => TryGetString(ext, "country")),
            Studio:     FirstExtended(root, ext => TryGetString(ext, "network")),
            Status:     FirstExtended(root, ext => TryGetString(ext, "status")),
            Genres:     core?.Genres,
            Cast:       core?.Cast,
            Tags:       core?.Tags,
            Ratings:    CollectRatings(root),
            TrailerUrl: FirstExtended(root, ext => TryGetString(ext, "trailer")),
            ExternalIds: CollectExternalIds(root),
            Artwork:    CollectArtwork(root, core),
            Seasons:    seasons
        );
    }

    private static ScraperEpisodeDetailsDto BuildEpisodeDetails(MediaItem item, int season)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrEmpty(item.MetadataJson) ? "{}" : item.MetadataJson);
        var root = doc.RootElement;
        var core = ParseResolvedCore(item.MetadataJson);

        return new ScraperEpisodeDetailsDto(
            Title:       core?.Title ?? item.Name,
            Overview:    core?.Overview ?? item.Overview,
            Season:      season,
            Episode:     item.Number ?? 0,
            Year:        core?.Year ?? item.Year,
            Cast:        core?.Cast,
            Directors:   core?.Directors,
            Ratings:     CollectRatings(root),
            ThumbUrl:    core?.PosterUrl ?? item.PosterUrl,
            ExternalIds: CollectExternalIds(root)
        );
    }

    // ── Cross-provider aggregation ───────────────────────────────────────────
    // Every chronicle.plugin.* partition in MetadataJson is a candidate. Fields are
    // taken from the first partition that has them, in whatever order the providers
    // were actually written (itself already governed by Chronicle's own configured
    // plugin priority) -- never a fixed provider name hardcoded here.

    private static IEnumerable<JsonElement> GetProviderPartitions(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) yield break;
        foreach (var prop in root.EnumerateObject())
            if (prop.Name.StartsWith("chronicle.plugin.", StringComparison.Ordinal) && prop.Value.ValueKind == JsonValueKind.Object)
                yield return prop.Value;
    }

    /// <summary>Tries an extractor against each partition's "extendedData" object, first non-null wins.</summary>
    private static string? FirstExtended(JsonElement root, Func<JsonElement, string?> extract)
    {
        foreach (var partition in GetProviderPartitions(root))
        {
            if (partition.TryGetProperty("extendedData", out var ext) && ext.ValueKind == JsonValueKind.Object)
            {
                var value = extract(ext);
                if (!string.IsNullOrEmpty(value)) return value;
            }
        }
        return null;
    }

    private static ScraperExternalIdsDto? CollectExternalIds(JsonElement root)
    {
        string? imdb = null, tvdb = null, tmdb = null, trakt = null;
        foreach (var partition in GetProviderPartitions(root))
        {
            if (!partition.TryGetProperty("extendedData", out var ext) || ext.ValueKind != JsonValueKind.Object)
                continue;

            JsonElement idsSource = ext;
            if (ext.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Object)
                idsSource = ids;

            imdb  ??= TryGetStringOrNumber(idsSource, "imdb");
            tvdb  ??= TryGetStringOrNumber(idsSource, "tvdb");
            tmdb  ??= TryGetStringOrNumber(idsSource, "tmdb") ?? TryGetStringOrNumber(ext, "tmdbId");
            trakt ??= TryGetStringOrNumber(idsSource, "trakt");
        }

        return (imdb ?? tvdb ?? tmdb ?? trakt) is null ? null : new ScraperExternalIdsDto(imdb, tvdb, tmdb, trakt);
    }

    /// <summary>Every partition's own rating, keyed by that partition's reported "source" name.</summary>
    private static Dictionary<string, ScraperRatingDto>? CollectRatings(JsonElement root)
    {
        Dictionary<string, ScraperRatingDto>? ratings = null;
        foreach (var partition in GetProviderPartitions(root))
        {
            var rating = TryGetDouble(partition, "rating");
            if (rating is null || rating <= 0) continue;

            var source = TryGetString(partition, "source") ?? "chronicle";
            ratings ??= new Dictionary<string, ScraperRatingDto>();
            ratings[source] = new ScraperRatingDto(rating.Value, TryGetInt(partition, "votes"));
        }
        return ratings;
    }

    private static readonly (string Field, string ArtType)[] ArtworkFieldMap =
    [
        ("posterUrl",      "poster"),
        ("backdropUrl",    "fanart"),
        ("logoUrl",        "clearlogo"),
        ("bannerUrl",      "banner"),
        ("clearartUrl",    "clearart"),
        ("discUrl",        "discart"),
        ("characterArtUrl","characterart"),
    ];

    /// <summary>Every distinct non-null image per art type across every provider partition, tagged by source.</summary>
    private static Dictionary<string, List<ScraperArtworkCandidateDto>>? CollectArtwork(JsonElement root, ResolvedCore? core)
    {
        var result = new Dictionary<string, List<ScraperArtworkCandidateDto>>();
        var seen = new HashSet<string>();

        void Add(string artType, string? url, string source)
        {
            if (string.IsNullOrEmpty(url) || !seen.Add(artType + "|" + url)) return;
            if (!result.TryGetValue(artType, out var list))
                result[artType] = list = [];
            list.Add(new ScraperArtworkCandidateDto(url, source));
        }

        // Chronicle's own authoritative pick goes first so it's the default Kodi selects.
        if (core is not null)
        {
            Add("poster", core.PosterUrl, "chronicle");
            Add("fanart", core.BackdropUrl, "chronicle");
            Add("clearlogo", core.LogoUrl, "chronicle");
            Add("banner", core.BannerUrl, "chronicle");
            Add("clearart", core.ClearartUrl, "chronicle");
            Add("discart", core.DiscUrl, "chronicle");
            Add("characterart", core.CharacterArtUrl, "chronicle");
        }

        foreach (var partition in GetProviderPartitions(root))
        {
            var source = TryGetString(partition, "source") ?? "unknown";
            foreach (var (field, artType) in ArtworkFieldMap)
                Add(artType, TryGetString(partition, field), source);
        }

        return result.Count > 0 ? result : null;
    }

    // ── "_resolved" core parsing (unchanged shape used elsewhere in the API) ────

    private sealed record ResolvedCore(
        string? Title, string? Overview, int? Year, string? PosterUrl, string? BackdropUrl,
        int? RuntimeMinutes, double? Rating, List<string>? Genres, List<string>? Cast,
        List<string>? Directors, List<string>? Tags, string? LogoUrl, string? BannerUrl,
        string? ClearartUrl, string? DiscUrl, string? CharacterArtUrl);

    private static ResolvedCore? ParseResolvedCore(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (!doc.RootElement.TryGetProperty("_resolved", out var r) || r.ValueKind != JsonValueKind.Object)
                return null;

            return new ResolvedCore(
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
                DiscUrl:        TryGetString(r, "discUrl"),
                CharacterArtUrl: TryGetString(r, "characterArtUrl")
            );
        }
        catch { return null; }
    }

    // ── JSON helpers ─────────────────────────────────────────────────────────

    private static string? TryGetString(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? TryGetStringOrNumber(JsonElement e, string prop)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            _ => null
        };
    }

    private static int? TryGetInt(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static double? TryGetDouble(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static List<string>? TryGetStringList(JsonElement e, string prop)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString()!);
        return list.Count > 0 ? list : null;
    }
}
