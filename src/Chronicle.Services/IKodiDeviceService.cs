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
}
