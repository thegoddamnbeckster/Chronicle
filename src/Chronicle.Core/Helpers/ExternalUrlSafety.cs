using System.Net;
using System.Net.Sockets;

namespace Chronicle.Core.Helpers;

/// <summary>
/// Guards any code path that lets Chronicle's own server fetch a URL a caller supplied (the
/// image-pin override endpoint, the poster-proxy endpoint) against SSRF -- a caller pointing
/// that fetch at an internal service, a cloud metadata endpoint (169.254.169.254), or anything
/// else on the server's own network rather than a genuine public image host.
/// </summary>
public static class ExternalUrlSafety
{
    /// <summary>Parses <paramref name="url"/> as an absolute http/https URI. Does not check
    /// reachability or safety -- see <see cref="IsSafeToFetchAsync"/> for that.</summary>
    public static bool IsWellFormedHttpUrl(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        uri = parsed;
        return true;
    }

    /// <summary>
    /// Resolves the URI's host and confirms every address it resolves to is public/globally
    /// routable. Returns false (unsafe) if resolution fails outright -- an unresolvable host is
    /// never treated as "fine to fetch", it just fails differently than a private-IP target.
    /// </summary>
    public static async Task<bool> IsSafeToFetchAsync(Uri uri, CancellationToken ct = default)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.IdnHost, ct);
        }
        catch
        {
            return false;
        }
        return addresses.Length > 0 && addresses.All(IsPublicAddress);
    }

    private static bool IsPublicAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return false;
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return false;
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return false;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 0) return false;                              // 0.0.0.0/8
            if (b[0] == 10) return false;                             // 10.0.0.0/8
            if (b[0] == 127) return false;                            // 127.0.0.0/8 loopback
            if (b[0] == 100 && b[1] is >= 64 and <= 127) return false; // 100.64.0.0/10 CGNAT
            if (b[0] == 169 && b[1] == 254) return false;              // 169.254.0.0/16 link-local / cloud metadata
            if (b[0] == 172 && b[1] is >= 16 and <= 31) return false;  // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return false;              // 192.168.0.0/16
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false;                   // fc00::/7 unique local
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return false;   // fe80::/10 link-local
        }
        return true;
    }
}
