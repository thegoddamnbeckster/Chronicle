namespace Chronicle.Core.Helpers;

/// <summary>
/// Identifies the PICTURE behind an image URL, so the same photo fetched at different sizes or with different tracking
/// parameters is recognised as one photo.
///
/// A person's headshots accumulate from several feeds, and one TMDB file reaches Chronicle in up to three URLs -
/// <c>/t/p/h632/x.jpg</c> (the "current" pick), <c>/t/p/w500/x.jpg</c> (a thumbnail) and <c>/t/p/original/x.jpg</c>
/// (the gallery entry). Matched on the URL they were stored as three photos, the person's Photos gallery showed one
/// picture three times, and hiding a bad one would have let it straight back in under another size.
/// </summary>
public static class HeadshotImageKey
{
    /// <summary>The identity of the picture at <paramref name="url"/>; empty for a blank url.</summary>
    public static string Of(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return trimmed.ToLowerInvariant();

        var host = uri.Host.ToLowerInvariant();
        if (host == "image.tmdb.org")
        {
            // /t/p/{size}/{file}: the size segment (w500, h632, original ...) changes, the file does not.
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 4 && segments[0] == "t" && segments[1] == "p")
                return $"tmdb:{segments[^1].ToLowerInvariant()}";
        }

        // Anything else: host + path. Drops the query string and fragment (Wikipedia appends utm_* parameters that
        // differ between calls for the very same file).
        return $"{host}{uri.AbsolutePath}".ToLowerInvariant();
    }
}
