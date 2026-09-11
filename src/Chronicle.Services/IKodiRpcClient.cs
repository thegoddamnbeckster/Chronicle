using Chronicle.Core.Models;

namespace Chronicle.Services;

public interface IKodiRpcClient
{
    /// <summary>Calls VideoLibrary.RefreshMovie/RefreshTVShow/RefreshEpisode (per kind) against
    /// device's own JSON-RPC-over-HTTP endpoint -- the one mechanism confirmed (live, via
    /// Chronicle_Scraper's nfo_rebuild.py, see that module's own kodi.log-verified doc) to make
    /// an already-imported item reconsider its local NFO. Best-effort: any failure (device
    /// offline, wrong credentials, remote control since turned off) is caught and logged,
    /// never thrown -- a failed push here just means that one device catches up on its own
    /// next scan/rebuild instead of instantly. Returns true only on a genuine JSON-RPC success
    /// response.</summary>
    Task<bool> RefreshAsync(KodiDevice device, string kind, int kodiId, CancellationToken ct = default);

    /// <summary>Calls VideoLibrary.Scan (no directory -- a full library scan) against device's
    /// own JSON-RPC endpoint. Unlike RefreshAsync, this is the one call that can make Kodi
    /// discover a file it doesn't have a library entry for yet at all -- see
    /// IKodiLibraryScanService's own doc for why that gap exists and what calls this. Same
    /// best-effort contract as RefreshAsync: any failure is caught, logged, and returned as
    /// false, never thrown.</summary>
    Task<bool> ScanAsync(KodiDevice device, CancellationToken ct = default);
}
