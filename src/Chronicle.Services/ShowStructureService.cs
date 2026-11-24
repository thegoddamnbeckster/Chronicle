using System.Text.Json;
using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// Builds a show's seasons and episodes from its metadata providers. Shared by the Kodi scraper (which asks for a
/// show's episode guide) and by adding a show by hand, where the rule is that everything beneath what the user adds
/// is added with it. Existing seasons and episodes are never touched, only the missing ones are created.
/// </summary>
public interface IShowStructureService
{
    Task EnsureEpisodesResolvedAsync(MediaItem show, CancellationToken ct);
}

public sealed class ShowStructureService : IShowStructureService
{
    private readonly ChronicleDbContext _db;
    private readonly IMediaService _mediaService;
    private readonly IPluginRegistry _registry;
    private readonly EpisodeTopUpThrottle _episodeThrottle;
    private readonly ILogger<ShowStructureService> _log;

    public ShowStructureService(ChronicleDbContext db, IMediaService mediaService, IPluginRegistry registry,
        EpisodeTopUpThrottle episodeThrottle, ILogger<ShowStructureService> log)
    {
        _db = db; _mediaService = mediaService; _registry = registry; _episodeThrottle = episodeThrottle; _log = log;
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
    /// aired and landed in a folder no scan folder covers yet. Every season is re-checked
    /// against the provider on every call and topped up with any episode NUMBER it doesn't
    /// already have -- never touching/duplicating the ones it does, and never re-creating a
    /// season container that already exists (see the season-container lookup below).
    ///
    /// Root-caused live (2026-09-12): before this, "already has local episodes" meant
    /// "permanently done, never check again" -- meaning a CURRENTLY AIRING show could never
    /// learn about a newly released episode through this path either, no matter how many
    /// times Kodi asked, since this endpoint backs Kodi's getepisodelist/getepisodedetails
    /// contract directly. A user would have needed the show fully removed and rescanned from
    /// scratch every time a new episode dropped -- exactly the "why do I have to nuke the
    /// whole library for one new episode" complaint this fixes. Deliberately NOT special-
    /// cased to just the latest season (per-user direction, 2026-09-12: "just get any
    /// episodes that aren't in Kodi yet") -- every known season is re-checked, not only the
    /// one most likely to still be airing.
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
    public async Task EnsureEpisodesResolvedAsync(MediaItem show, CancellationToken ct)
    {
        var candidates = CollectProviderExternalIds(show.MetadataJson);
        if (candidates.Count == 0) return;
        if (_episodeThrottle.IsFresh(show.Id)) return;   // looked at the provider within the last hour

        var showLock = _episodeResolutionLocks.GetOrAdd(show.Id, _ => new SemaphoreSlim(1, 1));
        await showLock.WaitAsync(ct);
        try
        {
            // A request that queued behind another for the same show finds the work already done.
            if (_episodeThrottle.IsFresh(show.Id)) return;
            await ResolveEpisodesLockedAsync(show, candidates, ct);
            _episodeThrottle.MarkChecked(show.Id);
        }
        catch (Exception ex)
        {
            // Degrade gracefully: this endpoint used to be a pure local read that could
            // never fail except for a missing show. A provider timeout/rate-limit/bug must
            // not turn Kodi's getepisodelist call into a 500 -- return whatever local data
            // already exists (possibly none) instead.
            _log.LogWarning(ex,
                "show-structure: episode resolution failed for show {ShowId}, returning local data only",
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
        // Every season MediaItem this show already has (regardless of episode count) -- looked
        // up once so a season container is always REUSED below, never re-created. CreateAsync
        // has no find-or-create check of its own (see MediaService.CreateAsync); the season
        // create call further down used to run unconditionally for any seasonNum not in the old
        // "has episodes" set, which could just as easily have been an EMPTY season container
        // left over from an earlier interrupted attempt -- the exact "Season 4"/"Season 04"
        // sibling-duplication bug already root-caused elsewhere today (FileScanService's
        // UpsertGroupItemAsync), just via this endpoint's own separate code path instead.
        var seasonRows = await _db.MediaItems
            .Where(s => s.ParentId == show.Id && s.HierarchyLevel == 1 && s.Number != null)
            .ToListAsync(ct);
        var seasonIds = seasonRows.Select(s => s.Id).ToList();

        // Every episode number Chronicle already has, per season number -- a season row with a
        // null Number (shouldn't normally happen, but not impossible from a legacy import path)
        // is never treated as "known" for any season number, especially not season 0
        // (Specials), which a naive `Number ?? 0` would silently and permanently block.
        var episodeNumbersBySeasonId = (await _db.MediaItems
                .Where(e => e.HierarchyLevel == 2 && e.ParentId != null && seasonIds.Contains(e.ParentId.Value))
                .Select(e => new { e.ParentId, e.Number })
                .ToListAsync(ct))
            .Where(e => e.Number.HasValue)
            .GroupBy(e => e.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Number!.Value).ToHashSet());

        // Tolerates two rows sharing a season number (see TvSeasonIndex) -- a plain
        // ToDictionary here threw on every request for a show in that state.
        var seasonIndex = TvSeasonIndex.Build(seasonRows, episodeNumbersBySeasonId);
        if (seasonIndex.DuplicatedSeasonNumbers.Count > 0)
        {
            _log.LogWarning(
                "show-structure: show {ShowId} has more than one row for season number(s) {Seasons}; " +
                "using the oldest row of each as the container for new episodes",
                show.Id, string.Join(", ", seasonIndex.DuplicatedSeasonNumbers));
        }
        var seasonContainerByNumber = seasonIndex.ContainerByNumber.ToDictionary();
        var existingEpisodeNumbersBySeasonNumber = seasonIndex.EpisodeNumbersBySeasonNumber;

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
            var isKnownSeason = existingEpisodeNumbersBySeasonNumber.ContainsKey(seasonNum);

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
                // Only counts toward the "past this show's real season count" streak for a
                // season Chronicle had never heard of at all -- an empty/unchanged response for
                // an already-known season (the common case: nothing new in it) must not cut the
                // search for later, still-undiscovered seasons short.
                if (!isKnownSeason) consecutiveEmpty++;
                continue;
            }
            consecutiveEmpty = 0;

            var alreadyKnownNumbers = existingEpisodeNumbersBySeasonNumber.GetValueOrDefault(seasonNum);
            var newEpisodes = alreadyKnownNumbers is null
                ? episodes
                : episodes.Where(e => !alreadyKnownNumbers.Contains(e.EpisodeNumber)).ToList();
            if (newEpisodes.Count == 0) continue;

            await using (var tx = await _db.Database.BeginTransactionAsync(ct))
            {
                if (!seasonContainerByNumber.TryGetValue(seasonNum, out var season))
                {
                    season = await _mediaService.CreateAsync(new CreateMediaRequest(
                        show.MediaTypeId, show.Id, $"Season {seasonNum}", null, null, null, null,
                        HierarchyLevel: 1, Number: seasonNum), ct);
                    seasonContainerByNumber[seasonNum] = season;
                }

                foreach (var ep in newEpisodes)
                {
                    var episode = await _mediaService.CreateAsync(new CreateMediaRequest(
                        show.MediaTypeId, season.Id, ep.Title, null, ep.Overview, ep.StillUrl, null,
                        HierarchyLevel: 2, Number: ep.EpisodeNumber), ct);

                    StampProviderPartition(episode, providerPluginId!, ep, seasonNum);
                }

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }

            _log.LogInformation(
                "show-structure: resolved season {Season} of show {ShowId} ({Count} new of {Total} episode(s)) from {PluginId}",
                seasonNum, show.Id, newEpisodes.Count, episodes.Count, providerPluginId);
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
                air_date       = ReleaseYear.OrNullDate(ep.AirDate),
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

    private static string? TryGetString(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
