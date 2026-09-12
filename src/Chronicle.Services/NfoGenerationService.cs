using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Chronicle.Services;

/// <summary>
/// Scheduled task that generates every pending NFO Rebuild Queue item's NFO directly,
/// server-side -- see NfoRebuildQueueItem's own doc for why the queue exists (cataloging every
/// movie/show/episode that needs an NFO), and NfoPushService's own doc for the write-to-disk
/// mechanism this reuses via INfoPushService.TryPushAsync.
///
/// Corrects a real architectural mistake in the queue's original design (caught 2026-09-11):
/// completion depended entirely on SOME Kodi device successfully resolving the item in ITS OWN
/// local VideoLibrary (Chronicle_Scraper's nfo_rebuild.py claim/resolve/delete/refresh flow) --
/// but Chronicle already has everything it needs (title, cast, ratings, art, uniqueid -- all
/// server-side data, reachable via the item's own MetadataJson) to write a correct NFO without
/// any Kodi device's involvement at all. The one thing Kodi structurally contributes that
/// Chronicle can't -- streamdetails, local art files -- is spliced in locally by Kodi's own
/// nfo_writer.py/tv_nfo_writer.py whenever it happens to read/refresh a file (see those
/// modules' own docs); that splice doesn't need to gate whether Chronicle considers the NFO
/// "generated" at all.
///
/// Confirmed live as the actual root cause of a stuck rebuild-queue progress bar: of 3,186
/// pending rows, 3,179 were episodes, which (unlike movies) have no source-browsing fallback --
/// Chronicle_Scraper's get_episode() can only resolve a file via
/// VideoLibrary.GetEpisodes(tvshowid, ...), so a brand-new episode no device had ever scanned
/// could never complete via the old device-driven path, no matter how long it sat in the queue.
///
/// Deliberately coexists with the old claim/complete/release flow rather than replacing it:
/// ClaimBatchAsync already only hands out rows where CompletedAt is null, so once this service
/// completes a row, no device will ever be offered it -- an older, not-yet-updated
/// Chronicle_Scraper install calling claim/complete/release continues to work exactly as
/// before, just increasingly finding less left to do as this service races ahead of it. Nothing
/// server- or client-side needs to change for that coexistence to be safe.
/// </summary>
public sealed class NfoGenerationService : IScheduledTask
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _log = Log.ForContext<NfoGenerationService>();

    // Bounded per run, not "drain the whole backlog in one call" -- a multi-thousand-item
    // backlog would otherwise make a single scheduled tick run for a very long time. Frequent
    // ticks (DefaultCron below) keep making forward progress across successive runs instead.
    private const int BatchSize = 200;

    // Concurrency cap on the fan-out below -- each TryPushAsync call is a loopback HTTP round
    // trip plus a file write, not free; unbounded parallelism across a 200-item batch would just
    // hammer Chronicle's own API and the file system at once for no benefit. Same order of
    // magnitude as ScheduledScanService's own default file-scan concurrency.
    private const int MaxConcurrency = 8;

    public NfoGenerationService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    // ── IScheduledTask ────────────────────────────────────────────────────────

    public string TaskId      => "nfo_generation";
    public string DisplayName => "NFO Generation";
    public string Description => "Writes a fresh NFO directly for every movie/TV item that " +
                                  "doesn't have one on disk yet -- no Kodi device involved.";
    public string DefaultCron => "*/2 * * * *";

    public async Task ExecuteAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var db           = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var rebuildQueue = scope.ServiceProvider.GetRequiredService<INfoRebuildQueueService>();
        var devices      = scope.ServiceProvider.GetRequiredService<IKodiDeviceService>();

        // Root-caused live (2026-09-12): this sweep and an active library scan's own live,
        // per-item NFO pushes routinely landed on the same freshly-discovered item within
        // seconds of each other, each independently rebuilding and writing the identical NFO --
        // pure waste, and it competes with the scan for the exact server/IO capacity the scan
        // needs most. See IKodiDeviceService.IsScanActiveAsync's own doc. Skipping the whole tick
        // (rather than filtering per-item) is deliberate: if a scan is active, NOTHING here is
        // urgent enough to contend with it -- the next tick two minutes later tries again.
        if (await devices.IsScanActiveAsync(ct))
        {
            _log.Debug("NfoGenerationService: a Kodi device is actively scanning -- skipping this tick.");
            return;
        }

        // No specific triggering user for a scheduled backlog pass -- same "first user" system
        // fallback SyncOrchestrationService already uses for its own no-particular-user
        // background writes. Only used to mint the loopback JWT TryPushAsync needs internally;
        // nothing about the generated NFO is user-specific.
        var userId = await db.Users.OrderBy(u => u.Id).Select(u => (int?)u.Id).FirstOrDefaultAsync(ct);
        if (userId is null)
        {
            _log.Information("NfoGenerationService: no users in DB yet -- nothing to do.");
            return;
        }

        var pending = await rebuildQueue.GetPendingForGenerationAsync(BatchSize, ct);
        if (pending.Count == 0)
        {
            _log.Debug("NfoGenerationService: nothing pending.");
            return;
        }

        int generated = 0, failed = 0, stillPending = 0, errored = 0;
        using var semaphore = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);

        // Every item's work is wrapped in its own try/catch below -- TryPushAsync throws for
        // anything outside its own internal (HTTP call, file write) try/catches, e.g. a
        // transient DB blip or a device-refresh fan-out failure. Without this, one item's
        // exception would fault this Task.WhenAll and abort the whole batch (including the
        // summary log below never running), turning one bad row into a false "run failed" for
        // 199 other items that processed just fine.
        await Task.WhenAll(pending.Select(async row =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                // Own scope per parallel unit -- ChronicleDbContext (and anything holding one,
                // like INfoPushService/INfoRebuildQueueService) isn't safe for concurrent use,
                // same reasoning as ScheduledScanService.PreviewFolderAsync's own per-task scope.
                using var itemScope    = _scopeFactory.CreateScope();
                var itemDb             = itemScope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                var itemNfoPush        = itemScope.ServiceProvider.GetRequiredService<INfoPushService>();
                var itemRebuildQueue   = itemScope.ServiceProvider.GetRequiredService<INfoRebuildQueueService>();

                var outcome = await itemNfoPush.TryPushAsync(row.MediaItemId, userId.Value, ct);
                if (outcome == true)
                {
                    await itemRebuildQueue.CompleteFromGenerationAsync(row.Id, ct);
                    Interlocked.Increment(ref generated);
                }
                else if (outcome == false)
                {
                    // Left pending -- TryPushAsync already logged the specific failure. Retried
                    // on the next scheduled run rather than given up on here.
                    Interlocked.Increment(ref failed);
                }
                else
                {
                    // Null: genuinely nothing to push right now (most commonly "no known
                    // on-disk location yet"). Auto-complete only if the underlying item is
                    // actually gone -- same "don't strand a dangling row forever" reasoning as
                    // NfoRebuildQueueService.BuildClaimDtosAsync's own missing-item handling --
                    // otherwise leave it pending so a later file-scanner pass can still resolve it.
                    var stillExists = await itemDb.MediaItems.AnyAsync(m => m.Id == row.MediaItemId, ct);
                    if (!stillExists)
                        await itemRebuildQueue.CompleteFromGenerationAsync(row.Id, ct);
                    else
                        Interlocked.Increment(ref stillPending);
                }
            }
            catch (OperationCanceledException)
            {
                throw; // genuine shutdown/cancellation -- not this item's fault, don't count or swallow it
            }
            catch (Exception ex)
            {
                _log.Warning(ex,
                    "NfoGenerationService: unexpected error generating item {MediaItemId} " +
                    "(queue row {QueueItemId}) -- left pending, will retry next run.",
                    row.MediaItemId, row.Id);
                Interlocked.Increment(ref errored);
            }
            finally
            {
                semaphore.Release();
            }
        }));

        _log.Information(
            "NfoGenerationService: processed {Total} pending item(s) -- {Generated} generated, " +
            "{Failed} failed, {Errored} errored (both will retry), {StillPending} skipped (no known location yet).",
            pending.Count, generated, failed, errored, stillPending);
    }
}
