using System.Text.Json;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Chronicle.Services.Plugins;

/// <summary>
/// Scheduled task: for every installed plugin that has a catalog entry (see PluginCatalog),
/// checks GitHub's actual latest release tag against the installed Version and records
/// whether a newer one is available. Per-user request (2026-09-04): "Chronicle needs to
/// automatically update installed plugins from the catalog so it's always on the latest
/// version" -- chose "check automatically, install on approval" over fully-silent
/// auto-install, so this task only ever flags LatestVersionAvailable; the actual install
/// step is a separate, explicit action (PluginsController's update-from-catalog endpoint).
///
/// Deliberately checks GitHub directly rather than trusting PluginCatalog's own static
/// Version field: that field is Chronicle's OWN source code, manually bumped by whoever
/// last synced the catalog (see PluginCatalog.cs's own doc and the SIMKL entry's comment
/// about its Version lagging behind the real repo) -- it is not itself a live signal.
/// </summary>
public sealed class PluginUpdateCheckService : IScheduledTask
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _log = Log.ForContext<PluginUpdateCheckService>();

    public PluginUpdateCheckService(IServiceScopeFactory scopeFactory, IHttpClientFactory httpClientFactory)
    {
        _scopeFactory      = scopeFactory;
        _httpClientFactory = httpClientFactory;
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

        var github = _httpClientFactory.CreateClient("github");
        int checkedCount = 0, updatesFound = 0;

        foreach (var plugin in installed)
        {
            ct.ThrowIfCancellationRequested();

            var entry = Array.Find(PluginCatalog.Entries, e => e.PluginId == plugin.PluginId);
            if (entry is null) continue; // not in the catalog (custom/local-only build) -- nothing to check against

            try
            {
                var apiUrl = $"https://api.github.com/repos/{entry.GithubRepo}/releases/latest";
                using var resp = await github.GetAsync(apiUrl, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    _log.Debug("Update check: GitHub returned {Status} for {Repo}", (int)resp.StatusCode, entry.GithubRepo);
                    continue;
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var tag = doc.RootElement.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
                var latestVersion = tag?.TrimStart('v', 'V');
                if (string.IsNullOrWhiteSpace(latestVersion)) continue;

                checkedCount++;
                plugin.UpdateCheckedAt = DateTime.UtcNow;
                var isNewer = IsNewerVersion(latestVersion, plugin.Version);
                plugin.LatestVersionAvailable = isNewer ? latestVersion : null;
                if (isNewer)
                {
                    updatesFound++;
                    _log.Information("Update available for {PluginId}: {Installed} -> {Latest}",
                        plugin.PluginId, plugin.Version, latestVersion);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // One plugin's GitHub call failing (rate limit, network blip, repo renamed)
                // must not stop the rest of the batch -- same reasoning as every other
                // per-item loop in this codebase that logs and continues.
                _log.Warning(ex, "Update check failed for plugin {PluginId}", plugin.PluginId);
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
