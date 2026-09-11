using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Chronicle.Core.Models;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

public sealed class KodiRpcClient(IHttpClientFactory httpClientFactory, ILogger<KodiRpcClient> logger) : IKodiRpcClient
{
    // Short -- this runs synchronously inline with a manual metadata edit today (see
    // NfoPushService's caller); a slow/unreachable device must not make an unrelated save feel
    // stuck. A device that times out just misses this one push and catches up on its own next
    // scan/rebuild, same as any other best-effort path in this feature.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    public Task<bool> RefreshAsync(KodiDevice device, string kind, int kodiId, CancellationToken ct = default)
    {
        var (method, paramName) = kind switch
        {
            "movie"   => ("VideoLibrary.RefreshMovie", "movieid"),
            "tvshow"  => ("VideoLibrary.RefreshTVShow", "tvshowid"),
            "episode" => ("VideoLibrary.RefreshEpisode", "episodeid"),
            _ => (null, null),
        };
        if (method is null)
        {
            logger.LogWarning("KodiRpcClient: unknown kind {Kind} for device {Device} -- skipping.", kind, device.Name);
            return Task.FromResult(false);
        }

        return SendAsync(device, method, new Dictionary<string, object> { [paramName!] = kodiId }, ct);
    }

    public Task<bool> ScanAsync(KodiDevice device, CancellationToken ct = default)
    {
        // No "directory" parameter -- deliberately a full-library scan, not a targeted one.
        // Chronicle's own fileScanner.folderPath is recorded from THIS SERVER's own view of
        // the filesystem (e.g. "J:\Videos\TV\..."), which has no reliable mapping to how any
        // given Kodi device names that same share as one of ITS OWN sources (a different
        // drive letter, a different SMB share name, NFS instead of SMB, a source added under
        // a completely different label -- every registered device already varies in Host, see
        // KodiDevice). A full scan costs more per call but needs no such mapping and is safe
        // to call repeatedly: Kodi's own scanner is cheap for files it already knows about,
        // only genuinely new files cost real work. IKodiLibraryScanService is what keeps this
        // from being called too often.
        return SendAsync(device, "VideoLibrary.Scan", new Dictionary<string, object> { ["showdialogs"] = false }, ct);
    }

    private async Task<bool> SendAsync(
        KodiDevice device, string method, object @params, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params });

        using var client = httpClientFactory.CreateClient(nameof(KodiRpcClient));
        client.Timeout = Timeout;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{device.Host}:{device.Port}/jsonrpc")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(device.Username))
        {
            var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{device.Username}:{device.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", creds);
        }

        try
        {
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("KodiRpcClient: {Method} to {Device} ({Host}) returned HTTP {Status}.",
                    method, device.Name, device.Host, (int)response.StatusCode);
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                logger.LogWarning("KodiRpcClient: {Method} rejected by {Device} ({Host}): {Error}",
                    method, device.Name, device.Host, error.GetRawText());
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "KodiRpcClient: {Method} to {Device} ({Host}) failed.", method, device.Name, device.Host);
            return false;
        }
    }
}
