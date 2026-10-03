namespace Chronicle.Core.Helpers;

/// <summary>
/// Makes an image URL safe to hand to a client that fetches it verbatim.
///
/// Confirmed live (2026-10-03): Fanart.tv really does host an image whose file name contains a
/// space and parentheses ("https://assets.fanart.tv/fanart/S_78901 (1).jpg", Supernatural's thumb).
/// Kodi's curl rejects that URL outright ("URL using bad/illegal format or missing URL"), and the
/// scraper addon's own urllib download refuses it too, so the image never loads. A space in a URL
/// is never valid; "%20" is what it means. Parentheses are legal in a URL path and are left alone.
/// </summary>
public static class ArtworkUrlHelper
{
    /// <summary>
    /// Replaces spaces (and other ASCII whitespace/control characters, which can never appear in a
    /// URL) with their percent-encoding. Anything already percent-encoded, and every legal URL
    /// character, is left exactly as it was, so an already-valid URL comes back unchanged and
    /// encoding twice is harmless. Null/empty pass straight through.
    /// </summary>
    public static string? Encode(string? url)
    {
        if (string.IsNullOrEmpty(url)) return url;

        var needsEncoding = false;
        foreach (var c in url)
        {
            if (c <= ' ' || c == '\u007f') { needsEncoding = true; break; }
        }
        if (!needsEncoding) return url;

        var sb = new System.Text.StringBuilder(url.Length + 8);
        foreach (var c in url)
        {
            if (c <= ' ' || c == '\u007f') sb.Append('%').Append(((int)c).ToString("X2"));
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
