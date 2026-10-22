using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services.Export;

/// <param name="Skipped">Why nothing was exported (null when an export ran).</param>
public record ExportSummary(
    string PluginId,
    string? Skipped,
    bool PreviewOnly,
    int WatchesPlanned,
    int RatingsPlanned,
    int ListStatusesPlanned,
    ExportPushResult? Result);

public interface IExportService
{
    /// <summary>
    /// Reconciles Chronicle's records with the service behind <paramref name="pluginId"/> and pushes what the
    /// service is missing. Runs on its own after each import; never throws (a failed export must not fail the
    /// import it follows) and never deletes anything on the service.
    /// </summary>
    Task<ExportSummary> ExportAsync(string pluginId, int? userId = null, CancellationToken ct = default);
}

public class ExportService : IExportService
{
    private const int Chunk = 400;     // below SQLite's host-parameter limit for IN (...) lists

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPluginRegistry _registry;
    private readonly ILogger<ExportService> _log;

    public ExportService(IServiceScopeFactory scopeFactory, IPluginRegistry registry, ILogger<ExportService> log)
    {
        _scopeFactory = scopeFactory;
        _registry     = registry;
        _log          = log;
    }

    public async Task<ExportSummary> ExportAsync(string pluginId, int? userId = null, CancellationToken ct = default)
    {
        var import = _registry.GetImportProvider(pluginId);
        if (import is not IExportProvider provider)
            return Skipped(pluginId, "the plugin cannot export");

        try
        {
            var options = provider.GetExportOptions();
            if (!options.Enabled) return Skipped(pluginId, "export is switched off in the plugin's settings");
            if (!await import.IsAuthenticatedAsync(ct)) return Skipped(pluginId, "the plugin is not authenticated");

            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

            var resolvedUserId = userId
                ?? await db.Users.OrderBy(u => u.Id).Select(u => (int?)u.Id).FirstOrDefaultAsync(ct);
            if (resolvedUserId is null or 0) return Skipped(pluginId, "there is no user to export for");

            var caps = provider.GetExportCapabilities();

            // Skip the whole run when neither side has changed since the last complete one (services ask clients
            // to gate full library reads on a cheap "anything changed?" value).
            var fingerprint = await provider.GetRemoteFingerprintAsync(ct);
            var localMark = await LocalChangeMarkAsync(db, resolvedUserId.Value, ct);
            if (fingerprint is not null && !options.PreviewOnly
                && await GateMatchesAsync(db, pluginId, fingerprint, localMark, ct))
                return Skipped(pluginId, "nothing has changed on either side since the last complete export");
            var importSources = _registry.GetImportProviders()
                .Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // The service's own records first. A partial read must never be compared against, so a failure
            // here ends the run (GetRemoteStateAsync throws rather than return something incomplete).
            var remote = await provider.GetRemoteStateAsync(ct);

            var exportScope = await ExportScope.LoadAsync(db, ct);
            var (movieTypeIds, showTypeIds) = await exportScope.TypeIdsAsync(db, ct);
            var local = new List<LocalItem>();
            local.AddRange(await GatherMoviesAsync(db, resolvedUserId.Value, importSources, movieTypeIds, ct));
            local.AddRange(await GatherEpisodesAsync(db, resolvedUserId.Value, importSources, showTypeIds, ct));

            var batch = ExportPlanner.Plan(local, remote, caps, options);
            _log.LogInformation(
                "Export to {PluginId}: {Items} local items compared; {Watches} watches ({Rewatches} rewatches), {Ratings} ratings, {Statuses} list statuses to send{Preview}",
                pluginId, local.Count, batch.Watches.Count, batch.Watches.Count(w => w.IsRewatch),
                batch.Ratings.Count, batch.ListStatuses.Count, options.PreviewOnly ? " (preview only - nothing sent)" : "");

            if (options.PreviewOnly || batch.IsEmpty)
            {
                if (options.PreviewOnly) LogPlan(pluginId, batch);
                var previewSummary = new ExportSummary(pluginId, null, options.PreviewOnly,
                    batch.Watches.Count, batch.Ratings.Count, batch.ListStatuses.Count, null);
                await RecordRunAsync(db, previewSummary, ct);
                if (batch.IsEmpty && !options.PreviewOnly && fingerprint is not null)
                    await StoreGateAsync(db, pluginId, fingerprint, localMark, ct);      // already in step
                return previewSummary;
            }

            var result = await provider.PushAsync(batch, ct);
            _log.LogInformation(
                "Export to {PluginId} sent: {Watches} watches, {Ratings} ratings, {Statuses} list statuses; {NotFound} items the service could not match; {Deferred} left for the next run; {Skipped} the service cannot take; {Errors} errors",
                pluginId, result.WatchesPushed, result.RatingsPushed, result.ListStatusesPushed, result.NotFound, result.Deferred, result.Skipped, result.Errors.Count);
            foreach (var error in result.Errors.Take(20))
                _log.LogWarning("Export to {PluginId}: {Error}", pluginId, error);

            var summary = new ExportSummary(pluginId, null, false,
                batch.Watches.Count, batch.Ratings.Count, batch.ListStatuses.Count, result);
            await RecordRunAsync(db, summary, ct);

            // Remember "all caught up" only after a complete, error-free run. Our own pushes change the service's
            // fingerprint, so read it again rather than keep the one from before the push.
            if (result.Deferred == 0 && result.Errors.Count == 0 && fingerprint is not null)
                await StoreGateAsync(db, pluginId, await provider.GetRemoteFingerprintAsync(ct) ?? fingerprint, localMark, ct);
            return summary;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Export to {PluginId} failed - nothing further was sent", pluginId);
            return Skipped(pluginId, $"the export failed: {ex.Message}");
        }
        finally
        {
            await PersistRefreshedSettingsAsync(import, pluginId);
        }
    }

    /// <summary>
    /// A service that renews its login while exporting (SIMKL AUTH V2 refresh tokens) hands the new tokens back here.
    /// The old access token stops working the moment it is renewed, so they are saved even when the run failed.
    /// Deliberately not tied to the caller's cancellation: a cancelled run must not lose a token that was just renewed.
    /// </summary>
    private async Task PersistRefreshedSettingsAsync(IImportProvider? import, string pluginId)
    {
        try
        {
            var refreshed = import?.GetRefreshedSettings();
            if (refreshed is not { Count: > 0 }) return;
            await using var scope = _scopeFactory.CreateAsyncScope();
            var plugins = scope.ServiceProvider.GetRequiredService<IPluginService>();
            _log.LogInformation("Persisting refreshed login tokens for {PluginId}", pluginId);
            await plugins.MergeSettingsAsync(pluginId, refreshed, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not save the refreshed login tokens for {PluginId}", pluginId);
        }
    }

    private static ExportSummary Skipped(string pluginId, string why) => new(pluginId, why, false, 0, 0, 0, null);

    // ── the "nothing changed" gate ────────────────────────────────────────────

    private static async Task<string> LocalChangeMarkAsync(ChronicleDbContext db, int userId, CancellationToken ct)
    {
        var lastEvent = await db.InteractionEvents.Where(e => e.UserId == userId)
            .MaxAsync(e => (DateTime?)e.CreatedAt, ct);
        var lastLibrary = await db.UserLibraries.Where(l => l.UserId == userId)
            .MaxAsync(l => (DateTime?)l.UpdatedAt, ct);
        return $"{lastEvent:O}|{lastLibrary:O}";
    }

    private static string GateKey(string pluginId) => $"export_state.{pluginId}.gate";

    private static async Task<bool> GateMatchesAsync(
        ChronicleDbContext db, string pluginId, string fingerprint, string localMark, CancellationToken ct)
    {
        var stored = await db.AppSettings.FindAsync([GateKey(pluginId)], ct);
        return stored?.Value == $"{fingerprint}#{localMark}";
    }

    private static async Task StoreGateAsync(
        ChronicleDbContext db, string pluginId, string fingerprint, string localMark, CancellationToken ct)
    {
        var key = GateKey(pluginId);
        var value = $"{fingerprint}#{localMark}";
        var setting = await db.AppSettings.FindAsync([key], ct);
        if (setting is null) db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else setting.Value = value;
        await db.SaveChangesAsync(ct);
    }

    private void LogPlan(string pluginId, ExportBatch batch)
    {
        foreach (var w in batch.Watches.Take(100))
            _log.LogInformation("Export preview {PluginId}: {Kind} {Title} at {At:O}",
                pluginId, w.IsRewatch ? "rewatch" : "watch", Describe(w.Item), w.WatchedAt);
        foreach (var r in batch.Ratings.Take(100))
            _log.LogInformation("Export preview {PluginId}: rating {Rating} for {Title}", pluginId, r.Rating, Describe(r.Item));
        foreach (var s in batch.ListStatuses.Take(100))
            _log.LogInformation("Export preview {PluginId}: status {Status} for {Title}", pluginId, s.Status, Describe(s.Item));
    }

    private static string Describe(ExportItemRef item) =>
        item.MediaType == "tv_episode"
            ? $"{item.ShowTitle} S{item.SeasonNumber:00}E{item.EpisodeNumber:00}"
            : $"{item.Title} ({item.Year})";

    private static async Task RecordRunAsync(ChronicleDbContext db, ExportSummary summary, CancellationToken ct)
    {
        var key = $"export_state.{summary.PluginId}.last_run";
        var value = JsonSerializer.Serialize(new
        {
            at = DateTimeOffset.UtcNow,
            summary.PreviewOnly,
            summary.WatchesPlanned,
            summary.RatingsPlanned,
            summary.ListStatusesPlanned,
            sent = summary.Result is null ? null : new
            {
                summary.Result.WatchesPushed, summary.Result.RatingsPushed, summary.Result.ListStatusesPushed,
                summary.Result.NotFound, errors = summary.Result.Errors.Count,
            },
        });
        var setting = await db.AppSettings.FindAsync([key], ct);
        if (setting is null) db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else setting.Value = value;
        await db.SaveChangesAsync(ct);
    }

    // ── gathering Chronicle's own records ─────────────────────────────────────

    private static string? MapListStatus(LibraryStatus status) => status switch
    {
        LibraryStatus.PlanToWatch => "plantowatch",
        LibraryStatus.OnHold      => "hold",
        LibraryStatus.Dropped     => "dropped",
        _                         => null,
    };

    private static async Task<List<LocalItem>> GatherMoviesAsync(
        ChronicleDbContext db, int userId, ISet<string> importSources, List<int> movieTypeIds, CancellationToken ct)
    {
        if (movieTypeIds.Count == 0) return [];

        // A movie is anything of a movie type that has no children. Movies inside a collection are level-1 children
        // of the collection's container (2,053 of them in the real library), and the container itself - a parent -
        // is a folder, not a movie: it must never be exported, however it was named or id'd.
        var libraries = await (
            from l in db.UserLibraries.AsNoTracking()
            join m in db.MediaItems.AsNoTracking() on l.MediaItemId equals m.Id
            where l.UserId == userId && movieTypeIds.Contains(m.MediaTypeId)
                  && !db.MediaItems.Any(child => child.ParentId == m.Id)
            select new { Library = l, Item = new ItemRow(m.Id, m.Name, m.Year) }).ToListAsync(ct);

        return await BuildAsync(db, userId, importSources,
            libraries.Select(x => (x.Library, x.Item)).ToList(), ct, episodeContext: null);
    }

    private static async Task<List<LocalItem>> GatherEpisodesAsync(
        ChronicleDbContext db, int userId, ISet<string> importSources, List<int> tvTypeIds, CancellationToken ct)
    {
        if (tvTypeIds.Count == 0) return [];

        var rows = await (
            from l in db.UserLibraries.AsNoTracking()
            join e in db.MediaItems.AsNoTracking() on l.MediaItemId equals e.Id
            where l.UserId == userId && tvTypeIds.Contains(e.MediaTypeId) && e.HierarchyLevel == 2 && e.ParentId != null
            select new { Library = l, EpisodeId = e.Id, e.Name, e.Number, SeasonId = e.ParentId!.Value }).ToListAsync(ct);
        if (rows.Count == 0) return [];

        var seasonIds = rows.Select(r => r.SeasonId).Distinct().ToList();
        var seasons = new Dictionary<int, (int? Number, int? ShowId)>();
        foreach (var chunk in seasonIds.Chunk(Chunk))
            foreach (var s in await db.MediaItems.AsNoTracking().Where(m => chunk.Contains(m.Id))
                         .Select(m => new { m.Id, m.Number, m.ParentId }).ToListAsync(ct))
                seasons[s.Id] = (s.Number is { } n ? (int?)n : null, s.ParentId);

        var showIds = seasons.Values.Where(s => s.ShowId.HasValue).Select(s => s.ShowId!.Value).Distinct().ToList();
        var shows = new Dictionary<int, ItemRow>();
        foreach (var chunk in showIds.Chunk(Chunk))
            foreach (var s in await db.MediaItems.AsNoTracking().Where(m => chunk.Contains(m.Id))
                         .Select(m => new ItemRow(m.Id, m.Name, m.Year)).ToListAsync(ct))
                shows[s.Id] = s;

        var episodeContext = new Dictionary<int, EpisodeContext>();
        var items = new List<(UserLibrary Library, ItemRow Item)>();
        foreach (var r in rows)
        {
            if (!seasons.TryGetValue(r.SeasonId, out var season) || season.Number is null
                || season.ShowId is null || !shows.TryGetValue(season.ShowId.Value, out var show)
                || r.Number is null)
                continue;
            episodeContext[r.EpisodeId] = new EpisodeContext(show, season.Number.Value, (int)r.Number.Value);
            items.Add((r.Library, new ItemRow(r.EpisodeId, r.Name, show.Year)));
        }

        return await BuildAsync(db, userId, importSources, items, ct, episodeContext);
    }

    private sealed record ItemRow(int Id, string Name, int? Year);
    private sealed record EpisodeContext(ItemRow Show, int Season, int Episode);
    private sealed record EventRow(int MediaItemId, DateTime Timestamp, double? ProgressPercent,
        bool MarkedAsWatched, string? DeviceName, bool IsApproximate);

    /// <summary>Builds the comparable items; for an episode the ids are the SHOW's.</summary>
    private static async Task<List<LocalItem>> BuildAsync(
        ChronicleDbContext db, int userId, ISet<string> importSources,
        List<(UserLibrary Library, ItemRow Item)> items, CancellationToken ct,
        Dictionary<int, EpisodeContext>? episodeContext)
    {
        if (items.Count == 0) return [];

        // Ids belong to the movie, or to the show for an episode.
        var idOwner = items.Select(i => episodeContext is not null ? episodeContext[i.Item.Id].Show.Id : i.Item.Id)
            .Distinct().ToList();
        var idsByOwner = new Dictionary<int, Dictionary<string, string>>();
        foreach (var chunk in idOwner.Chunk(Chunk))
            foreach (var e in await db.MediaExternalIds.AsNoTracking()
                         .Where(x => chunk.Contains(x.MediaItemId)
                                     && ExportKeys.Sources.Contains(x.Source))
                         .Select(x => new { x.MediaItemId, x.Source, x.ExternalId }).ToListAsync(ct))
            {
                if (!idsByOwner.TryGetValue(e.MediaItemId, out var d))
                    idsByOwner[e.MediaItemId] = d = new Dictionary<string, string>();
                d.TryAdd(e.Source, e.ExternalId);
            }

        var eventsByItem = new Dictionary<int, List<LocalEvent>>();
        foreach (var chunk in items.Select(i => i.Item.Id).Distinct().Chunk(Chunk))
            foreach (var e in await db.InteractionEvents.AsNoTracking()
                         .Where(x => x.UserId == userId && chunk.Contains(x.MediaItemId))
                         .Select(x => new EventRow(x.MediaItemId, x.Timestamp, x.ProgressPercent,
                             x.MarkedAsWatched, x.DeviceName, x.IsApproximateTimestamp)).ToListAsync(ct))
            {
                if (!eventsByItem.TryGetValue(e.MediaItemId, out var list))
                    eventsByItem[e.MediaItemId] = list = [];
                list.Add(new LocalEvent(DateTime.SpecifyKind(e.Timestamp, DateTimeKind.Utc),
                    e.ProgressPercent ?? 0, e.MarkedAsWatched, e.DeviceName, e.IsApproximate));
            }

        var result = new List<LocalItem>();
        foreach (var (library, item) in items)
        {
            var ownerId = episodeContext is not null ? episodeContext[item.Id].Show.Id : item.Id;
            if (!idsByOwner.TryGetValue(ownerId, out var ids)) continue;

            var completed = library.Status is LibraryStatus.Completed or LibraryStatus.Rewatching;
            var derived = WatchDeriver.Derive(
                eventsByItem.TryGetValue(item.Id, out var ev) ? ev : [],
                importSources, completed,
                library.CompletedAt is { } c ? DateTime.SpecifyKind(c, DateTimeKind.Utc) : null);

            ExportItemRef reference;
            if (episodeContext is not null)
            {
                var ctx = episodeContext[item.Id];
                reference = new ExportItemRef("tv_episode", item.Name, ctx.Show.Year, ids, ctx.Show.Name, ctx.Season, ctx.Episode);
            }
            else
            {
                reference = new ExportItemRef("movie", item.Name, item.Year, ids);
            }

            result.Add(new LocalItem(
                reference, derived.Playbacks, derived.FirstWatchEvidence,
                episodeContext is null ? library.UserRating : null,        // SIMKL rates movies and shows, not episodes
                library.UserRatingUpdatedAt is { } r ? DateTime.SpecifyKind(r, DateTimeKind.Utc) : null,
                completed || derived.Playbacks.Count > 0 || derived.FirstWatchEvidence is not null,
                episodeContext is null ? MapListStatus(library.Status) : null));    // a list status belongs to the movie or show, not an episode
        }
        return result;
    }
}
