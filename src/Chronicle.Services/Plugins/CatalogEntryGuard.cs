using System.Text.RegularExpressions;
using Chronicle.Core.Models;

namespace Chronicle.Services.Plugins;

/// <summary>
/// Checks a catalog entry before any of its fields reach a file path or a URL. The catalog can come from a URL an
/// administrator configured, so an entry is data from outside, not code: its plugin id becomes a folder name, its repo becomes
/// part of an api.github.com address and its asset becomes a download.
/// </summary>
public static partial class CatalogEntryGuard
{
    /// <summary>Largest plugin archive Chronicle will download.</summary>
    public const long MaxArchiveBytes = 200L * 1024 * 1024;

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex PluginIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}/[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex RepoPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 _.+-]{0,199}$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();

    /// <summary>Null when the entry is safe to use; otherwise what is wrong with it.</summary>
    public static string? Check(PluginCatalogEntry entry)
    {
        if (!PluginIdPattern().IsMatch(entry.PluginId ?? "")) return "the plugin id is not a plain identifier";
        if (!RepoPattern().IsMatch(entry.GithubRepo ?? "") || entry.GithubRepo!.Contains("..", StringComparison.Ordinal))
            return "the GitHub repository is not of the form owner/name";
        if (!FileNamePattern().IsMatch(entry.AssetName ?? "") || entry.AssetName!.Contains("..", StringComparison.Ordinal))
            return "the asset name is not a plain file name";
        if (!FileNamePattern().IsMatch(entry.DllName ?? "") || entry.DllName!.Contains("..", StringComparison.Ordinal))
            return "the DLL name is not a plain file name";
        return null;
    }

    /// <summary>True only for an https address on GitHub's API host, the one place a plugin archive may be fetched from.</summary>
    public static bool IsGitHubApiUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(uri.UserInfo);
}
