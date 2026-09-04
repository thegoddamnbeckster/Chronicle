using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Chronicle.Services.Plugins;

/// <summary>
/// Scheduled task: for every installed plugin that has a catalog entry, checks
/// PluginCatalogService's live-resolved GitHub version against the installed Version and
/// records whether a newer one is available. Per-user request (2026-09-04): "Chronicle
/// needs to automatically update installed plugins from the catalog so it's always on the
/// latest version" -- chose "check automatically, install on approval" over fully-silent
/// auto-install, so this task only ever flags LatestVersionAvailable; the actual install
/// step is a separate, explicit action (PluginsController's update-from-catalog endpoint).
/// </summary>
public sealed class PluginUpdateCheckService : IScheduledTask
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PluginCatalogService _catalogService;
    private readonly ILogger _log = Log.ForContext<PluginUpdateCheckService>();

    public PluginUpdateCheckService(IServiceScopeFactory scopeFactory, PluginCatalogService catalogService)
    {
        _scopeFactory   = scopeFactory;
        _catalogService = catalogService;
    }

    public string TaskId      => "plugin_update_check";
    public string DisplayName => "Plugin Update Check";
    public string Description => "Checks the plugin catalog's GitHub repos for newer releases than what's installed. Does not install anything -- just flags an 'Update available' badge for you to act on.";
    public string DefaultCron => "0 5 * * *";

    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        var installed = await db.Plugins.ToListAsync(ct);
        if (installed.Count == 0) return;

        // One batch of live GitHub lookups (cached briefly by PluginCatalogService) covers
        // every installed plugin, rather than this task making its own separate release-API
        // call per plugin.
        var catalog = await _catalogService.GetCatalogAsync(ct);
        var byPluginId = catalog.ToDictionary(e => e.PluginId, StringComparer.OrdinalIgnoreCase);

        int checkedCount = 0, updatesFound = 0;

        foreach (var plugin in installed)
        {
            ct.ThrowIfCancellationRequested();

            if (!byPluginId.TryGetValue(plugin.PluginId, out var entry))
                continue; // not in the catalog (custom/local-only build, or no usable release right now)

            checkedCount++;
            plugin.UpdateCheckedAt = DateTime.UtcNow;
            var isNewer = IsNewerVersion(entry.Version, plugin.Version);
            plugin.LatestVersionAvailable = isNewer ? entry.Version : null;
            if (isNewer)
            {
                updatesFound++;
                _log.Information("Update available for {PluginId}: {Installed} -> {Latest}",
                    plugin.PluginId, plugin.Version, entry.Version);
            }
        }

        await db.SaveChangesAsync(ct);
        _log.Information("PluginUpdateCheckService: checked {Checked} plugin(s), {Found} update(s) available",
            checkedCount, updatesFound);
    }

    /// <summary>
    /// Dotted-numeric version comparison via System.Version -- every catalog entry and every
    /// plugin manifest.json in this codebase uses plain "X.Y.Z" versions, no pre-release
    /// suffixes, so the BCL's own parser is sufficient without pulling in a semver library.
    /// An unparseable candidate is treated as "not newer" (fail closed -- never claim an
    /// update is available from data we can't actually compare).
    /// </summary>
    internal static bool IsNewerVersion(string candidate, string current)
    {
        if (!Version.TryParse(candidate, out var candidateVersion)) return false;
        if (!Version.TryParse(current, out var currentVersion)) return true;
        return candidateVersion > currentVersion;
    }
}
