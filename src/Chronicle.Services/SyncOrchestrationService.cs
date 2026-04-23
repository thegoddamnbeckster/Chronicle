using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

public class SyncOrchestrationService : ISyncOrchestrationService
{
    private const string SyncStateKeyPrefix = "sync_state.";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPluginRegistry _registry;
    private readonly ILogger<SyncOrchestrationService> _log;

    public SyncOrchestrationService(
        IServiceScopeFactory scopeFactory,
        IPluginRegistry registry,
        ILogger<SyncOrchestrationService> log)
    {
        _scopeFactory = scopeFactory;
        _registry     = registry;
        _log          = log;
    }

    public async Task<SyncSummary> SyncAsync(
        string pluginId, bool fullSync = false, CancellationToken ct = default)
    {
        var provider = _registry.GetImportProvider(pluginId)
            ?? throw new InvalidOperationException($"Plugin '{pluginId}' has no IImportProvider.");

        if (!await provider.IsAuthenticatedAsync(ct))
            throw new InvalidOperationException($"Plugin '{pluginId}' is not authenticated.");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

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
        await Task.WhenAll(historyTask, ratingsTask, watchlistTask);

        var history   = historyTask.Result;
        var ratings   = ratingsTask.Result;
        var watchlist = watchlistTask.Result;

        int itemsMatched = 0, stubsCreated = 0, watchEventsAdded = 0, creditsAdded = 0;
        var errors = new List<string>();

        foreach (var evt in history)
        {
            try
            {
                var (item, isNew) = await MatchOrCreateAsync(db, evt, pluginId, ct);
                if (isNew) stubsCreated++; else itemsMatched++;
                watchEventsAdded += await UpsertWatchEventAsync(db, item.Id, evt, ct);

                // Library status belongs on the root show, not on individual episodes.
                var libraryItemId = item.ParentId.HasValue
                    ? await GetRootItemIdAsync(db, item, ct)
                    : item.Id;
                await UpsertLibraryStatusAsync(db, libraryItemId, evt, ct);

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
            try { await UpsertRatingAsync(db, rating, pluginId, ct); }
            catch (Exception ex) { errors.Add($"rating {rating.ExternalId}: {ex.Message}"); }
        }

        foreach (var entry in watchlist)
        {
            try { await UpsertWatchlistStatusAsync(db, entry, pluginId, ct); }
            catch (Exception ex) { errors.Add($"watchlist {entry.ExternalId}: {ex.Message}"); }
        }

        // Persist last-synced timestamp
        var setting = await db.AppSettings.FindAsync([syncKey], ct);
        if (setting is null)
            db.AppSettings.Add(new AppSetting { Key = syncKey, Value = DateTimeOffset.UtcNow.ToString("O") });
        else
            setting.Value = DateTimeOffset.UtcNow.ToString("O");
        await db.SaveChangesAsync(ct);

        _log.LogInformation(
            "Sync complete for {PluginId}: {Matched} matched, {Created} stubs, {Events} events, {Credits} credits, {Errors} errors",
            pluginId, itemsMatched, stubsCreated, watchEventsAdded, creditsAdded, errors.Count);

        return new SyncSummary(itemsMatched, stubsCreated, watchEventsAdded, creditsAdded, errors);
    }

    // ── Item matching ─────────────────────────────────────────────────────────

    internal async Task<(MediaItem item, bool isNew)> MatchOrCreateAsync(
        ChronicleDbContext db,
        ImportedWatchEvent evt,
        string pluginId,
        CancellationToken ct)
    {
        // Route TV episodes to the hierarchy builder when season/episode numbers are present.
        if (evt.MediaType == "tv_episode" && evt.SeasonNumber.HasValue && evt.EpisodeNumber.HasValue)
            return await MatchOrCreateEpisodeAsync(db, evt, pluginId, ct);

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
                return (await db.MediaItems.FindAsync([byAdditional], ct)
                    ?? throw new InvalidOperationException($"MediaItem {byAdditional} missing"), false);
        }

        // 3. Title + year fuzzy match
        if (evt.Title is not null && evt.Year.HasValue)
        {
            var byTitle = await db.MediaItems
                .FirstOrDefaultAsync(i => i.Year == evt.Year && i.Name == evt.Title, ct);
            if (byTitle is not null)
                return (byTitle, false);
        }

        // 4. Create stub
        var provider = _registry.GetImportProvider(pluginId);
        return (await CreateStubAsync(db, evt, pluginId, provider, ct), true);
    }

    private async Task<MediaItem> CreateStubAsync(
        ChronicleDbContext db,
        ImportedWatchEvent evt,
        string pluginId,
        IImportProvider? provider,
        CancellationToken ct)
    {
        ImportedItemMetadata? meta = null;
        if (provider is not null)
        {
            try { meta = await provider.GetItemMetadataAsync(evt.ExternalId, evt.MediaType, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "GetItemMetadataAsync failed for {Id}", evt.ExternalId); }
        }

        var mediaTypeName = MapMediaType(evt.MediaType);
        var mediaType = await db.MediaTypes
            .FirstOrDefaultAsync(t => t.Name == mediaTypeName, ct)
            ?? throw new InvalidOperationException($"Media type '{mediaTypeName}' not found in database.");

        var item = new MediaItem
        {
            Name           = meta?.Title ?? evt.Title ?? "Unknown",
            Year           = meta?.Year ?? evt.Year,
            MediaTypeId    = mediaType.Id,
            HierarchyLevel = 0,
            CreatedAt      = DateTime.UtcNow,
            UpdatedAt      = DateTime.UtcNow,
        };
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

        // Seed enrichment rows for all loaded metadata plugins that support this media type
        foreach (var (mpPluginId, mp, _) in _registry.GetMetadataProviderEntries())
        {
            var supported = mp.GetSupportedMediaTypes()
                .Any(t => string.Equals(t.MediaTypeName, mediaTypeName, StringComparison.OrdinalIgnoreCase));
            if (!supported) continue;

            var exists = await db.MediaEnrichments
                .AnyAsync(e => e.MediaItemId == item.Id && e.PluginId == mpPluginId, ct);
            if (exists) continue;

            allIds.TryGetValue(SourceFromPluginId(mpPluginId), out var knownId);
            db.MediaEnrichments.Add(new MediaItemEnrichment
            {
                MediaItemId = item.Id,
                PluginId    = mpPluginId,
                Status      = EnrichmentStatus.Pending,
                MaxRetries  = 3,
                ExternalId  = knownId,
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
            return (await db.MediaItems.FindAsync([byId], ct)!, false);

        // 2. Find or create the parent show using show-level data.
        var showEvt = evt with
        {
            ExternalId    = evt.ShowExternalId ?? evt.ExternalId,
            MediaType     = "tv",
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
        ChronicleDbContext db, int mediaItemId, ImportedWatchEvent evt, CancellationToken ct)
    {
        var ts = evt.WatchedAt.UtcDateTime;
        var exists = await db.InteractionEvents
            .AnyAsync(e => e.MediaItemId == mediaItemId && e.Timestamp == ts, ct);
        if (exists) return 0;

        db.InteractionEvents.Add(new InteractionEvent
        {
            MediaItemId     = mediaItemId,
            Timestamp       = ts,
            ProgressPercent = evt.ProgressPercent ?? 100,
            MarkedAsWatched = true,
        });
        await db.SaveChangesAsync(ct);
        return 1;
    }

    // ── Library status ────────────────────────────────────────────────────────

    private static async Task UpsertLibraryStatusAsync(
        ChronicleDbContext db, int mediaItemId, ImportedWatchEvent evt, CancellationToken ct)
    {
        var entry = await db.UserLibraries
            .FirstOrDefaultAsync(l => l.MediaItemId == mediaItemId, ct);

        var newStatus = evt.MediaType == "tv_episode" ? LibraryStatus.Watching : LibraryStatus.Completed;

        if (entry is null)
        {
            db.UserLibraries.Add(new UserLibrary
            {
                MediaItemId = mediaItemId,
                Status      = newStatus,
                AddedAt     = DateTime.UtcNow,
                UpdatedAt   = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }
        else if (entry.Status is LibraryStatus.PlanToWatch or LibraryStatus.Unwatched)
        {
            entry.Status = newStatus;
            await db.SaveChangesAsync(ct);
        }
    }

    private static async Task UpsertWatchlistStatusAsync(
        ChronicleDbContext db, ImportedWatchlistEntry entry, string pluginId, CancellationToken ct)
    {
        var source = SourceFromPluginId(pluginId);
        var mediaItemId = await db.MediaExternalIds
            .Where(e => e.Source == source && e.ExternalId == entry.ExternalId)
            .Select(e => e.MediaItemId)
            .FirstOrDefaultAsync(ct);
        if (mediaItemId == 0) return;

        var lib = await db.UserLibraries.FirstOrDefaultAsync(l => l.MediaItemId == mediaItemId, ct);
        if (lib is null)
        {
            db.UserLibraries.Add(new UserLibrary
            {
                MediaItemId = mediaItemId,
                Status      = LibraryStatus.PlanToWatch,
                AddedAt     = DateTime.UtcNow,
                UpdatedAt   = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }
    }

    private static async Task UpsertRatingAsync(
        ChronicleDbContext db, ImportedRating rating, string pluginId, CancellationToken ct)
    {
        var source = SourceFromPluginId(pluginId);
        var mediaItemId = await db.MediaExternalIds
            .Where(e => e.Source == source && e.ExternalId == rating.ExternalId)
            .Select(e => e.MediaItemId)
            .FirstOrDefaultAsync(ct);
        if (mediaItemId == 0) return;

        var lib = await db.UserLibraries.FirstOrDefaultAsync(l => l.MediaItemId == mediaItemId, ct);
        if (lib is null) return;

        lib.UserRating = rating.Rating;
        await db.SaveChangesAsync(ct);
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
        pluginId.Split('.').Last();

    private static string MapMediaType(string importType) => importType switch
    {
        "movie"      => "movies",
        "tv_show"    => "tv",
        "tv_episode" => "tv",
        _            => importType,
    };
}
