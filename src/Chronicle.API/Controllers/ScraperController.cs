using Chronicle.API.DTOs;
using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Services;
using Chronicle.Services.Plugins;
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
    private readonly IMovieCollectionService _collections;
    private readonly IPluginRegistry _registry;
    private readonly ILogger<ScraperController> _logger;

    public ScraperController(ChronicleDbContext context, IMediaService mediaService,
        IMetadataEnrichmentService enrichment, IMovieCollectionService collections,
        IPluginRegistry registry, ILogger<ScraperController> logger)
    {
        _context    = context;
        _mediaService = mediaService;
        _enrichment = enrichment;
        _collections = collections;
        _registry   = registry;
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
    public async Task<IActionResult> SearchMovies(
        [FromQuery] string? title, [FromQuery] int? year, [FromQuery] string? fileName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title))
            return BadRequest(ApiResponse<object>.Fail("TITLE_REQUIRED", "title is required."));

        var movieTypeId = await GetMediaTypeIdAsync("movies", ct);
        if (movieTypeId == 0)
            return NotFound(ApiResponse<object>.Fail("MEDIA_TYPE_NOT_FOUND", "No active 'movies' media type is configured."));

        // Search every movie-like type, not just "movies". A fan edit or anime movie is still
        // a movie file on disk, so Kodi scrapes it through this same endpoint — and if we only
        // looked at MediaTypeId == movies we'd fail to find the item the user already has and
        // mint a duplicate "movies" copy of their fan edit on every single scrape. That is
        // exactly what happened in the wild (confirmed 2026-08-07: repeated fan-edit
        // duplicates, plus surviving anime_movies/movies pairs). Creation below still uses the
        // requested "movies" type — this only widens what counts as "already have it".
        var movieLikeTypeIds = await GetMovieLikeTypeIdsAsync(ct);
        var candidates = await _context.MediaItems
            .Where(m => movieLikeTypeIds.Contains(m.MediaTypeId) && m.HierarchyLevel <= 1)
            .ToListAsync(ct);

        // Exclude collection containers from candidate matching -- a container's own Name can
        // coincidentally normalize-match a searched title (e.g. a folder literally named "John
        // Wick Collection"), and Kodi always expects a real movie's id back from this endpoint,
        // never a container's. Confirmed the underlying data can support this: containers sit at
        // HierarchyLevel 0, same as any other candidate here, so nothing else filters them out.
        var containerIds = await _collections.GetCollectionContainerIdsAsync(
            _context, candidates.Select(c => c.Id).ToList(), ct);
        if (containerIds.Count > 0)
            candidates = candidates.Where(c => !containerIds.Contains(c.Id)).ToList();

        // Confirmation by filename, tried before title matching: title-token matching only
        // finds an existing item when the title Kodi derived from the folder name happens to
        // agree with Chronicle's own stored title -- it has no way to know "Alien - Derelict"
        // and "Derelict" are the same physical file. A verified filename sidesteps that
        // entirely: if any existing candidate's own recorded file (fileScanner.filePaths, or a
        // prior scrape's reported KnownFileName) has this exact basename, it IS this movie,
        // regardless of what title mismatch would otherwise have missed it. This is what
        // closes the gap that let a fan edit spawn a second, wrongly-typed, posterless
        // duplicate of itself on every scrape (confirmed live 2026-08-20: items 487715-487718).
        MediaItem? existing = null;
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            existing = candidates.FirstOrDefault(c =>
                string.Equals(TryGetScannedFileName(c.MetadataJson), fileName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                _logger.LogInformation(
                    "scraper/movies/search: title={Title} year={Year} fileName={FileName} -> matched existing item {ItemId} by filename (skipped title matching)",
                    title, year, fileName, existing.Id);
        }

        existing ??= FindByNormalizedTitle(candidates, title, year);

        var item = await ResolveOrCreateAsync(existing, movieTypeId, title, year, fileName, ct);
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

    /// <summary>
    /// Chronicle_Scraper calls this after it resolves a movie's real folder/file the "slow"
    /// way -- title+year matching against Kodi's own VideoLibrary or source browsing --
    /// because this item has no fileScanner record for KnownFileName to have short-circuited
    /// that search. Persists the discovered filename so it becomes a verified, known fact for
    /// every future request: the fuzzy fallback only ever has to run once per item, not on
    /// every single scrape. Deliberately a separate "scraperResolvedFile" key rather than
    /// writing into "fileScanner" itself -- that key carries broader meaning elsewhere
    /// (MetadataContributionService, LibraryService's hierarchy-import detection) that a
    /// scraper-side discovery shouldn't masquerade as.
    /// </summary>
    public sealed record ResolvedFileRequest(string FileName);

    [HttpPost("movies/{id:int}/resolved-file")]
    public async Task<IActionResult> ReportResolvedFile(int id, [FromBody] ResolvedFileRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.FileName))
            return BadRequest(ApiResponse<object>.Fail("FILENAME_REQUIRED", "fileName is required."));

        var item = await _context.MediaItems.FindAsync([id], ct);
        if (item is null)
            return NotFound(ApiResponse<object>.Fail("MEDIA_NOT_FOUND", $"Media item {id} not found."));

        SetScraperResolvedFile(item, request.FileName);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "scraper/movies/{ItemId}/resolved-file: recorded {FileName} -- future requests won't need to re-search for this item",
            id, request.FileName);
        return Ok(ApiResponse<object>.Ok(new { }));
    }

    /// <summary>
    /// Stamps the "scraperResolvedFile" partition onto an item's MetadataJson -- the single
    /// place that shape gets written, shared by ReportResolvedFile (an existing item, reported
    /// back after the fact) and ResolveOrCreateAsync (a brand-new item, stamped immediately with
    /// whatever filename the search request already carried). Does not save -- the caller
    /// controls when/whether to commit.
    /// </summary>
    private static void SetScraperResolvedFile(MediaItem item, string fileName)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(
            string.IsNullOrEmpty(item.MetadataJson) ? "{}" : item.MetadataJson)!.AsObject();
        root["scraperResolvedFile"] = new System.Text.Json.Nodes.JsonObject
        {
            ["fileName"]   = fileName,
            ["resolvedAt"] = DateTime.UtcNow.ToString("O"),
        };
        item.MetadataJson = root.ToJsonString();
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
                    // permanently blank, fall back to the first member's own poster (same rule,
                    // same order, the web collection page now also falls back with -- see
                    // IMovieCollectionService.GetFallbackPosterAsync) -- better than nothing, and
                    // a common convention other media managers already use. Deliberately no
                    // ownership filter: a collection member without a local file is exactly as
                    // eligible a poster source as one you have -- the goal is always showing a
                    // poster where one exists, not gating it on file ownership.
                    collectionPoster = await _collections.GetFallbackPosterAsync(_context, parent.Id, ct);
                    usedFallback = collectionPoster is not null;
                }

                collection = new ScraperCollectionDto(
                    parent.Id,
                    parentResolved?.Title ?? parent.Name,
                    parentResolved?.Overview ?? parent.Overview,
                    collectionPoster,
                    parentResolved?.BackdropUrl,
                    // Kodi's set folder accepts all of these; anything omitted here simply
                    // cannot reach Kodi, since movie sets have no scraper hook of their own.
                    LogoUrl:     parentResolved?.LogoUrl,
                    BannerUrl:   parentResolved?.BannerUrl,
                    ClearartUrl: parentResolved?.ClearartUrl,
                    DiscUrl:     parentResolved?.DiscUrl,
                    ThumbUrl:    parentResolved?.ThumbUrl,
                    PinnedSlots: ParsePinnedSlots(parent.MetadataJson));

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

        // Same widening as movies/search above: an anime series is scraped through Kodi's TV
        // path, so restricting the lookup to MediaTypeId == tv would create a duplicate "tv"
        // copy of a show the user already has filed under anime.
        var showLikeTypeIds = await GetShowLikeTypeIdsAsync(ct);
        var showCandidates = await _context.MediaItems
            .Where(m => showLikeTypeIds.Contains(m.MediaTypeId) && m.HierarchyLevel == 0)
            .ToListAsync(ct);
        var item = FindByNormalizedTitle(showCandidates, title, year);

        // TV shows have no filename-confirmation signal from Kodi's find step (that feature
        // is movies-only, per SearchMovies's fileName parameter) -- explicit null, not an
        // oversight.
        item = await ResolveOrCreateAsync(item, tvTypeId, title, year, fileName: null, ct);
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
    /// Kodi's "getepisodelist" step: every episode for this show, walking through season
    /// containers when present (see class remarks on the one show in dev data where
    /// episodes attach directly to the show, skipping the season level).
    ///
    /// If Chronicle has no local episode records yet, resolves them on the spot from
    /// whichever configured metadata provider has an external id for this show (see
    /// EnsureEpisodesResolvedAsync) -- mirroring the same resolve-or-create pattern
    /// SearchShows/SearchMovies already use for the show/movie itself, rather than
    /// requiring Chronicle's separate file-scanner/import pipeline to have already run
    /// first. A file scanner import still wins if it got there first (existing episodes
    /// are never touched here), and still supplies the richer per-file data (exact
    /// filename, stream details) that only scanning a real file on disk can provide --
    /// this only fills the gap when nothing has scanned this show's files yet, so Kodi
    /// still gets a real episode list to match its local files against immediately.
    /// </summary>
    [HttpGet("tv/episodes")]
    public async Task<IActionResult> GetEpisodes([FromQuery] int showId, CancellationToken ct)
    {
        var show = await _context.MediaItems.FindAsync([showId], ct);
        if (show is null)
            return NotFound(ApiResponse<object>.Fail("MEDIA_NOT_FOUND", $"Media item {showId} not found."));

        await EnsureEpisodesResolvedAsync(show, ct);

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

    /// <summary>camelCase to match every "chronicle.plugin.*" partition already written
    /// elsewhere (see MetadataEnrichmentService.MetadataBlobOptions, which this mirrors --
    /// that one is internal to Chronicle.Services and not visible from this assembly).</summary>
    private static readonly JsonSerializerOptions ScraperMetadataBlobOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>One lock per show id, so two concurrent requests for the same show can't
    /// both see the same missing season and both create it. Scoped to this process only
    /// (adequate for Chronicle's single-API-instance deployment model) -- see
    /// EnsureEpisodesResolvedAsync. Mirrors MetadataEnrichmentService's own per-plugin
    /// SemaphoreSlim dictionary pattern.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim>
        _episodeResolutionLocks = new();

    /// <summary>
    /// Fetches this show's episode guide directly from whichever configured metadata
    /// provider has an external id for it, and creates the Season/Episode hierarchy for
    /// any season Chronicle doesn't already have local episodes for -- Chronicle doesn't
    /// need its own file scanner to have already found these exact files first; the
    /// provider's own episode guide is the source of truth Kodi's local file matching (by
    /// season/episode number) needs to work against.
    ///
    /// Checked PER SEASON, not per-show: a show can have some seasons from a real file
    /// scan (with per-file data a scan alone can supply, like exact filenames) and be
    /// completely missing others -- e.g. only season 1 was ever scanned before season 2
    /// aired and landed in a folder no scan folder covers yet. A season that already has
    /// local episodes (from either this method or a file-scanner import) is left untouched.
    ///
    /// Tries providers in the order their external ids appear on the show's MetadataJson,
    /// NOT Chronicle's configured per-field resolution priority (MetadataResolutionService)
    /// -- that priority is about which provider's VALUE wins for an already-resolved field,
    /// not which provider order to attempt an episode-list fetch in, and wiring the two
    /// together is future work. Not every provider implements GetEpisodeListAsync (default
    /// is "unsupported, return empty"); the first one that actually returns a season's
    /// worth of episodes wins, and every subsequent season for this show uses that same
    /// provider for consistency. Stops after several consecutive provider-queried (not
    /// skipped-because-already-local) seasons come back empty -- past a show's real season
    /// count, not a failure.
    ///
    /// Each season is created inside its own transaction, so a request cancelled or a
    /// provider that throws mid-season rolls that season back entirely rather than leaving
    /// a half-populated season permanently "resolved" (only episode EXISTENCE, not
    /// completeness, is what marks a season as done). A per-show lock serializes concurrent
    /// callers so two overlapping requests for the same show can't both create the same
    /// season. Provider failures are caught and logged -- this endpoint degrades to
    /// whatever local data already exists rather than turning what used to be a pure DB
    /// read into a 500.
    /// </summary>
    private async Task EnsureEpisodesResolvedAsync(MediaItem show, CancellationToken ct)
    {
        var candidates = CollectProviderExternalIds(show.MetadataJson);
        if (candidates.Count == 0) return;

        var showLock = _episodeResolutionLocks.GetOrAdd(show.Id, _ => new SemaphoreSlim(1, 1));
        await showLock.WaitAsync(ct);
        try
        {
            await ResolveEpisodesLockedAsync(show, candidates, ct);
        }
        catch (Exception ex)
        {
            // Degrade gracefully: this endpoint used to be a pure local read that could
            // never fail except for a missing show. A provider timeout/rate-limit/bug must
            // not turn Kodi's getepisodelist call into a 500 -- return whatever local data
            // already exists (possibly none) instead.
            _logger.LogWarning(ex,
                "scraper/tv/episodes: episode resolution failed for show {ShowId}, returning local data only",
                show.Id);
        }
        finally
        {
            showLock.Release();
        }
    }

    private async Task ResolveEpisodesLockedAsync(
        MediaItem show, List<(string PluginId, string ExternalId)> candidates, CancellationToken ct)
    {
        // Seasons that already have at least one local episode with a KNOWN number -- left
        // alone regardless of source, so a real file-scanner import always wins for the
        // season it actually covers. A season row with a null Number (shouldn't normally
        // happen, but not impossible from a legacy import path) is never treated as
        // "resolved" for any season number -- especially not season 0 (Specials), which a
        // naive `Number ?? 0` would silently and permanently block.
        var existingSeasonNumbers = (await _context.MediaItems
            .Where(s => s.ParentId == show.Id && s.HierarchyLevel == 1 && s.Number != null)
            .Where(s => _context.MediaItems.Any(e => e.ParentId == s.Id && e.HierarchyLevel == 2))
            .Select(s => s.Number!.Value)
            .ToListAsync(ct))
            .ToHashSet();

        IMetadataProvider? provider = null;
        string? showExternalId = null;
        string? providerPluginId = null;

        const int maxSeasons = 60;      // safety bound, not a real-world show's actual season count
        const int emptyStreakLimit = 5; // tolerate a show that doesn't start numbering at season 1
        var consecutiveEmpty = 0;

        // Season 0 is "Specials" (Kodi's own convention for behind-the-scenes/bonus content
        // that isn't part of the numbered run) -- a real, common season, not a placeholder,
        // and some shows have nothing BUT specials scanned yet.
        for (var seasonNum = 0; seasonNum <= maxSeasons && consecutiveEmpty < emptyStreakLimit; seasonNum++)
        {
            if (existingSeasonNumbers.Contains(seasonNum))
            {
                consecutiveEmpty = 0;
                continue;
            }

            IReadOnlyList<ProviderEpisodeSummary> episodes;
            if (provider is not null)
            {
                episodes = await provider.GetEpisodeListAsync(showExternalId!, seasonNum, ct);
            }
            else
            {
                episodes = [];
                foreach (var (pluginId, externalId) in candidates)
                {
                    var candidate = _registry.GetMetadataProvider(pluginId);
                    if (candidate is null) continue;

                    var result = await candidate.GetEpisodeListAsync(externalId, seasonNum, ct);
                    if (result.Count == 0) continue;

                    provider = candidate;
                    showExternalId = externalId;
                    providerPluginId = pluginId;
                    episodes = result;
                    break;
                }
            }

            if (episodes.Count == 0)
            {
                consecutiveEmpty++;
                continue;
            }
            consecutiveEmpty = 0;

            await using (var tx = await _context.Database.BeginTransactionAsync(ct))
            {
                var season = await _mediaService.CreateAsync(new CreateMediaRequest(
                    show.MediaTypeId, show.Id, $"Season {seasonNum}", null, null, null, null,
                    HierarchyLevel: 1, Number: seasonNum), ct);

                foreach (var ep in episodes)
                {
                    var episode = await _mediaService.CreateAsync(new CreateMediaRequest(
                        show.MediaTypeId, season.Id, ep.Title, null, ep.Overview, ep.StillUrl, null,
                        HierarchyLevel: 2, Number: ep.EpisodeNumber), ct);

                    StampProviderPartition(episode, providerPluginId!, ep, seasonNum);
                }

                await _context.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }

            _logger.LogInformation(
                "scraper/tv/episodes: resolved season {Season} of show {ShowId} ({Count} episodes) from {PluginId}, no local scan required",
                seasonNum, show.Id, episodes.Count, providerPluginId);
        }
    }

    /// <summary>
    /// Stamps a "chronicle.plugin.*" MetadataJson partition on a provider-resolved episode,
    /// the same shape MetadataEnrichmentService.MergeMetadata writes for every other
    /// enriched item -- without this, GetEpisodeDetails' Cast/Crew/Ratings/ExternalIds/Aired
    /// fields (which read exclusively from provider partitions, never from plain columns)
    /// silently report nothing for every episode this method creates. Deliberately minimal:
    /// only the fields ProviderEpisodeSummary actually carries (title/overview/still/air
    /// date) are known here -- full cast/crew/external-id enrichment for these episodes is
    /// still a gap versus the search-based ResolveOrCreateAsync path, tracked separately.
    /// </summary>
    private static void StampProviderPartition(
        MediaItem episode, string pluginId, ProviderEpisodeSummary ep, int seasonNum)
    {
        var metadata = new MediaMetadata
        {
            Source      = PluginIdHelper.ToSource(pluginId),
            Title       = ep.Title,
            Overview    = ep.Overview,
            PosterUrl   = ep.StillUrl,
            ExtendedData = JsonSerializer.SerializeToElement(new
            {
                season_number  = seasonNum,
                episode_number = ep.EpisodeNumber,
                air_date       = ep.AirDate,
            }),
        };

        var partitions = string.IsNullOrEmpty(episode.MetadataJson)
            ? new Dictionary<string, JsonElement>()
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(episode.MetadataJson!) ?? [];
        partitions[pluginId] = JsonSerializer.SerializeToElement(metadata, ScraperMetadataBlobOptions);
        episode.MetadataJson = JsonSerializer.Serialize(partitions);
    }

    /// <summary>Every (pluginId, externalId) pair this item has a stored external id for, in
    /// the order they appear in MetadataJson -- see EnsureEpisodesResolvedAsync.</summary>
    private static List<(string PluginId, string ExternalId)> CollectProviderExternalIds(string? metadataJson)
    {
        var result = new List<(string, string)>();
        if (string.IsNullOrEmpty(metadataJson)) return result;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!prop.Name.StartsWith("chronicle.plugin.", StringComparison.Ordinal)) continue;
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                var extId = TryGetString(prop.Value, "externalId");
                if (!string.IsNullOrEmpty(extId)) result.Add((prop.Name, extId));
            }
        }
        catch (JsonException) { }
        return result;
    }

    /// <summary>Kodi's "getepisodedetails" step: full details for one already-known episode.</summary>
    [HttpGet("tv/episode-details")]
    public async Task<IActionResult> GetEpisodeDetails([FromQuery] int id, CancellationToken ct)
    {
        var item = await _context.MediaItems.FindAsync([id], ct);
        if (item is null)
            return NotFound(ApiResponse<object>.Fail("MEDIA_NOT_FOUND", $"Media item {id} not found."));

        var season = 1;
        string? showTitle = null;
        int? showYear = null;
        if (item.ParentId.HasValue)
        {
            var parent = await _context.MediaItems.FindAsync([item.ParentId.Value], ct);
            if (parent is not null && parent.HierarchyLevel == 1)
            {
                season = parent.Number ?? 1;
                // The show itself is one level further up -- needed so the addon can locate the
                // show's own folder on disk for this episode (Kodi's find/getepisodedetails
                // contract never hands it a file path any more than the movies one does; see
                // Chronicle_Scraper's movie_art_sync.py module docstring for the same gap on the
                // movie side). Nothing here assumes only one hierarchy shape -- if the show
                // somehow isn't found, showTitle/showYear are simply left null.
                if (parent.ParentId.HasValue)
                {
                    var show = await _context.MediaItems.FindAsync([parent.ParentId.Value], ct);
                    if (show is not null && show.HierarchyLevel == 0)
                    {
                        var showResolved = ParseResolvedCore(show.MetadataJson);
                        showTitle = showResolved?.Title ?? show.Name;
                        showYear  = showResolved?.Year ?? show.Year;
                    }
                }
            }
        }

        var dto = BuildEpisodeDetails(item, season, showTitle, showYear);
        return Ok(ApiResponse<ScraperEpisodeDetailsDto>.Ok(dto));
    }

    // ── Shared resolve-or-create ────────────────────────────────────────────

    private async Task<MediaItem?> ResolveOrCreateAsync(
        MediaItem? existing, int mediaTypeId, string title, int? year, string? fileName, CancellationToken ct)
    {
        var item = existing;
        if (item is null)
        {
            item = await _mediaService.CreateAsync(new CreateMediaRequest(
                mediaTypeId, null, title, year, null, null, null, 0, null), ct);

            // Stamp + save BEFORE enrichment, not after: a caller with a filename already knows
            // a real physical file exists for whatever's about to be created, and losing that
            // fact here is exactly what previously left a freshly-created item with no file
            // record at all until get_details() rediscovered it later via a completely separate
            // source-browsing fallback (confirmed live 2026-08-20, "Marked Men - Rule + Shaw").
            // Saved first so EnrichItemAsync's own separately-tracked DbContext instance reads
            // this back rather than racing it -- enrichment only merges its own plugin
            // partition in, it never touches unrelated keys like scraperResolvedFile.
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                SetScraperResolvedFile(item, fileName);
                await _context.SaveChangesAsync(ct);
            }

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
    private async Task<int> GetMediaTypeIdAsync(string name, CancellationToken ct) =>
        await _context.MediaTypes.Where(t => t.Name == name && t.IsActive).Select(t => t.Id).FirstOrDefaultAsync(ct);

    /// <summary>Flat, single-level types whose items are all "a movie file on disk" as far as
    /// Kodi is concerned — kept in sync with MovieCollectionService.IsMovieLikeTypeName.</summary>
    private static readonly string[] MovieLikeTypeNames = ["movies", "fanedits", "anime_movies"];

    /// <summary>Hierarchical show types scraped through Kodi's TV path.</summary>
    private static readonly string[] ShowLikeTypeNames = ["tv", "anime"];

    private Task<List<int>> GetMovieLikeTypeIdsAsync(CancellationToken ct) =>
        _context.MediaTypes.Where(t => t.IsActive && MovieLikeTypeNames.Contains(t.Name))
            .Select(t => t.Id).ToListAsync(ct);

    private Task<List<int>> GetShowLikeTypeIdsAsync(CancellationToken ct) =>
        _context.MediaTypes.Where(t => t.IsActive && ShowLikeTypeNames.Contains(t.Name))
            .Select(t => t.Id).ToListAsync(ct);

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
    ///
    /// Year matching is tiered, not exact-only -- confirmed directly (2026-08-04) that the
    /// caller's SQL query used to hard-filter candidates to `m.Year == year` BEFORE they ever
    /// reached this method, so a real, already-enriched item (e.g. "2 Lava 2 Lantula!",
    /// scanned with Year=2016 from its own file) was invisible to a search for the same movie
    /// under a different year (Kodi sends 2017, parsed from its folder name) -- title matched
    /// perfectly, but the year filter excluded it from `candidates` entirely, so a brand-new
    /// empty duplicate got created instead. Confirmed this had already happened repeatedly
    /// (three separate duplicate rows for the same movie, created on three different scrape
    /// attempts).
    ///
    /// The second tier resolves this the same way a human would: not by guessing ("years are
    /// close enough"), but by checking Chronicle's own recorded fileScanner.folderPath -- the
    /// exact folder name Chronicle's file scanner saw, which is the SAME folder name Kodi
    /// itself parses its search year from (Kodi's useFolderNames setting). If the year embedded
    /// in that recorded folder name matches what Kodi sent, this is verifiably the same file on
    /// disk, not a heuristic guess -- Kodi's search year and Chronicle's on-record folder year
    /// both trace back to the identical folder name. A year mismatch beyond what the recorded
    /// path itself confirms still refuses to match (could be a genuinely different film, e.g. a
    /// same-titled remake) and falls through to creating a new item, same as before.
    /// </summary>
    private static MediaItem? FindByNormalizedTitle(List<MediaItem> candidates, string title, int? year = null)
    {
        var target = NormalizeTitle(title);
        if (target.Length == 0) return null;

        var titleMatches = candidates.Where(m => NormalizeTitle(m.Name) == target).ToList();
        if (titleMatches.Count == 0) return null;

        static MediaItem? Richest(IEnumerable<MediaItem> pool) =>
            pool.OrderByDescending(m => m.MetadataJson?.Length ?? 0).FirstOrDefault();

        if (!year.HasValue)
            return Richest(titleMatches);

        return Richest(titleMatches.Where(m => m.Year == year))
            ?? Richest(titleMatches.Where(m => TryGetScannedFolderYear(m.MetadataJson) == year))
            ?? Richest(titleMatches.Where(m => !m.Year.HasValue && TryGetScannedFolderYear(m.MetadataJson) is null));
    }

    // Matches a trailing "(YYYY)"/"[YYYY]" in a folder name -- same convention Kodi's own
    // useFolderNames year parsing and _trailingYearRe (this file) both already rely on.
    private static readonly System.Text.RegularExpressions.Regex _folderYearRe =
        new(@"[\(\[](\d{4})[\)\]]\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Extracts the year embedded in this item's own fileScanner.folderPath, if it was ever
    /// scanned from disk -- the same folder name Kodi's search year came from, so this is a
    /// verified fact about the actual file, not a re-derived guess. Null if the item has no
    /// file-scanner record, or its folder name carries no year.
    /// </summary>
    private static int? TryGetScannedFolderYear(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (!doc.RootElement.TryGetProperty("fileScanner", out var fs) || fs.ValueKind != JsonValueKind.Object)
                return null;
            var folderPath = TryGetString(fs, "folderPath");
            if (string.IsNullOrEmpty(folderPath)) return null;
            var folderName = folderPath.TrimEnd('\\', '/').Split('\\', '/').LastOrDefault();
            if (string.IsNullOrEmpty(folderName)) return null;
            var m = _folderYearRe.Match(folderName);
            return m.Success && int.TryParse(m.Groups[1].Value, out var y) ? y : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Extracts the real video file's own basename (with extension) from this item's
    /// fileScanner.filePaths, if it was ever scanned from disk. This is what
    /// ScraperMovieDetailsDto.KnownFileName exposes to Chronicle_Scraper -- see that field's
    /// own doc comment for why a verified filename beats re-deriving the location from title
    /// and year.
    /// </summary>
    // Delegates to the single canonical reader (Chronicle.Services.Scan.FileIdentityJson) --
    // same fileScanner-then-scraperResolvedFile fallback, now shared with HasKnownFile instead
    // of two independent copies of this exact logic.
    private static string? TryGetScannedFileName(string? metadataJson) =>
        Chronicle.Services.Scan.FileIdentityJson.GetKnownFileName(metadataJson);

    // Strips a trailing "(YYYY)"/"[YYYY]" year annotation before tokenizing -- year is already
    // matched separately via the caller's candidates.Year filter, and different sources
    // (Kodi's folder name vs. a prior TMDB-sourced stub) don't consistently fold it into the
    // title string itself.
    private static readonly System.Text.RegularExpressions.Regex _trailingYearRe =
        new(@"\s*[\(\[]\d{4}[\)\]]\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Tokenizes into words, sorts them, and rejoins -- makes the match independent of word
    /// ORDER as well as punctuation/case. Confirmed necessary directly (2026-08-03): the same
    /// real movie existed in the library as both "Jason Lives - Friday the 13th Part VI" (an
    /// earlier TMDB-collection-sourced stub) and "Friday the 13th Part VI - Jason Lives (1986)"
    /// (a later file-scanner import using a differently-ordered folder name). The previous
    /// concatenate-and-compare version treated these as two different movies -- same letters,
    /// different order -- and silently created a duplicate instead of matching the existing
    /// item, which is exactly the kind of duplicate Kodi's own "movie set" view can't reconcile.
    /// Sorting the word tokens makes both resolve to the same signature regardless of order.
    /// </summary>
    private static string NormalizeTitle(string? text)
    {
        if (text is null) return "";
        var stripped = _trailingYearRe.Replace(text, "");
        var words = System.Text.RegularExpressions.Regex.Matches(stripped, "[A-Za-z0-9]+")
            .Select(m => m.Value.ToLowerInvariant())
            .OrderBy(w => w, StringComparer.Ordinal);
        return string.Join(' ', words);
    }

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
            Crew:           core?.Crew,
            Tags:           core?.Tags,
            Ratings:        CollectRatings(root),
            TrailerUrl:     FirstExtended(root, ext => TryGetString(ext, "trailer")),
            ExternalIds:    CollectExternalIds(root),
            Artwork:        CollectArtwork(root, core),
            Collection:     collection,
            KnownFileName:  TryGetScannedFileName(item.MetadataJson)
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
            RuntimeMinutes: core?.RuntimeMinutes,
            Genres:     core?.Genres,
            Cast:       core?.Cast,
            Crew:       core?.Crew,
            Tags:       core?.Tags,
            Ratings:    CollectRatings(root),
            TrailerUrl: FirstExtended(root, ext => TryGetString(ext, "trailer")),
            ExternalIds: CollectExternalIds(root),
            Artwork:    CollectArtwork(root, core),
            Seasons:    seasons
        );
    }

    private static ScraperEpisodeDetailsDto BuildEpisodeDetails(MediaItem item, int season, string? showTitle, int? showYear)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrEmpty(item.MetadataJson) ? "{}" : item.MetadataJson);
        var root = doc.RootElement;
        var core = ParseResolvedCore(item.MetadataJson);

        return new ScraperEpisodeDetailsDto(
            Title:          core?.Title ?? item.Name,
            Overview:       core?.Overview ?? item.Overview,
            Season:         season,
            Episode:        item.Number ?? 0,
            Year:           core?.Year ?? item.Year,
            Aired:          FirstExtended(root, ext => TryGetString(ext, "air_date") ?? TryGetString(ext, "aired") ?? TryGetString(ext, "released")),
            RuntimeMinutes: core?.RuntimeMinutes,
            Cast:           core?.Cast,
            Crew:           core?.Crew,
            Ratings:        CollectRatings(root),
            ThumbUrl:       core?.PosterUrl ?? item.PosterUrl,
            ExternalIds:    CollectExternalIds(root),
            ShowTitle:      showTitle,
            ShowYear:       showYear
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

            // Lossless ingestion (see Chronicle/CLAUDE.md): a provider's single first-class
            // field per type (posterUrl, backdropUrl, ...) is only ever its own top pick --
            // AdditionalImages is where a provider like Fanart.tv preserves every OTHER
            // candidate it actually has for that type. Surface those here too, tagged with
            // the same art-type strings, so Kodi's "Choose Art" picker sees the full set
            // this provider returned, not just the one entry ArtworkFieldMap covers.
            if (partition.TryGetProperty("additionalImages", out var images) && images.ValueKind == JsonValueKind.Array)
            {
                foreach (var img in images.EnumerateArray())
                {
                    var artType = TryGetString(img, "type");
                    var url     = TryGetString(img, "url");
                    if (!string.IsNullOrEmpty(artType) && !string.IsNullOrEmpty(url))
                        Add(artType, url, source);
                }
            }
        }

        return result.Count > 0 ? result : null;
    }

    // ── "_resolved" core parsing (unchanged shape used elsewhere in the API) ────

    private sealed record ResolvedCore(
        string? Title, string? Overview, int? Year, string? PosterUrl, string? BackdropUrl,
        int? RuntimeMinutes, double? Rating, List<string>? Genres, List<CastMemberDto>? Cast,
        List<CrewMemberDto>? Crew, List<string>? Tags, string? LogoUrl, string? BannerUrl,
        string? ClearartUrl, string? DiscUrl, string? CharacterArtUrl, string? ThumbUrl);

    /// <summary>
    /// Which artwork slots the user has explicitly pinned, read from the reserved
    /// <c>_overrides</c> key. Lets the Kodi addon tell "Chronicle happened to resolve this"
    /// from "the user chose this" — only the latter earns the right to overwrite a local file.
    /// </summary>
    private static IReadOnlyList<string> ParsePinnedSlots(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson)) return [];
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (!doc.RootElement.TryGetProperty("_overrides", out var o) || o.ValueKind != JsonValueKind.Object)
                return [];
            return [.. o.EnumerateObject().Select(p => p.Name)];
        }
        catch (JsonException)
        {
            return [];
        }
    }

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
                Cast:           TryGetCastList(r, "cast"),
                Crew:           TryGetCrewList(r, "crew"),
                Tags:           TryGetStringList(r, "tags"),
                LogoUrl:        TryGetString(r, "logoUrl"),
                BannerUrl:      TryGetString(r, "bannerUrl"),
                ClearartUrl:    TryGetString(r, "clearartUrl"),
                DiscUrl:        TryGetString(r, "discUrl"),
                CharacterArtUrl: TryGetString(r, "characterArtUrl"),
                ThumbUrl:       TryGetString(r, "thumbUrl")
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

    /// <summary>Parses a "cast" array of {name, role} objects -- written by CastMember's
    /// JsonPropertyName-attributed serialization (see Chronicle.Plugins.Models.CastMember).</summary>
    private static List<CastMemberDto>? TryGetCastList(JsonElement e, string prop)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var list = new List<CastMemberDto>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var name = TryGetString(item, "name");
            if (string.IsNullOrEmpty(name)) continue;
            list.Add(new CastMemberDto(name, TryGetString(item, "role")));
        }
        return list.Count > 0 ? list : null;
    }

    /// <summary>Parses a "crew" array of {name, job} objects -- written by CrewMember's
    /// JsonPropertyName-attributed serialization (see Chronicle.Plugins.Models.CrewMember).</summary>
    private static List<CrewMemberDto>? TryGetCrewList(JsonElement e, string prop)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var list = new List<CrewMemberDto>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var name = TryGetString(item, "name");
            if (string.IsNullOrEmpty(name)) continue;
            list.Add(new CrewMemberDto(name, TryGetString(item, "job")));
        }
        return list.Count > 0 ? list : null;
    }
}
