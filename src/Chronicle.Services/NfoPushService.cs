using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// Builds this item's NFO the exact same way the sidecar endpoints already do (an internal,
/// JWT-authenticated loopback call to Chronicle's own API -- see the "why loopback" note below),
/// writes it straight to the item's own on-disk location, then pushes a targeted
/// VideoLibrary.Refresh* to every Kodi instance that already knows this item's internal id.
///
/// Why a loopback HTTP call instead of calling the sidecar-building logic directly: that logic
/// (ScraperController's BuildMovieDetailsDtoAsync/BuildShowDetailsDtoAsync/
/// BuildEpisodeDetailsDtoAsync plus the DTO-to-ResolvedXData mapping) is ~800 lines of
/// already-shipped, already-verified-live controller code with its own web of internal helpers.
/// Re-deriving or relocating all of that to share it with this service would be a large,
/// separately-risky refactor of working code; a single internal HTTP round-trip on
/// localhost is cheap and keeps this feature's own new, unverified code isolated from it.
/// Authenticated with a freshly minted JWT for the item's owning user (Chronicle already mints
/// these the same way for every browser login) rather than any new internal-only bypass.
/// </summary>
public sealed class NfoPushService(
    ChronicleDbContext db,
    IJwtTokenService jwt,
    IHttpClientFactory httpClientFactory,
    IKodiDeviceService devices,
    IKodiRpcClient rpc,
    ILogger<NfoPushService> logger) : INfoPushService
{
    /// <summary>Stable id this service's own row lives under in the shared background_tasks
    /// table -- see RecordStatusAsync's own doc for why this reuses that table/UI rather than
    /// building a separate one.</summary>
    private const string StatusTaskId = "nfo-push";

    public async Task PushAsync(int mediaItemId, int userId, CancellationToken ct = default)
    {
        try
        {
            var outcome = await PushCoreAsync(mediaItemId, userId, ct);
            if (outcome.HasValue)
                await RecordStatusAsync(outcome.Value, mediaItemId, null, ct);
        }
        catch (OperationCanceledException)
        {
            // Routine (the caller's own ct was cancelled -- e.g. app shutdown mid-push), not a
            // failure -- logged at Info with no stack trace, unlike the genuine-failure case
            // below, so a client disconnect doesn't read as a bug when triaging logs later.
            logger.LogInformation(
                "NfoPushService: push for media item {Id} was cancelled -- the next " +
                "scheduled/manual NFO rebuild will still pick this up.", mediaItemId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "NfoPushService: push failed for media item {Id} -- the next scheduled/manual " +
                "NFO rebuild will still pick this up.", mediaItemId);
            await RecordStatusAsync(false, mediaItemId, ex.Message, ct);
        }
    }

    /// <summary>
    /// Surfaces this service's otherwise-invisible fire-and-forget activity in Chronicle's own
    /// UI, by reusing the existing generic Background Tasks page/table (BackgroundTasksPage.tsx,
    /// background_tasks) rather than building a separate live-activity channel from scratch --
    /// per-edit/-rating/-watch pushes are far too frequent and short-lived to justify their own
    /// UI, but a single rolling "last push" row lets a user actually SEE this is happening at
    /// all, which was a real, reported gap (2026-09-06): "There is no obvious way that NFOs are
    /// being written... this needs to be a background task."
    ///
    /// Deliberately NOT registered as an IScheduledTask: this isn't cron-scheduled or manually
    /// re-runnable (there's nothing meaningful to "Run Now" -- a push only ever fires in
    /// response to a real edit/rating/watch event), so IsEnabled/Schedulable are both false --
    /// TaskSchedulerService's own tick loop filters on IsEnabled and so never touches this row,
    /// and the frontend hides the Schedule button for Schedulable=false. The one known rough
    /// edge: BackgroundTasksController still shows a "Run Now" button (nothing in the schema
    /// distinguishes "exists but not manually triggerable" from "runnable"), which returns
    /// TASK_NOT_FOUND if clicked since no IScheduledTask is registered under this id --
    /// acceptable given how rarely anyone would click it on a row already showing live status.
    ///
    /// outcome=null (nothing recorded -- see PushCoreAsync's own doc) covers "nothing to push"
    /// cases (item not found, unsupported kind, no known on-disk location yet) that aren't a
    /// meaningful "did the background task run" signal and would just spam the Last Run
    /// timestamp for events where nothing actually happened.
    /// </summary>
    private async Task RecordStatusAsync(bool succeeded, int mediaItemId, string? errorDetail, CancellationToken ct)
    {
        try
        {
            var row = await db.BackgroundTasks.FindAsync([StatusTaskId], ct);
            var isNew = row is null;
            row ??= new BackgroundTask
            {
                TaskId      = StatusTaskId,
                DisplayName = "NFO Push",
                Description = "Writes a fresh NFO and pushes a targeted library refresh to " +
                              "every Kodi device that already knows an item, whenever that " +
                              "item is edited, rated, or marked watched in Chronicle. Fires " +
                              "automatically on those events -- not on a schedule, and " +
                              "nothing to manually run here.",
                CronExpression = "0 0 1 1 *", // never used: IsEnabled stays false, see class doc
                IsEnabled      = false,
                Schedulable    = false,
            };
            if (isNew) db.BackgroundTasks.Add(row);

            row.LastRunAt        = DateTime.UtcNow;
            row.LastRunSucceeded = succeeded;
            row.LastErrorMessage = succeeded ? null : (errorDetail ?? $"Push failed for media item {mediaItemId} -- see server logs.");

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) when (isNew)
            {
                // Lost a race with a concurrent push's own first-ever RecordStatusAsync call
                // (e.g. a rating and an edit landing near-simultaneously, each on its own DI
                // scope/DbContext) -- same shape and fix as KodiDeviceService.RegisterAsync's
                // identical race on api_token_id. Detach our failed insert and update the row
                // that won instead, rather than losing this call's status update entirely.
                db.Entry(row).State = EntityState.Detached;
                var existing = await db.BackgroundTasks.FirstAsync(t => t.TaskId == StatusTaskId, ct);
                existing.LastRunAt        = DateTime.UtcNow;
                existing.LastRunSucceeded = succeeded;
                existing.LastErrorMessage = succeeded ? null : (errorDetail ?? $"Push failed for media item {mediaItemId} -- see server logs.");
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "NfoPushService: couldn't record push status for visibility.");
        }
    }

    /// <summary>Returns null when there was genuinely nothing to push (item/type not found,
    /// unsupported kind, no on-disk location known yet) -- these aren't a background-task
    /// outcome worth recording, see RecordStatusAsync's own doc. Returns true/false once actual
    /// push work was attempted (an NFO write, at minimum).</summary>
    private async Task<bool?> PushCoreAsync(int mediaItemId, int userId, CancellationToken ct)
    {
        var item = await db.MediaItems.FindAsync([mediaItemId], ct);
        if (item is null) return null;

        var mediaType = await db.MediaTypes.FindAsync([item.MediaTypeId], ct);
        if (mediaType is null) return null;

        var kind = NfoKindHelper.Classify(mediaType.Name, item.HierarchyLevel);
        if (kind is null)
            return null; // not a pushable kind: person, music, season container, collection, etc.

        var sidecarPath = kind switch
        {
            "movie"   => $"/api/v1/scraper/movies/sidecar?id={mediaItemId}",
            "tvshow"  => $"/api/v1/scraper/tv/sidecar?id={mediaItemId}",
            "episode" => $"/api/v1/scraper/tv/episode-sidecar?id={mediaItemId}",
            _         => throw new InvalidOperationException($"Unhandled NFO kind '{kind}'.")
        };

        var (folderPath, filePaths, nfoPath) = ReadFileScannerLocation(item.MetadataJson);
        var destPath = nfoPath ?? DeriveNfoPath(kind, folderPath, filePaths);
        if (destPath is null)
        {
            logger.LogInformation(
                "NfoPushService: item {Id} has no known on-disk location yet -- nothing to push.", mediaItemId);
            return null;
        }

        var user = await db.Users.FindAsync([userId], ct);
        if (user is null) return null;
        var token = jwt.GenerateToken(user);

        var client = httpClientFactory.CreateClient("internal-loopback");
        using var request = new HttpRequestMessage(HttpMethod.Get, sidecarPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "NfoPushService: couldn't reach Chronicle's own sidecar endpoint for item {Id}.", mediaItemId);
            return false;
        }
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "NfoPushService: sidecar build for item {Id} returned HTTP {Status}.",
                mediaItemId, (int)response.StatusCode);
            return false;
        }
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);

        try
        {
            // Write-then-rename, not a direct write to the final path: destPath may be an
            // already-imported item's existing, good NFO. A direct write left mid-flight by a
            // cancelled request or a killed process would leave that file truncated on disk --
            // the same failure class as the earlier Chronicle_Scraper art-corruption saga.
            // File.Move's rename is effectively atomic on both Windows and Linux for a same-
            // volume destination (true here: the temp file is a sibling in the same folder).
            var tempPath = destPath + ".chronicle-tmp";
            await File.WriteAllBytesAsync(tempPath, bytes, ct);
            File.Move(tempPath, destPath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Logged and left at that -- not surfaced as a task failure. This is almost always
            // an environmental condition outside Chronicle's control (the item's drive is
            // offline, a share dropped, the folder was deleted) rather than a bug in the push
            // itself, and there's nothing actionable a user could do from a permanent red
            // "FAILED" badge that just sits there until some unrelated future push happens to
            // succeed. Per-user correction (2026-09-11): "I don't want to see an unexplained
            // error in the UI. log it and move on." Returning null (not false) means
            // RecordStatusAsync is skipped entirely -- the task's last-known status is left
            // exactly as it was rather than being overwritten with this one item's failure.
            logger.LogWarning(ex, "NfoPushService: couldn't write NFO for item {Id} to {Path}.", mediaItemId, destPath);
            return null;
        }
        logger.LogInformation("NfoPushService: wrote NFO for item {Id} to {Path}.", mediaItemId, destPath);

        var targets = await devices.GetPushTargetsAsync(mediaItemId, ct);
        // Fanned out concurrently, not one device at a time: KodiRpcClient.RefreshAsync already
        // caps each call at its own 8s timeout, but a sequential loop would pay that timeout
        // once PER unreachable device instead of once total.
        await Task.WhenAll(targets.Select(async t =>
        {
            var (device, mapping) = t;
            var ok = await rpc.RefreshAsync(device, mapping.Kind, mapping.KodiId, ct);
            logger.LogInformation(
                "NfoPushService: refresh push to device {Device} ({Host}) for item {Id} -- {Result}.",
                device.Name, device.Host, mediaItemId, ok ? "accepted" : "failed");
        }));

        // The NFO write itself succeeding is the meaningful outcome here, even if targets is
        // empty (this device has no Kodi devices registered against it yet -- e.g. before this
        // item's first NFO-rebuild pass has ever run, see kodi_library_ids/report_kodi_id) --
        // that's not a push failure, just nothing left to fan out to yet.
        return true;
    }

    private static (string? FolderPath, List<string>? FilePaths, string? NfoPath) ReadFileScannerLocation(
        string? metadataJson)
    {
        // filePaths goes through FileIdentityJson.ExtractFilePaths -- the codebase's own
        // "single canonical reader for physical-file identity" (see that method's doc) -- rather
        // than a second hand-rolled copy of the same fileScanner.filePaths shape. folderPath/
        // nfoPath have no equivalent shared helper today, so those two stay bespoke here.
        var filePaths = Chronicle.Services.Scan.FileIdentityJson.ExtractFilePaths(metadataJson).ToList();

        if (string.IsNullOrEmpty(metadataJson)) return (null, filePaths.Count > 0 ? filePaths : null, null);
        try
        {
            if (JsonNode.Parse(metadataJson)?.AsObject()["fileScanner"] is not JsonObject fs)
                return (null, filePaths.Count > 0 ? filePaths : null, null);

            var folderPath = fs["folderPath"]?.GetValue<string>();
            var nfoPath     = fs["nfoPath"]?.GetValue<string>();
            return (folderPath, filePaths.Count > 0 ? filePaths : null, nfoPath);
        }
        catch (Exception)
        {
            // Never let a malformed/unexpected fileScanner shape (JsonException from Parse, or
            // InvalidOperationException from AsObject()/GetValue<string>() hitting the wrong
            // JSON kind) escape this best-effort lookup -- PushAsync's own outer catch would
            // still save the request, but "no location yet" is a much clearer log line than a
            // stack trace for what's ultimately just missing/odd metadata.
            return (null, filePaths.Count > 0 ? filePaths : null, null);
        }
    }

    /// <summary>Same naming convention Chronicle_Scraper's nfo_writer.py/tv_nfo_writer.py use:
    /// "tvshow.nfo" for a show's own root, the real video file's own basename otherwise.
    /// Only reached when this item has never had an NFO before (nfoPath unset) -- an
    /// already-scraped item's own recorded nfoPath is always preferred over re-deriving this.</summary>
    private static string? DeriveNfoPath(string kind, string? folderPath, List<string>? filePaths)
    {
        if (string.IsNullOrEmpty(folderPath)) return null;
        if (kind == "tvshow") return Path.Combine(folderPath, "tvshow.nfo");

        var videoFile = filePaths?.FirstOrDefault();
        if (string.IsNullOrEmpty(videoFile)) return null;
        var stem = Path.GetFileNameWithoutExtension(videoFile);
        return Path.Combine(folderPath, stem + ".nfo");
    }
}
