using System.Collections.Concurrent;
using System.Net.Sockets;
using Chronicle.Plugins.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// Verifies that URLs a metadata provider claims to have found actually resolve to real
/// content before they're allowed into a MediaItem's persisted metadata. Providers occasionally
/// build URLs from bad assumptions (e.g. a non-resolving CDN subdomain) and previously such a
/// URL would flow straight through to Chronicle's merge-priority resolution and get displayed
/// as if it were good data. Nulling the field instead lets a lower-priority plugin's valid URL
/// (or a blank field) win, rather than silently showing a broken image.
/// </summary>
public interface IMetadataUrlValidator
{
    /// <summary>Checks every URL field on <paramref name="metadata"/> and clears any that don't
    /// resolve. Mutates <paramref name="metadata"/> in place.</summary>
    Task ValidateAndCleanAsync(MediaMetadata metadata, CancellationToken ct = default);
}

public class MetadataUrlValidator(
    IHttpClientFactory httpClientFactory,
    ILogger<MetadataUrlValidator> logger,
    IMemoryCache? cache = null) : IMetadataUrlValidator
{
    /// <summary>How a URL checked out. <see cref="Unknown"/> (rate limited, server trouble, timeout) keeps the URL: a provider's
    /// image must not be thrown away because its CDN was busy for a moment.</summary>
    internal enum Verdict { Valid, Invalid, Unknown }

    internal static TimeSpan ValidFor => Tunables.Hours(Tunables.ImageCheckValidHours);
    internal static TimeSpan InvalidFor => Tunables.Minutes(Tunables.ImageCheckInvalidMinutes);
    private const int MaxConcurrentPerHost = 4;

    // One gate per host, shared by every enrichment running at once: an album with thirty cover images used to fire sixty
    // requests at the same CDN together, which is how a rate limit turned good images into "invalid" ones.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> HostGates = new(StringComparer.OrdinalIgnoreCase);

    public async Task ValidateAndCleanAsync(MediaMetadata metadata, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient("url-validation");

        (string Name, Func<string?> Get, Action<string?> Set)[] urlFields =
        [
            ("PosterUrl", () => metadata.PosterUrl, v => metadata.PosterUrl = v),
            ("BackdropUrl", () => metadata.BackdropUrl, v => metadata.BackdropUrl = v),
            ("LogoUrl", () => metadata.LogoUrl, v => metadata.LogoUrl = v),
            ("BannerUrl", () => metadata.BannerUrl, v => metadata.BannerUrl = v),
            ("ThumbUrl", () => metadata.ThumbUrl, v => metadata.ThumbUrl = v),
            ("ClearartUrl", () => metadata.ClearartUrl, v => metadata.ClearartUrl = v),
            ("DiscUrl", () => metadata.DiscUrl, v => metadata.DiscUrl = v),
            ("CharacterArtUrl", () => metadata.CharacterArtUrl, v => metadata.CharacterArtUrl = v),
        ];

        var fieldChecks = urlFields
            .Where(f => !string.IsNullOrWhiteSpace(f.Get()))
            .Select(async f =>
            {
                var url = f.Get()!;
                if (await CheckAsync(url, client, ct) == Verdict.Invalid)
                {
                    logger.LogWarning(
                        "Dropping invalid {Field} URL from provider '{Source}': {Url}",
                        f.Name, metadata.Source, url);
                    f.Set(null);
                }
            });

        // A thumbnail lives beside its image on the same host, so only the image itself is checked: validating both doubled the
        // requests for nothing, and a thumbnail without a valid image goes with it below.
        var imageChecks = metadata.AdditionalImages.Select(async img =>
        {
            if (!string.IsNullOrWhiteSpace(img.Url) && await CheckAsync(img.Url, client, ct) == Verdict.Invalid)
            {
                logger.LogWarning(
                    "Dropping invalid AdditionalImage URL from provider '{Source}': {Url}",
                    metadata.Source, img.Url);
                img.Url = string.Empty;
                img.ThumbnailUrl = null;
            }
        });

        await Task.WhenAll(fieldChecks.Concat(imageChecks));

        // An AdditionalImage with no valid Url (its one required field) is useless — drop it
        // entirely rather than persisting an empty-string placeholder.
        metadata.AdditionalImages.RemoveAll(img => string.IsNullOrWhiteSpace(img.Url));
    }

    private async Task<Verdict> CheckAsync(string url, HttpClient client, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Verdict.Invalid;
        }

        var key = "url-validation:" + uri.AbsoluteUri;
        if (cache is not null && cache.TryGetValue(key, out Verdict known))
            return known;

        var gate = HostGates.GetOrAdd(uri.Host, _ => new SemaphoreSlim(MaxConcurrentPerHost, MaxConcurrentPerHost));
        await gate.WaitAsync(ct);
        Verdict verdict;
        try
        {
            // Try HEAD first (cheapest — no body transfer). Some CDNs reject HEAD outright
            // (405/403) despite serving the same URL fine on GET, so fall back before giving up.
            verdict = await TryRequestAsync(client, HttpMethod.Head, uri, ct);
            if (verdict != Verdict.Valid)
                verdict = await TryRequestAsync(client, HttpMethod.Get, uri, ct);
        }
        finally
        {
            gate.Release();
        }

        if (cache is not null && verdict != Verdict.Unknown)
            cache.Set(key, verdict, verdict == Verdict.Valid ? ValidFor : InvalidFor);
        return verdict;
    }

    private static async Task<Verdict> TryRequestAsync(HttpClient client, HttpMethod method, Uri uri, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, uri);
            // ResponseHeadersRead means a GET fallback never actually downloads the image body —
            // only the status line/headers are read before the response is disposed.
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode) return Verdict.Valid;

            var status = (int)response.StatusCode;
            // Busy or broken for the moment says nothing about whether the image exists.
            return status is 408 or 429 || status >= 500 ? Verdict.Unknown : Verdict.Invalid;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData })
        {
            // The host does not exist: the provider built the address from a bad assumption (what this check is for).
            return Verdict.Invalid;
        }
        catch (Exception)
        {
            // Per-URL client timeout, connection refused, TLS error, etc.: can't tell, so keep the URL.
            return Verdict.Unknown;
        }
    }
}
