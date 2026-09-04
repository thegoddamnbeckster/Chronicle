namespace Chronicle.Core.Models;

/// <summary>
/// One entry in Chronicle's static plugin catalog (see PluginCatalog in Chronicle.Services)
/// -- describes an installable plugin release on GitHub. Pure data, no HTTP/ASP.NET
/// dependencies, so it can be referenced from both the API layer (catalog/install endpoints)
/// and the Services layer (the scheduled update-check task).
/// </summary>
public record PluginCatalogEntry(
    string PluginId,
    string Name,
    string Description,
    string Author,
    string? IconUrl,
    string GithubRepo,
    string AssetName,
    string DllName,
    string[] Tags,
    bool IsInstalled = false,
    /// <summary>
    /// Expected SHA-256 hex digest of the ZIP asset (lowercase, no prefix).
    /// When set, Chronicle will reject the download if the computed hash does not match,
    /// protecting against a compromised GitHub release or a man-in-the-middle attack.
    /// </summary>
    string? Sha256 = null,
    /// <summary>Version string from the plugin's manifest (e.g. "1.2.0").</summary>
    string Version = ""
);
