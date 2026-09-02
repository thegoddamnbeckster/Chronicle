using System.Text.Json;
using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Services.Matching;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

public class SyncOrchestrationService : ISyncOrchestrationService
{
    private const string SyncStateKeyPrefix = "sync_state.";

    private readonly IServiceScopeFactory         _scopeFactory;
    private readonly IPluginRegistry              _registry;
    private readonly IMetadataResolutionService   _resolution;
    private readonly ILogger<SyncOrchestrationService> _log;
    private readonly IHostApplicationLifetime     _lifetime;

    public SyncOrchestrationService(
        IServiceScopeFactory scopeFactory,
        IPluginRegistry registry,
        IMetadataResolutionService resolution,
        ILogger<SyncOrchestrationService> log,
        IHostApplicationLifetime lifetime)
    {
        _scopeFactory = scopeFactory;
        _registry     = registry;
        _resolution   = resolution;
        _log          = log;
        _lifetime     = lifetime;
    }

    // Fires EnrichPendingAsync for every registered metadata provider in the background
    // so newly synced stubs are enriched without waiting for the next cron tick.
    private void TriggerEnrichmentInBackground()
    {
        var pluginIds = _registry.GetMetadataProviderEntries()
            .Select(e => e.PluginId)
            .ToList();

        foreach (var mpPluginId in pluginIds)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var svc = scope.ServiceProvider.GetRequiredService<IMetadataEnrichmentService>();
                    await svc.EnrichPendingAsync(mpPluginId, _lifetime.ApplicationStopping);
                }
                catch (OperationCanceledException)
                {
                    // Normal shutdown — not an error.
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Background enrichment after sync failed for plugin {PluginId}", mpPluginId);
                }
            });
        }
    }

    public async Task<SyncSummary> SyncAsync(
        string pluginId, bool fullSync = false, int? userId = null, CancellationToken ct = default)
    {
        var provider = _registry.GetImportProvider(pluginId)
            ?? throw new InvalidOperationException($"Plugin '{pluginId}' has no IImportProvider.");

        if (!await provider.IsAuthenticatedAsync(ct))
            throw new InvalidOperationException($"Plugin '{pluginId}' is not authenticated.");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        // Resolve the user who will own the library entries.
        // If the caller didn't supply one (e.g. background task), fall back to the first user.
        var resolvedUserId = userId
            ?? await db.Users.OrderBy(u => u.Id).Select(u => (int?)u.Id).FirstOrDefaultAsync(ct);
        if (resolvedUserId is null)
        {
            _log.LogWarning("SyncAsync: no users in DB yet — skipping library entry updates for {PluginId}", pluginId);
            resolvedUserId = 0;   // sentinel; library upserts will be skipped
        }

        var syncKey = $"{SyncStateKeyPrefix}{pluginId}.last_synced_at";
        DateTimeOffset? since = null;

        if (!fullSync)
        {
            var raw = await db.AppSettings.FindAsync([syncKey], ct);
            if (raw is not null && DateTimeOffset.TryParse(raw.Value, out var parsed))
                since = parsed;
        }

        _log.LogInformation("Starting {Mode} sync for {PluginId} (since={Since})",
            fullSync ? "full" : "delta", pluginId, since);

        var historyTask   = provider.GetWatchHistoryAsync(since, ct);
        var ratingsTask   = provider.GetRatingsAsync(ct);
        var watchlistTask = provider.GetWatchlistAsync(ct);
        var progressTask  = provider.GetPlaybackProgressAsync(ct);
        await Task.WhenAll(historyTask, ratingsTask, watchlistTask, progressTask);

        var history   = await historyTask;
        var ratings   = await ratingsTask;
        var watchlist = await watchlistTask;
        var progress  = await progressTask;

        int itemsMatched = 0, stubsCreated = 0, watchEventsAdded = 0, creditsAdded = 0, progressUpdated = 0;
        var errors = new List<string>();

        foreach (var evt in history)
        {
            try
            {
                var (item, isNew) = await MatchOrCreateAsync(db, evt, pluginId, ct);
                if (isNew) stubsCreated++; else itemsMatched++;
                if (resolvedUserId > 0)
                    watchEventsAdded += await UpsertWatchEventAsync(db, item.Id, resolvedUserId.Value, evt, provider.Name, ct);

                // Library status belongs on the root show, not on individual episodes.
                var libraryItemId = item.ParentId.HasValue
                    ? await GetRootItemIdAsync(db, item, ct)
                    : item.Id;
                if (resolvedUserId > 0)
                    await UpsertLibraryStatusAsync(db, libraryItemId, resolvedUserId.Value, evt, ct);

                // Credits on new root-level items only (episodes don't expose per-episode credits).
                if (isNew && !item.ParentId.HasValue)
                    creditsAdded += await FetchAndStoreCreditsAsync(db, item.Id, evt.ExternalId, evt.MediaType, pluginId, provider, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Sync error for item {ExternalId}", evt.ExternalId);
                errors.Add($"{evt.ExternalId}: {ex.Message}");
            }
        }

        foreach (var rating in ratings)
        {
            try
            {
                if (resolvedUserId > 0)
                    await UpsertRatingAsync(db, rating, pluginId, resolvedUserId.Value, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Sync error processing rating {ExternalId}", rating.ExternalId);
                errors.Add($"rating {rating.ExternalId}: {ex.Message}");
            }
        }

        foreach (var entry in watchlist)
        {
            try
            {
                if (resolvedUserId > 0)
                    await UpsertWatchlistStatusAsync(db, entry, pluginId, resolvedUserId.Value, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Sync error processing watchlist entry {ExternalId}", entry.ExternalId);
                errors.Add($"watchlist {entry.ExternalId}: {ex.Message}");
            }
        }

        foreach (var p in progress)
        {
            try
            {
                if (resolvedUserId > 0 && await UpsertPlaybackProgressAsync(db, p, pluginId, resolvedUserId.Value, ct))
                    progressUpdated++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Sync error processing playback progress {ExternalId}", p.ExternalId);
                errors.Add($"progress {p.ExternalId}: {ex.Message}");
            }
        }

        // Persist last-synced timestamp
        var setting = await db.AppSettings.FindAsync([syncKey], ct);
        if (setting is null)
            db.AppSettings.Add(new AppSetting { Key = syncKey, Value = DateTimeOffset.UtcNow.ToString("O") });
        else
            setting.Value = DateTimeOffset.UtcNow.ToString("O");
        await db.SaveChangesAsync(ct);

        _log.LogInformation(
            "Sync complete for {PluginId}: {Matched} matched, {Created} stubs, {Events} events, {Credits} credits, {Progress} progress updates, {Errors} errors",
            pluginId, itemsMatched, stubsCreated, watchEventsAdded, creditsAdded, progressUpdated, errors.Count);

        if (stubsCreated > 0)
            TriggerEnrichmentInBackground();

        return new SyncSummary(itemsMatched, stubsCreated, watchEventsAdded, creditsAdded, errors, progressUpdated);
    }

    // ── Item matching ─────────────────────────────────────────────────────────

    internal async Task<(MediaItem item, bool isNew)> MatchOrCreateAsync(
        ChronicleDbContext db,
        ImportedWatchEvent evt,
        string pluginId,
        CancellationToken ct)
    {
        // Route TV/anime episodes to the hierarchy builder when season/episode numbers are present.
        if ((evt.MediaType == "tv_episode" || evt.MediaType == "anime_episode")
            && evt.SeasonNumber.HasValue && evt.EpisodeNumber.HasValue)
            return await MatchOrCreateEpisodeAsync(db, evt, pluginId, ct);

        // Route books/audiobooks to the Author→Series→Book hierarchy builder.
        var mappedType = MediaItemMatcher.NormalizeMediaTypeName(evt.MediaType);
        if ((mappedType == "books" || mappedType == "audiobooks") && evt.AuthorName is not null)
            return await MatchOrCreateBookAsync(db, evt, pluginId, ct);

        // 1. Own provider ExternalId match
        var byOwn = await db.MediaExternalIds
            .Where(e => e.Source == SourceFromPluginId(pluginId) && e.ExternalId == evt.ExternalId)
            .Select(e => e.MediaItemId)
            .FirstOrDefaultAsync(ct);
        if (byOwn != 0)
            return (await db.MediaItems.FindAsync([byOwn], ct)
                ?? throw new InvalidOperationException($"MediaItem {byOwn} missing"), false);

        // 2. AdditionalIds match
        foreach (var (source, extId) in evt.AdditionalIds)
        {
            var byAdditional = await db.MediaExternalIds
                .Where(e => e.Source == source && e.ExternalId == extId)
                .Select(e => e.MediaItemId)
                .FirstOrDefaultAsync(ct);
            if (byAdditional != 0)
            {
                // Graft the syncing plugin's own ExternalId so Stage 1 finds this item on the next sync.
                await GraftExternalIdAsync(db, byAdditional, pluginId, evt.ExternalId, ct);
                return (await db.MediaItems.FindAsync([byAdditional], ct)
                    ?? throw new InvalidOperationException($"MediaItem {byAdditional} missing"), false);
            }
        }

        // 3. Title + year fuzzy match — see MediaItemMatcher.FindByTitleYearAsync for the
        //    "Title"/"Title (Year)"/colon-dash-variant matching and the media-type scoping
        //    that keeps a TV show from matching a movie stub sharing the same title/year
        //    (e.g. "Star Wars: The Clone Wars (2008)" exists as both).
        if (evt.Title is not null && evt.Year.HasValue)
        {
            var mediaTypeId = await MediaItemMatcher.TryResolveMediaTypeIdForMatchAsync(db, evt.MediaType, ct);
            if (mediaTypeId.HasValue)
            {
                var byTitle = await MediaItemMatcher.FindByTitleYearAsync(db, evt.Title, evt.Year, mediaTypeId.Value, ct);
                if (byTitle is not null)
                {
                    await GraftExternalIdAsync(db, byTitle.Id, pluginId, evt.ExternalId, ct);
                    return (byTitle, false);
                }
            }
        }

        // 4. Fetch richer metadata from the provider and re-check cross-refs (Stage 4a)
        //    before creating a stub — catches the case where the item exists under a
        //    TMDB/IMDB/TVDB id that wasn't in the original watch event AdditionalIds.
        var provider = _registry.GetImportProvider(pluginId);
        ImportedItemMetadata? stageMeta = null;
        if (provider is not null)
        {
            try { stageMeta = await provider.GetItemMetadataAsync(evt.ExternalId, evt.MediaType, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "GetItemMetadataAsync (Stage 4a) failed for {Id}", evt.ExternalId); }
        }
        if (stageMeta?.AdditionalIds is not null)
        {
            foreach (var (source, extId) in stageMeta.AdditionalIds)
            {
                var byMeta = await db.MediaExternalIds
                    .Where(e => e.Source == source && e.ExternalId == extId)
                    .Select(e => e.MediaItemId)
                    .FirstOrDefaultAsync(ct);
                if (byMeta != 0)
                {
                    var found = await db.MediaItems.FindAsync([byMeta], ct)
                        ?? throw new InvalidOperationException($"MediaItem {byMeta} missing");
                    await GraftExternalIdAsync(db, found.Id, pluginId, evt.ExternalId, ct);
                    return (found, false);
                }
            }
        }

        // 4b. Create stub with the metadata already fetched
        return (await CreateStubAsync(db, evt, pluginId, stageMeta, ct), true);
    }

    private async Task<MediaItem> CreateStubAsync(
        ChronicleDbContext db,
        ImportedWatchEvent evt,
        string pluginId,
        ImportedItemMetadata? meta,
        CancellationToken ct)
    {
        var mediaTypeName = MediaItemMatcher.NormalizeMediaTypeName(evt.MediaType);
        var mediaType = await db.MediaTypes
            .FirstOrDefaultAsync(t => t.Name == mediaTypeName, ct)
            ?? throw new InvalidOperationException($"Media type '{mediaTypeName}' not found in database.");

        var stubTitle = meta?.Title ?? evt.Title ?? "Unknown";
        var stubYear  = meta?.Year  ?? evt.Year;

        // ── Race-condition guard ───────────────────────────────────────────────
        // Multiple concurrent enrichment/sync workers can reach Stage 4b
        // simultaneously (e.g. two watch events for the same film in one sync pass,
        // or a sync and an enrichment background task running in parallel). Each
        // worker passes Stages 1-3 without finding the item because the other's
        // INSERT hasn't committed yet. Do one final title+year lookup inside this
        // method before inserting so at most one stub is created.
        var normalizedTitle = MediaItemNormalizer.NormalizeName(stubTitle);
        if (!string.IsNullOrEmpty(normalizedTitle))
        {
            var existing = await db.MediaItems
                .FirstOrDefaultAsync(m => m.MediaTypeId == mediaType.Id
                                       && m.Year == stubYear
                                       && m.NormalizedName == normalizedTitle, ct);
            if (existing is not null)
            {
                // Another worker beat us to it — graft our ExternalId and return.
                await GraftExternalIdAsync(db, existing.Id, pluginId, evt.ExternalId, ct);
                return existing;
            }

            // Retry with a trailing disambiguator parenthetical stripped (e.g. "Dogma (film)"
            // -> "Dogma") -- a metadata source's own title can carry one even though the
            // already-catalogued row doesn't, which would otherwise create a duplicate stub
            // instead of matching it. See MediaItemNormalizer.StripTrailingParenthetical.
            var deparenthesized = MediaItemNormalizer.StripTrailingParenthetical(stubTitle);
            if (deparenthesized.Length > 0 && deparenthesized != stubTitle)
            {
                var deparenNormalized = MediaItemNormalizer.NormalizeName(deparenthesized);
                if (!string.IsNullOrEmpty(deparenNormalized) && deparenNormalized != normalizedTitle)
                {
                    existing = await db.MediaItems
                        .FirstOrDefaultAsync(m => m.MediaTypeId == mediaType.Id
                                               && m.Year == stubYear
                                               && m.NormalizedName == deparenNormalized, ct);
                    if (existing is not null)
                    {
                        await GraftExternalIdAsync(db, existing.Id, pluginId, evt.ExternalId, ct);
                        return existing;
                    }
                }
            }
        }

        var item = new MediaItem
        {
            Name           = stubTitle,
            Year           = stubYear,
            MediaTypeId    = mediaType.Id,
            HierarchyLevel = 0,
            NormalizedName = normalizedTitle,
            CreatedAt      = DateTime.UtcNow,
            UpdatedAt      = DateTime.UtcNow,
        };

        // Write the provider's metadata directly into metadata_json so the plugin
        // metadata box is populated immediately after sync without waiting for a
        // separate enrichment run. Set MediaType on the entity so ResolveAsync has
        // the type context it needs to apply the priority assignment config.
        item.MediaType = mediaType;
        if (meta is not null)
        {
            MergeImportedMetadata(item, pluginId, meta);
            await _resolution.ResolveAsync(item, db, ct);
        }

        db.MediaItems.Add(item);
        await db.SaveChangesAsync(ct);

        // Store all known external IDs
        var allIds = new Dictionary<string, string>(evt.AdditionalIds)
        {
            [SourceFromPluginId(pluginId)] = evt.ExternalId
        };
        if (meta?.AdditionalIds is not null)
            foreach (var (s, v) in meta.AdditionalIds)
                allIds.TryAdd(s, v);

        foreach (var (source, extId) in allIds)
            db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = item.Id, Source = source, ExternalId = extId });

        // Seed enrichment rows for all loaded metadata plugins that support this media type.
        // For the syncing plugin itself: if we already have metadata from GetItemMetadataAsync,
        // seed as Completed so the enrichment runner doesn't redundantly re-fetch it.
        foreach (var (mpPluginId, mp, _) in _registry.GetMetadataProviderEntries())
        {
            var supported = mp.GetSupportedMediaTypes()
                .Any(t => string.Equals(t.MediaTypeName, mediaTypeName, StringComparison.OrdinalIgnoreCase));
            if (!supported) continue;

            var exists = await db.MediaEnrichments
                .AnyAsync(e => e.MediaItemId == item.Id && e.PluginId == mpPluginId, ct);
            if (exists) continue;

            allIds.TryGetValue(SourceFromPluginId(mpPluginId), out var knownId);
            var alreadyEnriched = meta is not null &&
                string.Equals(mpPluginId, pluginId, StringComparison.OrdinalIgnoreCase);
            db.MediaEnrichments.Add(new MediaItemEnrichment
            {
                MediaItemId     = item.Id,
                PluginId        = mpPluginId,
                Status          = alreadyEnriched ? EnrichmentStatus.Completed : EnrichmentStatus.Pending,
                LastCompletedAt = alreadyEnriched ? DateTime.UtcNow : null,
                MaxRetries      = 3,
                ExternalId      = knownId,
            });
        }

        await db.SaveChangesAsync(ct);
        return item;
    }

    // ── TV episode hierarchy ──────────────────────────────────────────────────

    private async Task<(MediaItem episode, bool isNew)> MatchOrCreateEpisodeAsync(
        ChronicleDbContext db, ImportedWatchEvent evt, string pluginId, CancellationToken ct)
    {
        var source = SourceFromPluginId(pluginId);

        // 1. Find existing episode by its synthetic ExternalId.
        var byId = await db.MediaExternalIds
            .Where(e => e.Source == source && e.ExternalId == evt.ExternalId)
            .Select(e => e.MediaItemId)
            .FirstOrDefaultAsync(ct);
        if (byId != 0)
            return (await db.MediaItems.FindAsync([byId], ct)
                   ?? throw new InvalidOperationException($"MediaItem {byId} disappeared during sync lookup."), false);

        // 2. Find or create the parent show using show-level data.
        var showEvt = evt with
        {
            ExternalId    = evt.ShowExternalId ?? evt.ExternalId,
            MediaType     = evt.MediaType == "anime_episode" ? "anime" : "tv",
            Title         = evt.ShowTitle ?? evt.Title,
            SeasonNumber  = null,
            EpisodeNumber = null,
            ShowExternalId = null,
            ShowTitle     = null,
        };
        var (show, _) = await MatchOrCreateAsync(db, showEvt, pluginId, ct);

        // 3. Find or create the Season node.
        var season = await db.MediaItems.FirstOrDefaultAsync(
            i => i.ParentId == show.Id && i.HierarchyLevel == 1 && i.Number == evt.SeasonNumber, ct);
        if (season is null)
        {
            season = new MediaItem
            {
                Name           = $"Season {evt.SeasonNumber}",
                MediaTypeId    = show.MediaTypeId,
                ParentId       = show.Id,
                HierarchyLevel = 1,
                Number         = evt.SeasonNumber,
                CreatedAt      = DateTime.UtcNow,
                UpdatedAt      = DateTime.UtcNow,
            };
            db.MediaItems.Add(season);
            await db.SaveChangesAsync(ct);
        }

        // 4. Find or create the Episode node.
        var episode = await db.MediaItems.FirstOrDefaultAsync(
            i => i.ParentId == season.Id && i.HierarchyLevel == 2 && i.Number == evt.EpisodeNumber, ct);
        if (episode is not null)
            return (episode, false);

        episode = new MediaItem
        {
            Name           = evt.Title ?? $"S{evt.SeasonNumber:D2}E{evt.EpisodeNumber:D2}",
            Year           = evt.Year,
            MediaTypeId    = show.MediaTypeId,
            ParentId       = season.Id,
            HierarchyLevel = 2,
            Number         = evt.EpisodeNumber,
            CreatedAt      = DateTime.UtcNow,
            UpdatedAt      = DateTime.UtcNow,
        };
        db.MediaItems.Add(episode);
        await db.SaveChangesAsync(ct);

        db.MediaExternalIds.Add(new MediaExternalId
        {
            MediaItemId = episode.Id,
            Source      = source,
            ExternalId  = evt.ExternalId,
        });
        await db.SaveChangesAsync(ct);

        return (episode, true);
    }

    // ── Book/audiobook hierarchy ──────────────────────────────────────────────

    private async Task<(MediaItem item, bool isNew)> MatchOrCreateBookAsync(
        ChronicleDbContext db, ImportedWatchEvent evt, string pluginId, CancellationToken ct)
    {
        var mediaTypeName = MediaItemMatcher.NormalizeMediaTypeName(evt.MediaType);
        var mediaType = await db.MediaTypes
            .FirstOrDefaultAsync(t => t.Name == mediaTypeName, ct)
            ?? throw new InvalidOperationException($"Media type '{mediaTypeName}' not found in database.");

        // ── Level 0: Author ───────────────────────────────────────────────────
        var authorName = evt.AuthorName ?? "Unknown";
        var authorNameLower = authorName.ToLowerInvariant();
        var author = await db.MediaItems
            .FirstOrDefaultAsync(i => i.MediaTypeId == mediaType.Id
                && i.HierarchyLevel == 0
                && i.Name.ToLower() == authorNameLower
                && i.ParentId == null, ct);

        // Still no match -- try a loose, whitespace-insensitive comparison before creating a
        // new author. Root-caused a real duplicate (2026-08-31): "James S. A. Corey" vs "James
        // S.A. Corey" are the same author, spaced differently around their initials, and even
        // the case-insensitive exact check above treats them as different names.
        if (author is null)
        {
            var looseTarget = MediaItemNormalizer.NormalizeNameLoose(authorName);
            if (!string.IsNullOrEmpty(looseTarget))
            {
                var rootAuthors = await db.MediaItems
                    .Where(i => i.MediaTypeId == mediaType.Id && i.HierarchyLevel == 0 && i.ParentId == null)
                    .ToListAsync(ct);
                author = rootAuthors.FirstOrDefault(
                    i => MediaItemNormalizer.NormalizeNameLoose(i.Name) == looseTarget);
            }
        }
        if (author is null)
        {
            author = new MediaItem
            {
                Name           = authorName,
                MediaTypeId    = mediaType.Id,
                HierarchyLevel = 0,
                CreatedAt      = DateTime.UtcNow,
                UpdatedAt      = DateTime.UtcNow,
            };
            db.MediaItems.Add(author);
            await db.SaveChangesAsync(ct);
        }

        // ── Level 1: Series (optional) ────────────────────────────────────────
        MediaItem? seriesItem = null;
        if (evt.SeriesName is not null)
        {
            seriesItem = await db.MediaItems
                .FirstOrDefaultAsync(i => i.ParentId == author.Id
                    && i.HierarchyLevel == 1
                    && i.Name == evt.SeriesName, ct);
            if (seriesItem is null)
            {
                seriesItem = new MediaItem
                {
                    Name           = evt.SeriesName,
                    MediaTypeId    = mediaType.Id,
                    HierarchyLevel = 1,
                    ParentId       = author.Id,
                    CreatedAt      = DateTime.UtcNow,
                    UpdatedAt      = DateTime.UtcNow,
                };
                db.MediaItems.Add(seriesItem);
                await db.SaveChangesAsync(ct);
            }
        }

        // ── Level 2 (or 1 if standalone): Book ───────────────────────────────
        var bookParentId = seriesItem?.Id ?? author.Id;
        var bookLevel    = seriesItem is not null ? 2 : 1;

        // Stage 1: own ExternalId
        var source = SourceFromPluginId(pluginId);
        var byOwn  = await db.MediaExternalIds
            .Where(e => e.Source == source && e.ExternalId == evt.ExternalId)
            .Select(e => e.MediaItemId)
            .FirstOrDefaultAsync(ct);
        if (byOwn != 0)
            return (await db.MediaItems.FindAsync([byOwn], ct)
                   ?? throw new InvalidOperationException($"MediaItem {byOwn} disappeared during sync lookup."), false);

        // Stage 2: AdditionalIds
        foreach (var (src, extId) in evt.AdditionalIds)
        {
            var byAdditional = await db.MediaExternalIds
                .Where(e => e.Source == src && e.ExternalId == extId)
                .Select(e => e.MediaItemId)
                .FirstOrDefaultAsync(ct);
            if (byAdditional != 0)
            {
                await GraftExternalIdAsync(db, byAdditional, pluginId, evt.ExternalId, ct);
                return (await db.MediaItems.FindAsync([byAdditional], ct)
                       ?? throw new InvalidOperationException($"MediaItem {byAdditional} disappeared during sync lookup."), false);
            }
        }

        // Stage 3: title + year under the resolved parent
        if (evt.Title is not null && evt.Year.HasValue)
        {
            var byTitle = await db.MediaItems
                .FirstOrDefaultAsync(i => i.ParentId == bookParentId
                    && i.Year == evt.Year
                    && i.Name == evt.Title, ct);
            if (byTitle is not null)
            {
                await GraftExternalIdAsync(db, byTitle.Id, pluginId, evt.ExternalId, ct);
                return (byTitle, false);
            }
        }

        // Stage 4: create stub
        var stubTitle = evt.Title ?? "Unknown";
        var stub = new MediaItem
        {
            Name           = evt.Year.HasValue ? $"{stubTitle} ({evt.Year})" : stubTitle,
            Year           = evt.Year,
            MediaTypeId    = mediaType.Id,
            HierarchyLevel = bookLevel,
            ParentId       = bookParentId,
            CreatedAt      = DateTime.UtcNow,
            UpdatedAt      = DateTime.UtcNow,
        };
        db.MediaItems.Add(stub);
        await db.SaveChangesAsync(ct);

        db.MediaExternalIds.Add(new MediaExternalId
        {
            MediaItemId = stub.Id,
            Source      = source,
            ExternalId  = evt.ExternalId,
        });
        foreach (var (s, v) in evt.AdditionalIds)
            db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = stub.Id, Source = s, ExternalId = v });

        // Seed enrichment rows for all metadata plugins supporting this media type
        foreach (var (mpPluginId, mp, _) in _registry.GetMetadataProviderEntries())
        {
            var supported = mp.GetSupportedMediaTypes()
                .Any(t => string.Equals(t.MediaTypeName, mediaTypeName, StringComparison.OrdinalIgnoreCase));
            if (!supported) continue;
            var exists = await db.MediaEnrichments
                .AnyAsync(e => e.MediaItemId == stub.Id && e.PluginId == mpPluginId, ct);
            if (exists) continue;
            db.MediaEnrichments.Add(new MediaItemEnrichment
            {
                MediaItemId = stub.Id,
                PluginId    = mpPluginId,
                Status      = EnrichmentStatus.Pending,
                MaxRetries  = 3,
            });
        }
        await db.SaveChangesAsync(ct);

        return (stub, true);
    }

    private static async Task<int> GetRootItemIdAsync(
        ChronicleDbContext db, MediaItem item, CancellationToken ct)
    {
        var current = item;
        while (current.ParentId.HasValue)
        {
            current = await db.MediaItems.FindAsync([current.ParentId.Value], ct)
                ?? throw new InvalidOperationException($"Parent {current.ParentId} not found");
        }
        return current.Id;
    }

    // ── Watch event ───────────────────────────────────────────────────────────

    private static async Task<int> UpsertWatchEventAsync(
        ChronicleDbContext db, int mediaItemId, int userId, ImportedWatchEvent evt, string sourceName, CancellationToken ct)
    {
        var ts = evt.WatchedAt.UtcDateTime;

        // Approximate timestamps (source gave no real per-item time, so we fell back to
        // "now") are a fresh value on every sync run and can never match by exact equality.
        // Treat "any event already recorded for this item" as the dedup key instead, or a
        // daily sync would insert a new fake "just watched" event forever.
        var exists = evt.WatchedAtIsApproximate
            ? await db.InteractionEvents
                .AnyAsync(e => e.UserId == userId && e.MediaItemId == mediaItemId, ct)
            : await db.InteractionEvents
                .AnyAsync(e => e.UserId == userId && e.MediaItemId == mediaItemId && e.Timestamp == ts, ct);
        if (exists) return 0;

        db.InteractionEvents.Add(new InteractionEvent
        {
            UserId          = userId,
            MediaItemId     = mediaItemId,
            Timestamp       = ts,
            ProgressPercent = evt.ProgressPercent ?? 100,
            DeviceName      = sourceName,
            MarkedAsWatched = true,
            CreatedAt       = DateTime.UtcNow,
            IsApproximateTimestamp = evt.WatchedAtIsApproximate,
        });
        await db.SaveChangesAsync(ct);
        return 1;
    }

    // ── Library status ────────────────────────────────────────────────────────

    private static async Task UpsertLibraryStatusAsync(
        ChronicleDbContext db, int mediaItemId, int userId, ImportedWatchEvent evt, CancellationToken ct)
    {
        var entry = await db.UserLibraries
            .FirstOrDefaultAsync(l => l.UserId == userId && l.MediaItemId == mediaItemId, ct);

        var newStatus = (evt.MediaType == "tv_episode" || evt.MediaType == "anime_episode")
            ? LibraryStatus.Watching
            : LibraryStatus.Completed;

        if (entry is null)
        {
            db.UserLibraries.Add(new UserLibrary
            {
                UserId      = userId,
                MediaItemId = mediaItemId,
                Status      = newStatus,
                AddedAt     = DateTime.UtcNow,
                UpdatedAt   = DateTime.UtcNow,
                CompletedAt = newStatus == LibraryStatus.Completed ? DateTime.UtcNow : null,
            });
            await db.SaveChangesAsync(ct);
        }
        else if (entry.Status is LibraryStatus.PlanToWatch or LibraryStatus.Unwatched)
        {
            entry.Status      = newStatus;
            entry.CompletedAt ??= newStatus == LibraryStatus.Completed ? DateTime.UtcNow : null;
            await db.SaveChangesAsync(ct);
        }
    }

    private static async Task UpsertWatchlistStatusAsync(
        ChronicleDbContext db, ImportedWatchlistEntry entry, string pluginId, int userId, CancellationToken ct)
    {
        var source = SourceFromPluginId(pluginId);
        var mediaItemId = await db.MediaExternalIds
            .Where(e => e.Source == source && e.ExternalId == entry.ExternalId)
            .Select(e => e.MediaItemId)
            .FirstOrDefaultAsync(ct);
        if (mediaItemId == 0) return;

        var lib = await db.UserLibraries
            .FirstOrDefaultAsync(l => l.UserId == userId && l.MediaItemId == mediaItemId, ct);
        if (lib is null)
        {
            db.UserLibraries.Add(new UserLibrary
            {
                UserId      = userId,
                MediaItemId = mediaItemId,
                Status      = LibraryStatus.PlanToWatch,
                AddedAt     = DateTime.UtcNow,
                UpdatedAt   = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }
    }

    internal static async Task UpsertRatingAsync(
        ChronicleDbContext db, ImportedRating rating, string pluginId, int userId, CancellationToken ct)
    {
        var source = SourceFromPluginId(pluginId);
        var mediaItemId = await db.MediaExternalIds
            .Where(e => e.Source == source && e.ExternalId == rating.ExternalId)
            .Select(e => e.MediaItemId)
            .FirstOrDefaultAsync(ct);
        if (mediaItemId == 0) return;

        var lib = await db.UserLibraries
            .FirstOrDefaultAsync(l => l.UserId == userId && l.MediaItemId == mediaItemId, ct);
        if (lib is null)
        {
            // Confirmed real bug (2026-08-30): a rating for an item that's matched in Chronicle's
            // catalog but has no UserLibrary row yet (e.g. rated directly on Trakt/Simkl without
            // ever syncing a watch event for it, or synced before this item was auto-tracked) was
            // silently dropped -- same "create if missing" pattern UpsertWatchEventAsync and
            // UpsertWatchlistStatusAsync already use, just missing here. A rating is itself strong
            // evidence the item was watched, so the new row defaults to Completed rather than the
            // Unwatched a plain auto-track would use.
            lib = new UserLibrary
            {
                UserId      = userId,
                MediaItemId = mediaItemId,
                Status      = LibraryStatus.Completed,
                AddedAt     = DateTime.UtcNow,
                UpdatedAt   = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
            };
            db.UserLibraries.Add(lib);
        }

        // Per-user request (2026-08-30): "the most recent status wins" -- a rating already
        // present with a NEWER timestamp than this sync's own (e.g. rated more recently on
        // Chronicle's web UI, or pushed from Kodi via Chronicle_Rating) must not be clobbered
        // by a stale value from this pass. A rating with no recorded timestamp yet (predates
        // this column, or came from a source with no real timestamp -- see Simkl's own
        // GetRatingsAsync doc) is treated as unconditionally old so a real incoming timestamp
        // always wins.
        if (lib.UserRatingUpdatedAt is { } existingRatedAt && rating.RatedAt.UtcDateTime <= existingRatedAt)
            return;

        lib.UserRating          = rating.Rating;
        lib.UserRatingUpdatedAt = rating.RatedAt.UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Feed path for ImportedPlaybackProgress (Trakt's GET /sync/playback/{movies,episodes} --
    /// see that record's own doc for why Simkl never supplies any). Same "most recent wins"
    /// rule as UpsertRatingAsync, compared against ResumeUpdatedAt -- this device's own live
    /// scrobbles (ScrobbleService) and another sync source both write that same field, so a
    /// stale Trakt sync must never overwrite a genuinely more recent position. Returns true
    /// when the position was actually applied (for the caller's own summary count), false when
    /// skipped (no match, or a newer position already on file).
    /// </summary>
    internal static async Task<bool> UpsertPlaybackProgressAsync(
        ChronicleDbContext db, ImportedPlaybackProgress progress, string pluginId, int userId, CancellationToken ct)
    {
        var source = SourceFromPluginId(pluginId);
        var mediaItemId = await db.MediaExternalIds
            .Where(e => e.Source == source && e.ExternalId == progress.ExternalId)
            .Select(e => e.MediaItemId)
            .FirstOrDefaultAsync(ct);
        if (mediaItemId == 0) return false;

        // Progress belongs on the episode/movie item itself (unlike library STATUS, which
        // belongs on the root show) -- Kodi's own resume field is per-episode, and so is
        // Chronicle's own ResumePositionPercent (see ScrobbleService).
        var lib = await db.UserLibraries
            .FirstOrDefaultAsync(l => l.UserId == userId && l.MediaItemId == mediaItemId, ct);
        if (lib is null)
        {
            lib = new UserLibrary
            {
                UserId      = userId,
                MediaItemId = mediaItemId,
                Status      = LibraryStatus.Watching,
                AddedAt     = DateTime.UtcNow,
                UpdatedAt   = DateTime.UtcNow,
                StartedAt   = DateTime.UtcNow,
            };
            db.UserLibraries.Add(lib);
        }
        else if (lib.Status is LibraryStatus.Unwatched or LibraryStatus.PlanToWatch)
        {
            // Don't downgrade Completed/Dropped/OnHold/Rewatching -- an in-progress position
            // from a sync source doesn't necessarily mean they're watching it again right now.
            lib.Status = LibraryStatus.Watching;
        }

        if (lib.ResumeUpdatedAt is { } existingResumeAt && progress.UpdatedAt.UtcDateTime <= existingResumeAt)
            return false;

        lib.ResumePositionPercent = progress.ProgressPercent;
        lib.ResumeUpdatedAt       = progress.UpdatedAt.UtcDateTime;
        lib.UpdatedAt             = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── Credits ───────────────────────────────────────────────────────────────

    private async Task<int> FetchAndStoreCreditsAsync(
        ChronicleDbContext db, int mediaItemId, string externalId,
        string mediaType, string pluginId, IImportProvider provider, CancellationToken ct)
    {
        List<ImportedCredit> credits;
        try { credits = await provider.GetCreditsAsync(externalId, mediaType, ct); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GetCreditsAsync failed for {Id}", externalId);
            return 0;
        }

        if (credits.Count == 0) return 0;

        var source = SourceFromPluginId(pluginId);
        var old = db.MediaCredits.Where(c => c.MediaItemId == mediaItemId && c.Source == source);
        db.MediaCredits.RemoveRange(old);

        foreach (var credit in credits)
        {
            db.MediaCredits.Add(new MediaCredit
            {
                MediaItemId      = mediaItemId,
                PersonName       = credit.PersonName,
                Role             = credit.Role,
                CharacterName    = credit.CharacterName,
                BillingOrder     = credit.BillingOrder,
                Source           = source,
                ExternalPersonId = credit.ExternalPersonId,
            });
        }

        await db.SaveChangesAsync(ct);
        return credits.Count;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string SourceFromPluginId(string pluginId) =>
        PluginIdHelper.ToSource(pluginId);

    private static void MergeImportedMetadata(MediaItem item, string pluginId, ImportedItemMetadata meta)
    {
        var existing = string.IsNullOrEmpty(item.MetadataJson)
            ? new Dictionary<string, JsonElement>()
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.MetadataJson) ?? [];

        // Field names must match MetadataResolutionService.FieldMap so that ResolveAsync
        // can promote values from this blob to item first-class columns (PosterUrl, Overview…).
        // MetadataBlobOptions enforces camelCase so any future addition to this object that
        // accidentally uses PascalCase (e.g. PosterUrl instead of posterUrl) is still correct.
        existing[pluginId] = JsonSerializer.SerializeToElement(new
        {
            title          = meta.Title,
            year           = meta.Year,
            overview       = meta.Overview,
            posterUrl      = meta.PosterUrl,
            backdropUrl    = meta.FanartUrl,
            runtimeMinutes = meta.RuntimeMinutes,
            ids            = meta.AdditionalIds,
        }, MetadataEnrichmentService.MetadataBlobOptions);

        item.MetadataJson = JsonSerializer.Serialize(existing);

        // Direct assignment as a fallback for items whose plugin is not yet configured
        // in the metadata assignment priority map — ensures the poster is visible even
        // before the user visits Settings → Metadata Assignment.
        // ResolveAsync (called after this) will override with the priority-map winner
        // if a higher-priority plugin also has a poster.
        if (!string.IsNullOrEmpty(meta.PosterUrl))
            item.PosterUrl = meta.PosterUrl;
    }

    private static async Task GraftExternalIdAsync(
        ChronicleDbContext db, int mediaItemId, string pluginId, string externalId, CancellationToken ct)
    {
        var source   = SourceFromPluginId(pluginId);
        var existing = await db.MediaExternalIds
            .FirstOrDefaultAsync(e => e.MediaItemId == mediaItemId && e.Source == source, ct);

        if (existing is null)
        {
            db.MediaExternalIds.Add(new MediaExternalId
                { MediaItemId = mediaItemId, Source = source, ExternalId = externalId });
            await db.SaveChangesAsync(ct);
        }
        else if (existing.ExternalId != externalId)
        {
            // Stale ID from a previous sync run — update to the current one.
            existing.ExternalId = externalId;
            await db.SaveChangesAsync(ct);
        }
        // else: already correct — no-op
    }
}
