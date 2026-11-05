using System.Text.Json;
using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Services.Matching;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>What one library import did.</summary>
public sealed record GameLibraryResult(
    int Matched, int Created, int Events, int StatusesSet, int Skipped, IReadOnlyList<string> Errors);

/// <summary>
/// Files a platform library (<see cref="ImportedLibraryEntry"/>) into Chronicle: one item per game, one
/// <see cref="UserGameOwnership"/> row per (user, platform, platform id), a library status for new items only.
/// <para>
/// It is separate from the watch-event path on purpose: that path records "a play happened" (marked watched, 100%,
/// Completed), and a library is mostly titles the user never started. Rules, each of which exists because the
/// shared path would break it:
/// </para>
/// <list type="bullet">
///   <item>An existing library row's status is the user's. It is advanced only while it still equals what this importer wrote.</item>
///   <item>A play event is written only for a last-played time newer than anything stored and after the user's own reset, never marked watched.</item>
///   <item>A title absent from a response is never removed. A title the user deleted is not recreated (its ownership row survives, item id null).</item>
///   <item>Titles match by id first; a year-less title matches by name only when exactly one candidate exists and it holds no different id from the same platform. Ambiguity makes two items, not a merge.</item>
/// </list>
/// </summary>
public sealed class GameLibraryImporter
{
    private readonly IPluginRegistry _registry;
    private readonly IMetadataResolutionService _resolution;
    private readonly ILogger _log;

    public GameLibraryImporter(IPluginRegistry registry, IMetadataResolutionService resolution, ILogger log)
    {
        _registry = registry;
        _resolution = resolution;
        _log = log;
    }

    public async Task<GameLibraryResult> ImportAsync(
        ChronicleDbContext db, string pluginId, string providerName, int userId,
        IReadOnlyList<ImportedLibraryEntry> entries, CancellationToken ct)
    {
        int matched = 0, created = 0, events = 0, statuses = 0, skipped = 0;
        var errors = new List<string>();

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                One r;
                try { r = await ImportOneAsync(db, pluginId, providerName, userId, entry, ct); }
                catch (DbUpdateException)
                {
                    // Another writer (background enrichment, a second sync) saved the same row between the check and the insert: a unique key
                    // refused it. Seen once in 342 entries on the first real import. The entry is complete once the tracker is cleared and the
                    // rows are read again, so it is retried once rather than reported.
                    db.ChangeTracker.Clear();
                    r = await ImportOneAsync(db, pluginId, providerName, userId, entry, ct);
                }
                if (r.Skipped) skipped++;
                else if (r.Created) created++;
                else matched++;
                events += r.Events;
                statuses += r.StatusSet ? 1 : 0;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Library import error for {ExternalId}", entry.ExternalId);
                errors.Add($"{entry.ExternalId}: {ex.Message}");
                db.ChangeTracker.Clear();
            }
        }

        return new GameLibraryResult(matched, created, events, statuses, skipped, errors);
    }

    private readonly record struct One(bool Skipped, bool Created, int Events, bool StatusSet);

    private async Task<One> ImportOneAsync(
        ChronicleDbContext db, string pluginId, string providerName, int userId, ImportedLibraryEntry entry, CancellationToken ct)
    {
        var source   = entry.StoreSource;
        var sourceId = StripSource(entry.ExternalId);
        var now      = DateTime.UtcNow;

        var own = await db.UserGameOwnerships
            .FirstOrDefaultAsync(o => o.UserId == userId && o.Source == source && o.SourceId == sourceId, ct);

        // A row with no item is the user's own deletion: leave it deleted.
        if (own is not null && own.MediaItemId is null)
            return new One(true, false, 0, false);

        MediaItem? item = own?.MediaItemId is int existingId ? await db.MediaItems.FindAsync([existingId], ct) : null;
        var isNew = false;

        // The item vanished under a live row (should not happen: SET NULL clears the id). Treat as a deletion.
        if (own is not null && item is null)
            return new One(true, false, 0, false);

        item ??= await MatchAsync(db, pluginId, entry, ct);
        if (item is null)
        {
            item = await CreateAsync(db, pluginId, entry, ct);
            isNew = true;
        }
        else if (own is null)
        {
            await GraftAsync(db, item.Id, pluginId, entry.ExternalId, ct);
        }

        var lastPlayed = entry.LastPlayedAt is { } lp && lp.UtcDateTime > DateTime.UnixEpoch ? lp.UtcDateTime : (DateTime?)null;
        var acquired   = entry.AcquiredAt   is { } aq && aq.UtcDateTime > DateTime.UnixEpoch ? aq.UtcDateTime : (DateTime?)null;

        if (own is null)
        {
            own = new UserGameOwnership
            {
                UserId = userId, Source = source, SourceId = sourceId, FirstSeenAt = now,
            };
            db.UserGameOwnerships.Add(own);
        }
        else if (own.IsWishlist == false && entry.IsWishlist)
        {
            // Owned already: a wishlist echo never downgrades it.
            return new One(false, false, 0, false);
        }

        own.MediaItemId          = item.Id;
        own.Platform             = string.IsNullOrWhiteSpace(entry.Platform) ? source : entry.Platform;
        own.PlaytimeMinutes      = entry.PlaytimeMinutes;
        own.LastPlayedAt         = lastPlayed ?? own.LastPlayedAt;
        own.AcquiredAt           = acquired ?? own.AcquiredAt;
        own.OwnedViaSubscription = entry.OwnedViaSubscription;
        own.IsWishlist           = entry.IsWishlist;
        own.TagsJson             = entry.Tags is { Count: > 0 } ? JsonSerializer.Serialize(entry.Tags) : null;
        own.RawJson              = entry.RawJson;
        own.UpdatedAt            = now;
        await db.SaveChangesAsync(ct);

        var statusSet = await ApplyStatusAsync(db, userId, item.Id, own, entry, lastPlayed, now, ct);
        var events    = await AddPlayEventAsync(db, userId, item.Id, providerName, entry, lastPlayed, ct);

        return new One(false, isNew, events, statusSet);
    }

    // ── Status ────────────────────────────────────────────────────────────────

    private static LibraryStatus Wanted(ImportedLibraryEntry e) =>
        e.IsWishlist ? LibraryStatus.PlanToWatch
        : e.PlaytimeMinutes is > 0 ? LibraryStatus.Watching
        : LibraryStatus.Unwatched;

    private static async Task<bool> ApplyStatusAsync(
        ChronicleDbContext db, int userId, int itemId, UserGameOwnership own, ImportedLibraryEntry entry,
        DateTime? lastPlayed, DateTime now, CancellationToken ct)
    {
        var want = Wanted(entry);
        var lib = await db.UserLibraries.FirstOrDefaultAsync(l => l.UserId == userId && l.MediaItemId == itemId, ct);

        if (lib is null)
        {
            db.UserLibraries.Add(new UserLibrary
            {
                UserId = userId, MediaItemId = itemId, Status = want, AddedAt = now, UpdatedAt = now,
                StartedAt = want == LibraryStatus.Watching ? lastPlayed ?? now : null,
            });
            own.ImportStatusApplied = want;
            await db.SaveChangesAsync(ct);
            return true;
        }

        // The user's status is theirs. Only advance one this importer wrote and nobody has changed since.
        if (own.ImportStatusApplied is null || lib.Status != own.ImportStatusApplied) return false;
        if (lib.Status == want) return false;

        // Only forward moves: never wind a game back to "not started".
        var forward = (lib.Status, want) is
            (LibraryStatus.PlanToWatch, LibraryStatus.Unwatched) or
            (LibraryStatus.PlanToWatch, LibraryStatus.Watching) or
            (LibraryStatus.Unwatched,   LibraryStatus.Watching);
        if (!forward) return false;

        // A play at or before the user's own reset is history they cleared.
        if (lib.WatchResetAt is { } reset && want == LibraryStatus.Watching && (lastPlayed is null || lastPlayed <= reset))
            return false;

        lib.Status    = want;
        lib.UpdatedAt = now;
        lib.StartedAt ??= want == LibraryStatus.Watching ? lastPlayed ?? now : null;
        own.ImportStatusApplied = want;
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── Plays ─────────────────────────────────────────────────────────────────

    private static async Task<int> AddPlayEventAsync(
        ChronicleDbContext db, int userId, int itemId, string providerName, ImportedLibraryEntry entry,
        DateTime? lastPlayed, CancellationToken ct)
    {
        if (entry.IsWishlist || lastPlayed is null) return 0;
        var ts = lastPlayed.Value;

        var resetAt = await db.UserLibraries.AsNoTracking()
            .Where(l => l.UserId == userId && l.MediaItemId == itemId)
            .Select(l => l.WatchResetAt).FirstOrDefaultAsync(ct);
        if (resetAt is { } r && ts <= r) return 0;

        var newest = await db.InteractionEvents
            .Where(e => e.UserId == userId && e.MediaItemId == itemId)
            .MaxAsync(e => (DateTime?)e.Timestamp, ct);
        if (newest is { } n && ts <= n) return 0;

        db.InteractionEvents.Add(new InteractionEvent
        {
            UserId = userId, MediaItemId = itemId, Timestamp = ts,
            ProgressPercent = null, MarkedAsWatched = false, DeviceName = providerName, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return 1;
    }

    // ── Matching ──────────────────────────────────────────────────────────────

    private static async Task<MediaItem?> MatchAsync(
        ChronicleDbContext db, string pluginId, ImportedLibraryEntry entry, CancellationToken ct)
    {
        var source = PluginIdHelper.ToSource(pluginId);

        // The same id is written two ways: "steam:32630" by the importer and a bare "32630" by a metadata plugin (IGDB stores Steam's id that way).
        // Both mean one game, so both are looked for.
        var bareOwn = Bare(entry.ExternalId);
        var byOwn = await db.MediaExternalIds
            .Where(e => e.Source == source && (e.ExternalId == entry.ExternalId || e.ExternalId == bareOwn))
            .Select(e => e.MediaItemId).FirstOrDefaultAsync(ct);
        if (byOwn != 0) return await db.MediaItems.FindAsync([byOwn], ct);

        foreach (var (src, extId) in entry.AdditionalIds)
        {
            var id = await db.MediaExternalIds
                .Where(e => e.Source == src && e.ExternalId == extId)
                .Select(e => e.MediaItemId).FirstOrDefaultAsync(ct);
            if (id != 0) return await db.MediaItems.FindAsync([id], ct);
        }

        var typeName = MediaItemMatcher.NormalizeMediaTypeName(entry.MediaType);
        var type = await db.MediaTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Name == typeName, ct);
        if (type is null) return null;

        var norm = MediaItemNormalizer.NormalizeName(entry.Title);
        if (string.IsNullOrEmpty(norm)) return null;

        var ceiling = type.HierarchyLevels > 1 ? 0 : 1;
        var candidates = await db.MediaItems
            .Where(m => m.MediaTypeId == type.Id && m.HierarchyLevel <= ceiling && m.NormalizedName == norm
                     && (entry.Year == null || m.Year == null || m.Year == entry.Year))
            .Take(3).ToListAsync(ct);
        if (candidates.Count == 0) return null;

        // A candidate that already carries a different id from this platform is a different game with the same name.
        var free = new List<MediaItem>();
        foreach (var c in candidates)
        {
            var theirs = await db.MediaExternalIds.Where(e => e.MediaItemId == c.Id && e.Source == source).Select(e => e.ExternalId).ToListAsync(ct);
            var hasOther = theirs.Any(id => !string.Equals(Bare(id), bareOwn, StringComparison.OrdinalIgnoreCase));
            if (!hasOther) free.Add(c);
        }
        // Exactly one, or it is ambiguous and a new item is made rather than a guess merged.
        return free.Count == 1 ? free[0] : null;
    }

    private async Task<MediaItem> CreateAsync(
        ChronicleDbContext db, string pluginId, ImportedLibraryEntry entry, CancellationToken ct)
    {
        var typeName = MediaItemMatcher.NormalizeMediaTypeName(entry.MediaType);
        var type = await db.MediaTypes.FirstOrDefaultAsync(t => t.Name == typeName, ct)
            ?? throw new InvalidOperationException($"Media type '{typeName}' not found in database.");

        var item = new MediaItem
        {
            Name = entry.Title, Year = entry.Year, MediaTypeId = type.Id, HierarchyLevel = 0,
            NormalizedName = MediaItemNormalizer.NormalizeName(entry.Title),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, MediaType = type,
        };

        // The platform's whole record sits under the plugin's key, beside the fields the resolver promotes.
        JsonElement raw;
        try { raw = JsonDocument.Parse(entry.RawJson).RootElement.Clone(); }
        catch { raw = JsonSerializer.SerializeToElement(entry.RawJson); }

        var blob = new Dictionary<string, JsonElement>
        {
            [pluginId] = JsonSerializer.SerializeToElement(new
            {
                title       = entry.Title,
                year        = entry.Year,
                overview    = entry.Overview,
                posterUrl   = entry.PosterUrl,
                backdropUrl = entry.BackdropUrl,
                ids         = entry.AdditionalIds,
                tags        = entry.Tags,
                raw,
            }, MetadataEnrichmentService.MetadataBlobOptions),
        };
        item.MetadataJson = JsonSerializer.Serialize(blob);
        if (!string.IsNullOrEmpty(entry.PosterUrl)) item.PosterUrl = entry.PosterUrl;
        await _resolution.ResolveAsync(item, db, ct);

        db.MediaItems.Add(item);
        await db.SaveChangesAsync(ct);

        var source = PluginIdHelper.ToSource(pluginId);
        var allIds = new Dictionary<string, string>(entry.AdditionalIds) { [source] = entry.ExternalId };
        foreach (var (s, v) in allIds)
            db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = item.Id, Source = s, ExternalId = v });

        // Every loaded metadata plugin for this type gets a pending row; the platform's own cheap record is not
        // treated as enrichment, so its metadata role still fetches the full record later.
        foreach (var (mpId, mp, _) in _registry.GetMetadataProviderEntries())
        {
            if (!mp.GetSupportedMediaTypes().Any(t => string.Equals(t.MediaTypeName, typeName, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (await db.MediaEnrichments.AnyAsync(e => e.MediaItemId == item.Id && e.PluginId == mpId, ct)) continue;
            allIds.TryGetValue(PluginIdHelper.ToSource(mpId), out var knownId);
            db.MediaEnrichments.Add(new MediaItemEnrichment
            {
                MediaItemId = item.Id, PluginId = mpId, Status = EnrichmentStatus.Pending, MaxRetries = 3, ExternalId = knownId,
            });
        }

        await db.SaveChangesAsync(ct);
        return item;
    }

    private static async Task GraftAsync(
        ChronicleDbContext db, int itemId, string pluginId, string externalId, CancellationToken ct)
    {
        var source = PluginIdHelper.ToSource(pluginId);
        var existing = await db.MediaExternalIds.FirstOrDefaultAsync(e => e.MediaItemId == itemId && e.Source == source, ct);
        if (existing is null)
            db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = itemId, Source = source, ExternalId = externalId });
        else if (existing.ExternalId != externalId)
            existing.ExternalId = externalId;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>An id without its platform prefix: "steam:32630" and "32630" are the same id.</summary>
    internal static string Bare(string externalId) => StripSource(externalId).Trim();

    private static string StripSource(string externalId)
    {
        var i = externalId.IndexOf(':');
        return i >= 0 ? externalId[(i + 1)..] : externalId;
    }

    /// <summary>Stores the account-level facts a platform reports, whole, under one settings key per plugin.</summary>
    public static async Task StoreAccountInfoAsync(
        ChronicleDbContext db, string pluginId, ImportedAccountInfo info, CancellationToken ct)
    {
        var key = $"account_info.{pluginId}";
        JsonElement raw;
        try { raw = JsonDocument.Parse(info.RawJson).RootElement.Clone(); }
        catch { raw = JsonSerializer.SerializeToElement(info.RawJson); }
        var value = JsonSerializer.Serialize(new
        {
            displayName = info.DisplayName,
            fetchedAt = DateTime.UtcNow,
            raw,
        });
        var row = await db.AppSettings.FindAsync([key], ct);
        if (row is null) db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else row.Value = value;
        await db.SaveChangesAsync(ct);
    }
}
