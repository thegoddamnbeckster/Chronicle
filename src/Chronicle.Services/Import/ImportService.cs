using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace Chronicle.Services.Import;

/// <summary>
/// Orchestrates import from external tracking services (Trakt, Simkl, …).
///
/// For each imported item the service:
///   1. Looks up the <see cref="MediaItem"/> by cross-reference ID, then by title+year.
///   2. Creates a stub <see cref="MediaItem"/> if no match is found.
///   3. Persists <see cref="InteractionEvent"/> / <see cref="UserLibrary"/> records.
/// </summary>
public class ImportService : IImportService
{
    private readonly ChronicleDbContext _db;
    private readonly IPluginRegistry   _registry;
    private readonly IPluginService    _pluginService;
    private readonly ILogger _log = Log.ForContext<ImportService>();

    public ImportService(ChronicleDbContext db, IPluginRegistry registry, IPluginService pluginService)
    {
        _db            = db;
        _registry      = registry;
        _pluginService = pluginService;
    }

    // ── Provider discovery ────────────────────────────────────────────────────

    public IReadOnlyList<IImportProvider> GetProviders() =>
        _registry.GetImportProviders();

    // ── Auth delegation ───────────────────────────────────────────────────────

    public async Task<DeviceAuthStart> StartAuthAsync(string pluginId, CancellationToken ct = default)
    {
        var provider = GetProvider(pluginId);
        return await provider.StartAuthAsync(ct);
    }

    public async Task<DeviceAuthPollResult> PollAuthAsync(
        string pluginId, string pollCode, CancellationToken ct = default)
    {
        var provider = GetProvider(pluginId);
        return await provider.PollAuthAsync(pollCode, ct);
    }

    public async Task<bool> IsAuthenticatedAsync(string pluginId, CancellationToken ct = default)
    {
        var provider = GetProvider(pluginId);
        return await provider.IsAuthenticatedAsync(ct);
    }

    // ── History ───────────────────────────────────────────────────────────────

    public async Task<ImportResult> ImportHistoryAsync(
        string pluginId, int userId, DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var provider = GetProvider(pluginId);
        _log.Information("Starting history import from {Plugin} for user {UserId}", pluginId, userId);

        var events = await provider.GetWatchHistoryAsync(since, ct);
        _log.Information("Retrieved {Count} watch events from {Plugin}", events.Count, pluginId);

        int imported = 0, skipped = 0;
        var errors = new List<string>();

        foreach (var evt in events)
        {
            try
            {
                var mediaItem = await FindOrCreateMediaItemAsync(
                    evt.ExternalId, evt.AdditionalIds, evt.MediaType, evt.Title, evt.Year, ct);

                // Skip duplicate interaction events (same user + media + timestamp)
                var ts = evt.WatchedAt.UtcDateTime;
                var exists = await _db.InteractionEvents
                    .AnyAsync(e => e.UserId == userId
                                && e.MediaItemId == mediaItem.Id
                                && e.Timestamp == ts, ct);

                if (exists) { skipped++; continue; }

                var progress = evt.ProgressPercent ?? 100.0;
                _db.InteractionEvents.Add(new InteractionEvent
                {
                    UserId         = userId,
                    MediaItemId    = mediaItem.Id,
                    Timestamp      = ts,
                    ProgressPercent = progress,
                    DeviceName     = $"Import: {pluginId}",
                    MarkedAsWatched = progress >= 80.0,
                    CreatedAt      = DateTime.UtcNow,
                });

                // Ensure a library entry exists and is marked Completed
                await UpsertLibraryEntryAsync(userId, mediaItem.Id, LibraryStatus.Completed, null, ct);

                imported++;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Skipping watch event {Id} due to error", evt.ExternalId);
                errors.Add($"{evt.ExternalId}: {ex.Message}");
                skipped++;
            }
        }

        await _db.SaveChangesAsync(ct);
        await PersistRefreshedTokensAsync(provider, pluginId, ct);
        _log.Information("History import done — imported {I}, skipped {S}", imported, skipped);
        return new ImportResult(imported, skipped, errors);
    }

    // ── Ratings ───────────────────────────────────────────────────────────────

    public async Task<ImportResult> ImportRatingsAsync(
        string pluginId, int userId, CancellationToken ct = default)
    {
        var provider = GetProvider(pluginId);
        _log.Information("Starting ratings import from {Plugin} for user {UserId}", pluginId, userId);

        var ratings = await provider.GetRatingsAsync(ct);
        _log.Information("Retrieved {Count} ratings from {Plugin}", ratings.Count, pluginId);

        int imported = 0, skipped = 0;
        var errors = new List<string>();

        foreach (var r in ratings)
        {
            try
            {
                var mediaItem = await FindOrCreateMediaItemAsync(
                    r.ExternalId, r.AdditionalIds, r.MediaType, r.Title, r.Year, ct);

                await UpsertLibraryEntryAsync(userId, mediaItem.Id, LibraryStatus.Completed, r.Rating, ct);
                imported++;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Skipping rating {Id} due to error", r.ExternalId);
                errors.Add($"{r.ExternalId}: {ex.Message}");
                skipped++;
            }
        }

        await _db.SaveChangesAsync(ct);
        await PersistRefreshedTokensAsync(provider, pluginId, ct);
        _log.Information("Ratings import done — imported {I}, skipped {S}", imported, skipped);
        return new ImportResult(imported, skipped, errors);
    }

    // ── Watchlist ─────────────────────────────────────────────────────────────

    public async Task<ImportResult> ImportWatchlistAsync(
        string pluginId, int userId, CancellationToken ct = default)
    {
        var provider = GetProvider(pluginId);
        _log.Information("Starting watchlist import from {Plugin} for user {UserId}", pluginId, userId);

        var entries = await provider.GetWatchlistAsync(ct);
        _log.Information("Retrieved {Count} watchlist entries from {Plugin}", entries.Count, pluginId);

        int imported = 0, skipped = 0;
        var errors = new List<string>();

        foreach (var entry in entries)
        {
            try
            {
                var mediaItem = await FindOrCreateMediaItemAsync(
                    entry.ExternalId, entry.AdditionalIds, entry.MediaType, entry.Title, entry.Year, ct);

                // Only add if the user doesn't already have this in their library
                var existing = await _db.UserLibraries
                    .AnyAsync(l => l.UserId == userId && l.MediaItemId == mediaItem.Id, ct);

                if (existing) { skipped++; continue; }

                await UpsertLibraryEntryAsync(userId, mediaItem.Id, LibraryStatus.PlanToWatch, null, ct);
                imported++;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Skipping watchlist entry {Id} due to error", entry.ExternalId);
                errors.Add($"{entry.ExternalId}: {ex.Message}");
                skipped++;
            }
        }

        await _db.SaveChangesAsync(ct);
        await PersistRefreshedTokensAsync(provider, pluginId, ct);
        _log.Information("Watchlist import done — imported {I}, skipped {S}", imported, skipped);
        return new ImportResult(imported, skipped, errors);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task PersistRefreshedTokensAsync(
        IImportProvider provider, string pluginId, CancellationToken ct)
    {
        var refreshed = provider.GetRefreshedSettings();
        if (refreshed is { Count: > 0 })
        {
            _log.Information("Persisting refreshed OAuth tokens for {Plugin}", pluginId);
            await _pluginService.MergeSettingsAsync(pluginId, refreshed, ct);
        }
    }

    private IImportProvider GetProvider(string pluginId)
    {
        return _registry.GetImportProvider(pluginId)
            ?? throw new InvalidOperationException(
                $"Import provider '{pluginId}' is not loaded. " +
                $"Install and enable the plugin first.");
    }

    /// <summary>
    /// Finds a matching <see cref="MediaItem"/> via external IDs, then title+year,
    /// then creates a stub item if nothing is found.
    /// </summary>
    private async Task<MediaItem> FindOrCreateMediaItemAsync(
        string primaryExternalId,
        IReadOnlyDictionary<string, string> additionalIds,
        string mediaType,
        string title,
        int? year,
        CancellationToken ct)
    {
        // Build a lookup set of all provided external IDs (primary + additional)
        var allIds = new Dictionary<string, string>(additionalIds);
        var primaryParts = primaryExternalId.Split(':', 2);
        if (primaryParts.Length == 2)
            allIds[primaryParts[0]] = primaryParts[1];

        // 1. Try to match any of the external IDs against media_external_ids
        foreach (var (source, extId) in allIds)
        {
            var match = await _db.MediaExternalIds
                .Include(x => x.MediaItem)
                .FirstOrDefaultAsync(x => x.Source == source && x.ExternalId == extId, ct);

            if (match?.MediaItem != null)
                return match.MediaItem;
        }

        // Resolved once, up front, so the title fallback below can scope its match to the
        // same media type — matching on Name/Year alone let an imported event for a movie
        // silently land on a same-named/same-year TV item (or vice versa), with no way to
        // tell them apart afterwards.
        var typeId = await ResolveMediaTypeIdAsync(mediaType, ct);

        // 2. Try title + year match
        if (!string.IsNullOrWhiteSpace(title))
        {
            var titleMatch = await _db.MediaItems
                .FirstOrDefaultAsync(m =>
                    m.Name == title &&
                    m.MediaTypeId == typeId &&
                    (!year.HasValue || m.Year == year), ct);

            if (titleMatch != null)
            {
                // Store the external IDs so future imports match faster
                await StoreExternalIdsAsync(titleMatch.Id, allIds, ct);
                return titleMatch;
            }
        }

        // 3. Create a stub item
        var stub = new MediaItem
        {
            MediaTypeId    = typeId,
            Name           = title,
            Year           = year,
            HierarchyLevel = 0,
            CreatedAt      = DateTime.UtcNow,
            UpdatedAt      = DateTime.UtcNow,
        };
        _db.MediaItems.Add(stub);
        await _db.SaveChangesAsync(ct);   // need the id before adding external IDs

        await StoreExternalIdsAsync(stub.Id, allIds, ct);
        await _db.SaveChangesAsync(ct);

        _log.Debug("Created stub media item '{Title}' ({Year}) id={Id}", title, year, stub.Id);
        return stub;
    }

    private async Task StoreExternalIdsAsync(
        int mediaItemId,
        IReadOnlyDictionary<string, string> ids,
        CancellationToken ct)
    {
        foreach (var (source, extId) in ids)
        {
            var exists = await _db.MediaExternalIds.AnyAsync(
                x => x.MediaItemId == mediaItemId && x.Source == source, ct);

            if (!exists)
            {
                _db.MediaExternalIds.Add(new MediaExternalId
                {
                    MediaItemId = mediaItemId,
                    Source      = source,
                    ExternalId  = extId,
                });
            }
        }
    }

    private async Task UpsertLibraryEntryAsync(
        int userId, int mediaItemId, LibraryStatus preferredStatus,
        int? rating, CancellationToken ct)
    {
        var entry = await _db.UserLibraries
            .FirstOrDefaultAsync(l => l.UserId == userId && l.MediaItemId == mediaItemId, ct);

        if (entry is null)
        {
            _db.UserLibraries.Add(new UserLibrary
            {
                UserId      = userId,
                MediaItemId = mediaItemId,
                Status      = preferredStatus,
                UserRating  = rating,
                AddedAt     = DateTime.UtcNow,
                UpdatedAt   = DateTime.UtcNow,
                CompletedAt = preferredStatus == LibraryStatus.Completed ? DateTime.UtcNow : null,
            });
        }
        else
        {
            // Only upgrade status (PlanToWatch → Completed, not Completed → PlanToWatch)
            if (preferredStatus == LibraryStatus.Completed)
            {
                entry.Status      = LibraryStatus.Completed;
                entry.CompletedAt ??= DateTime.UtcNow;
            }

            if (rating.HasValue)
                entry.UserRating = rating;

            entry.UpdatedAt = DateTime.UtcNow;
        }
    }

    private async Task<int> ResolveMediaTypeIdAsync(string mediaType, CancellationToken ct)
    {
        // Try to resolve a media type id that matches the import type name
        var normalised = mediaType.ToLowerInvariant() switch
        {
            "tv_episode" or "tv_show" or "show" or "tv" => "tv",
            "movie" or "film" => "movie",
            "anime" => "anime",
            _ => mediaType.ToLowerInvariant()
        };

        var type = await _db.MediaTypes
            .FirstOrDefaultAsync(t => t.Name == normalised && t.IsActive, ct);

        if (type != null)
            return type.Id;

        // Fall back to the first available active type
        var fallback = await _db.MediaTypes
            .Where(t => t.IsActive)
            .OrderBy(t => t.Id)
            .FirstOrDefaultAsync(ct);

        return fallback?.Id
            ?? throw new InvalidOperationException(
                "No active media types found in the database. Create at least one media type first.");
    }
}
