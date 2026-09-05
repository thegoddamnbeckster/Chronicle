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
}
