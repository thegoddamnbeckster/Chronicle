using Chronicle.Core.Models;

namespace Chronicle.Services;

public interface IKodiDeviceService
{
    /// <summary>Upserts the calling device's own row, keyed by apiTokenId (see KodiDevice's
    /// own doc for why). Called by Chronicle_Scraper's device_registration.py on pairing and
    /// periodically thereafter, so a changed LAN IP or webserver setting doesn't leave a stale
    /// row Chronicle keeps failing to reach.</summary>
    Task RegisterAsync(int userId, int apiTokenId, string name, string host, int port,
        string? username, string? password, CancellationToken ct = default);

    /// <summary>Records/updates this device's own Kodi-internal id for one MediaItem -- see
    /// KodiLibraryId's own doc. A no-op (not an error) if apiTokenId has no registered device
    /// yet (e.g. remote control is off on that Kodi instance) -- there's simply nothing to push
    /// to for it regardless.</summary>
    Task RecordKodiIdAsync(int apiTokenId, int mediaItemId, string kind, int kodiId, CancellationToken ct = default);

    /// <summary>Resolves an API token to its own registered KodiDevice.Id, or null if that
    /// token has no device registered yet (e.g. remote control is off on that Kodi instance).
    /// The single place this lookup lives -- callers (KodiDeviceController's rebuild-queue
    /// endpoints) should use this rather than querying KodiDevices directly.</summary>
    Task<int?> GetDeviceIdForApiTokenAsync(int apiTokenId, CancellationToken ct = default);

    /// <summary>Records that Chronicle imported at least one new movie/TV item, so any Kodi
    /// device that hasn't scanned since is due for one -- see IsScanNeededAsync's own doc for
    /// the pull side of this. mediaTypeName is checked against NfoKindHelper.IsVideoLibraryType
    /// (a music/book/etc. import has nothing for Kodi's video library to discover); a
    /// non-video-library name is a silent no-op. Global, not per-device or per-media-type: one
    /// app_settings timestamp, since a full local VideoLibrary.Scan (the only thing achievable
    /// with no directory param -- see this feature's own design doc) covers everything on a
    /// device regardless of which media type triggered it.</summary>
    Task SignalNewContentAsync(string mediaTypeName, CancellationToken ct = default);

    /// <summary>True if the global "new content" signal (see SignalNewContentAsync) is newer
    /// than this caller's own last acknowledged scan -- i.e. this device is due to run its own
    /// local VideoLibrary.Scan. Keyed directly by apiTokenId, NOT by KodiDevice/KodiDeviceId --
    /// see KodiScanAck's own doc for why: a KodiDevice row only exists once "Allow remote
    /// control via HTTP" has been turned on and device_registration.py has self-registered, but
    /// this feature has no such requirement, so gating it on that row's existence would silently
    /// make the whole feature depend on a setting it's specifically designed not to need.</summary>
    Task<bool> IsScanNeededAsync(int apiTokenId, CancellationToken ct = default);

    /// <summary>Records that this caller just performed its own local VideoLibrary.Scan in
    /// response to the signal -- called after the addon's own xbmc.executeJSONRPC call, not
    /// before, so a device that crashes or loses network mid-scan is still considered due next
    /// poll rather than wrongly marked caught-up. Creates the KodiScanAck row on first call --
    /// no prior registration required.</summary>
    Task AcknowledgeScanAsync(int apiTokenId, CancellationToken ct = default);

    /// <summary>Renews a short, self-expiring "some Kodi device is actively scanning right now"
    /// flag -- see IsScanActiveAsync's own doc for what it gates. Called by the addon's own
    /// xbmc.Monitor.onScanStarted() hook, then again periodically for the duration of a long
    /// scan (there is no onScanProgress callback in Kodi's own API), so the flag keeps renewing
    /// itself for as long as a scan is genuinely still running. Deliberately a single global TTL
    /// rather than per-device state or an explicit "finished" signal: with up to five Kodi
    /// instances sharing one library, one device finishing first must never prematurely resume
    /// generation while another is still mid-scan, and a device that crashes mid-scan must not
    /// leave the flag stuck forever -- letting it simply expire a couple of minutes after the
    /// last heartbeat handles both without any cross-device coordination.</summary>
    Task ReportScanActivityAsync(CancellationToken ct = default);

    /// <summary>True if some Kodi device renewed the scan-activity flag (see
    /// ReportScanActivityAsync) within its own TTL. NfoGenerationService checks this at the top
    /// of every scheduled tick and skips the whole run when true -- root-caused live
    /// (2026-09-12): its own 2-minute sweep and an active library scan's live, per-item NFO
    /// pushes routinely landed on the same freshly-discovered item within seconds of each other,
    /// each independently rebuilding and writing the identical NFO (confirmed via server log:
    /// every scanned item's sidecar built twice, its temp-file write losing a race against
    /// itself before succeeding on retry). Pausing the scheduled sweep for the scan's own
    /// duration removes the contention outright, and frees the exact server/IO capacity the
    /// active scan needs most rather than competing with it for it.</summary>
    Task<bool> IsScanActiveAsync(CancellationToken ct = default);
}
