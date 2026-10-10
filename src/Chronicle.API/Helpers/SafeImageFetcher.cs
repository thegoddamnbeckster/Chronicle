using System.Net;
using System.Net.Sockets;

namespace Chronicle.API.Helpers;

/// <summary>
/// Fetches a remote image on behalf of the browser (the poster proxy) without letting the
/// caller aim the server at its own network. The address check runs inside the connect
/// callback, on the address actually being connected to, so it also covers DNS rebinding and
/// every redirect hop -- a hostname that resolves to a private address at connect time is
/// refused no matter what it resolved to earlier.
/// </summary>
public sealed class SafeImageFetcher
{
    /// <summary>Largest image the proxy will relay.</summary>
    public const int MaxBytes = 10 * 1024 * 1024;

    /// <summary>The instance the application uses: only globally routable addresses are allowed.</summary>
    public static SafeImageFetcher Default { get; } = new(IsPublicAddress);

    /// <summary>Raster types only -- SVG can carry script when opened directly, so it is excluded.</summary>
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/jpg", "image/png", "image/webp", "image/gif", "image/avif",
    };

    private readonly HttpClient _client;
    private readonly Func<IPAddress, bool> _isAllowed;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;

    /// <param name="isAllowed">Decides whether a destination address may be connected to. Tests
    /// pass a permissive filter so a loopback test server can stand in for the internet; production
    /// always uses <see cref="IsPublicAddress"/>.</param>
    /// <param name="resolve">DNS lookup; replaceable so tests can return a mixed public/private answer.</param>
    public SafeImageFetcher(Func<IPAddress, bool> isAllowed,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
    {
        _isAllowed = isAllowed;
        _resolve = resolve ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
        _client = CreateClient();
    }

    public enum FetchStatus { Ok, BadRequest, NotFound, TooLarge, UnsupportedType }

    public sealed record FetchResult(FetchStatus Status, byte[]? Bytes = null, string? ContentType = null);

    /// <summary>Per-call limits. The defaults are the poster proxy's: raster images only, up to
    /// <see cref="MaxBytes"/>, http or https.</summary>
    /// <param name="MaxBytes">Largest body accepted.</param>
    /// <param name="HttpsOnly">Refuse plain http (also for redirects, which the connection filter
    /// cannot see the scheme of; a downgrade is caught by comparing the final URL).</param>
    /// <param name="ExtraContentTypes">Types accepted in addition to the raster set, e.g. SVG for
    /// callers that rasterise it before it reaches a browser.</param>
    public sealed record FetchOptions(int MaxBytes = SafeImageFetcher.MaxBytes, bool HttpsOnly = false,
        IReadOnlyCollection<string>? ExtraContentTypes = null);

    private HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            // A configured system proxy would make the proxy, not us, open the connection and
            // bypass the address check below.
            UseProxy = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = ConnectToAllowedAddressAsync,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Chronicle/1.0");
        return client;
    }

    public Task<FetchResult> FetchAsync(string? url, CancellationToken ct) => FetchAsync(url, ct, null);

    public async Task<FetchResult> FetchAsync(string? url, CancellationToken ct, FetchOptions? options)
    {
        options ??= new FetchOptions();
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && (options.HttpsOnly || uri.Scheme != Uri.UriSchemeHttp))
            || !string.IsNullOrEmpty(uri.UserInfo))
            return new FetchResult(FetchStatus.BadRequest);

        try
        {
            using var resp = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return new FetchResult(FetchStatus.NotFound);

            // A redirect may not walk an https request down to plain http.
            if (options.HttpsOnly && resp.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
                return new FetchResult(FetchStatus.NotFound);

            var contentType = resp.Content.Headers.ContentType?.MediaType;
            if (contentType is null
                || !(AllowedContentTypes.Contains(contentType)
                     || (options.ExtraContentTypes?.Contains(contentType, StringComparer.OrdinalIgnoreCase) ?? false)))
                return new FetchResult(FetchStatus.UnsupportedType);

            if (resp.Content.Headers.ContentLength is { } declared && declared > options.MaxBytes)
                return new FetchResult(FetchStatus.TooLarge);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > options.MaxBytes) return new FetchResult(FetchStatus.TooLarge);
                buffer.Write(chunk, 0, read);
            }
            return new FetchResult(FetchStatus.Ok, buffer.ToArray(), contentType.ToLowerInvariant());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Blocked address, DNS failure, timeout, TLS error: indistinguishable to the caller on purpose.
            return new FetchResult(FetchStatus.NotFound);
        }
    }

    private async ValueTask<Stream> ConnectToAllowedAddressAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        var addresses = IPAddress.TryParse(host, out var literal)
            ? new[] { literal }
            : await _resolve(host, ct);

        // Refuse the whole request if ANY resolved address is non-public, rather than quietly
        // picking a public one: a host that mixes public and private records is an attack shape.
        if (addresses.Length == 0 || addresses.Any(a => !_isAllowed(a)))
            throw new HttpRequestException("Destination address is not permitted.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// True only for globally routable unicast addresses. Rejects loopback, RFC 1918 private,
    /// link-local (including the 169.254.169.254 cloud-metadata address), carrier-grade NAT,
    /// unspecified, multicast/reserved, IPv6 unique-local and site-local, and IPv4-mapped IPv6
    /// forms of any of those.
    /// </summary>
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 0) return false;                                   // 0.0.0.0/8
            if (b[0] == 10) return false;                                  // 10.0.0.0/8
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;    // 100.64.0.0/10
            if (b[0] == 169 && b[1] == 254) return false;                  // 169.254.0.0/16
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;     // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 0 && b[2] == 0) return false;       // 192.0.0.0/24
            if (b[0] == 192 && b[1] == 168) return false;                  // 192.168.0.0/16
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return false;   // 198.18.0.0/15
            if (b[0] >= 224) return false;                                 // multicast, reserved, broadcast
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;
            if (address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any)) return false;
            var b = address.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false;                       // fc00::/7 unique local
            return true;
        }

        return false;
    }
}
