using Chronicle.Plugins.Models;
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
    ILogger<MetadataUrlValidator> logger) : IMetadataUrlValidator
{
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
                if (!await IsValidUrlAsync(url, client, ct))
                {
                    logger.LogWarning(
                        "Dropping invalid {Field} URL from provider '{Source}': {Url}",
                        f.Name, metadata.Source, url);
                    f.Set(null);
                }
            });

        var imageChecks = metadata.AdditionalImages.Select(async img =>
        {
            if (!string.IsNullOrWhiteSpace(img.Url) && !await IsValidUrlAsync(img.Url, client, ct))
            {
                logger.LogWarning(
                    "Dropping invalid AdditionalImage URL from provider '{Source}': {Url}",
                    metadata.Source, img.Url);
                img.Url = string.Empty;
            }
            if (!string.IsNullOrWhiteSpace(img.ThumbnailUrl) && !await IsValidUrlAsync(img.ThumbnailUrl, client, ct))
            {
                img.ThumbnailUrl = null;
            }
        });

        await Task.WhenAll(fieldChecks.Concat(imageChecks));

        // An AdditionalImage with no valid Url (its one required field) is useless — drop it
        // entirely rather than persisting an empty-string placeholder.
        metadata.AdditionalImages.RemoveAll(img => string.IsNullOrWhiteSpace(img.Url));
    }

    private static async Task<bool> IsValidUrlAsync(string url, HttpClient client, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        // Try HEAD first (cheapest — no body transfer). Some CDNs reject HEAD outright
        // (405/403) despite serving the same URL fine on GET, so fall back before giving up.
        if (await TryRequestAsync(client, HttpMethod.Head, uri, ct))
            return true;
        return await TryRequestAsync(client, HttpMethod.Get, uri, ct);
    }

    private static async Task<bool> TryRequestAsync(HttpClient client, HttpMethod method, Uri uri, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, uri);
            // ResponseHeadersRead means a GET fallback never actually downloads the image body —
            // only the status line/headers are read before the response is disposed.
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Per-URL client timeout, DNS failure, connection refused, TLS error, etc. — all
            // mean the URL doesn't currently evaluate to valid data, same as a bad status code.
            return false;
        }
    }
}
