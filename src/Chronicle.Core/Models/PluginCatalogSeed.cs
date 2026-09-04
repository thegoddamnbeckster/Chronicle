namespace Chronicle.Core.Models;

/// <summary>
/// The ONLY thing Chronicle still hardcodes about the plugin catalog: which GitHub repos
/// are part of it. Per-user request (2026-09-04): "The catalog needs to download from
/// github, not from vision [this machine's hostname] ... It all needs to come from Github."
/// Everything else a catalog entry needs -- name, description, author, icon, current
/// version, the download asset, which DLL to load -- is resolved live from each repo's own
/// manifest.json and latest GitHub release by PluginCatalogService, specifically so a new
/// plugin release becomes visible/installable without a Chronicle code change and redeploy.
/// A repo simply not having a release yet (or one with no attached asset) is not an error
/// here -- PluginCatalogService reports that entry as unavailable rather than failing the
/// whole catalog.
/// </summary>
public record PluginCatalogSeed(
    string PluginId,
    string GithubRepo,
    string[] Tags
);
