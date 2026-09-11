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

    /// <summary>Every (device, mapping) pair NfoPushService should push this MediaItem to --
    /// i.e. every Kodi instance that has both self-registered AND already reported its own
    /// internal id for this specific item via an ordinary scan.</summary>
    Task<List<(KodiDevice Device, KodiLibraryId Mapping)>> GetPushTargetsAsync(int mediaItemId, CancellationToken ct = default);

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
}
