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

    public async Task<bool> RefreshAsync(KodiDevice device, string kind, int kodiId, CancellationToken ct = default)
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
            return false;
        }

        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id      = 1,
            method,
            @params = new Dictionary<string, int> { [paramName!] = kodiId },
        });

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
        catch (HttpRequestException ex)
        {
            // "Device offline/unreachable" is the routine, expected failure mode here (a stale
            // registration, a Shield that's asleep, a LAN hiccup) -- confirmed live (2026-09-11)
            // that logging the full exception object floods the log with a 20+ line stack trace
            // per failure, tripled by .NET's own HttpClientFactory logging handlers doing the
            // same for the identical exception. This got dramatically more frequent once
            // NfoGenerationService started racing through the whole NFO backlog (hundreds of
            // pushes per run instead of one at a time), each fanning out to every registered
            // device regardless of whether it's actually reachable. One concise line is enough
            // to know which device and why; the stack trace adds nothing actionable for a
            // condition this expected. Genuinely unexpected exceptions still fall through to the
            // catch-all below with full detail.
            logger.LogWarning("KodiRpcClient: {Method} to {Device} ({Host}) failed -- device unreachable ({Reason}).",
                method, device.Name, device.Host, ex.Message);
            return false;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own 8s Timeout elapsed (not the caller's ct) -- same "expected, routine"
            // reasoning as HttpRequestException above, just a different exception shape for a
            // timeout specifically.
            logger.LogWarning("KodiRpcClient: {Method} to {Device} ({Host}) failed -- timed out after {Timeout}.",
                method, device.Name, device.Host, Timeout);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "KodiRpcClient: {Method} to {Device} ({Host}) failed unexpectedly.", method, device.Name, device.Host);
            return false;
        }
    }
}
