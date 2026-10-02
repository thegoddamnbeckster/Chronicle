using System.Text.Json;
using System.Text.Json.Nodes;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// One-time removal of every piece of data Chronicle ever stored from local .nfo files, run at
/// startup after Chronicle stopped reading NFOs entirely (2026-10-02). Removes:
///   - the Kodi NFO sidecar plugin (DB row, its data, and its folder on disk -- otherwise
///     PluginHostService's folder auto-registration would reinstall it on the next start);
///   - every item's "chronicle_scraper.legacy_nfo" partition (NFO contents the Kodi addon used to
///     contribute) and the nfoPath/nfoRaw/nfoParsed/nfoPosterUrl keys of the fileScanner partition;
///   - credits whose source is "legacy_nfo", plus any person stub left with no credits and no data
///     of its own as a result;
/// then re-resolves every item that actually lost NFO data, so no resolved field still holds an
/// NFO-derived value. Gated by an app_settings marker so it runs exactly once.
/// </summary>
public sealed class LegacyNfoPurgeService(
    IServiceScopeFactory scopeFactory,
    IHostEnvironment environment,
    ILogger<LegacyNfoPurgeService> logger)
{
    internal const string MarkerKey = "maintenance.legacy_nfo_purge";
    private const string NfoPluginId = "chronicle.plugin.kodi.nfo";
    private const string LegacyPartitionKey = "chronicle_scraper.legacy_nfo";
    private const string LegacyCreditSource = "legacy_nfo";
    private const int BatchSize = 500;
    private static readonly string[] FileScannerNfoKeys = ["nfoPath", "nfoRaw", "nfoParsed", "nfoPosterUrl"];

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            if (await db.AppSettings.AnyAsync(s => s.Key == MarkerKey, ct)) return;
        }

        var pluginRemoved = await RemoveSidecarPluginAsync(ct);
        var (cleaned, toResolve) = await StripNfoDataAsync(ct);
        var (credits, people) = await RemoveNfoCreditsAsync(ct);
        await ResolveAsync(toResolve, ct);

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            db.AppSettings.Add(new AppSetting { Key = MarkerKey, Value = DateTime.UtcNow.ToString("O") });
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation(
            "Legacy NFO purge: plugin {Plugin}; NFO keys removed from {Cleaned} item(s), {Resolved} of them " +
            "re-resolved; {Credits} credit(s) and {People} orphaned person stub(s) removed",
            pluginRemoved ? "uninstalled" : "not installed", cleaned, toResolve.Count, credits, people);
    }

    /// <summary>Same full cleanup a user-initiated uninstall does, plus the folder on disk.</summary>
    private async Task<bool> RemoveSidecarPluginAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var plugin = await db.Plugins.FirstOrDefaultAsync(p => p.PluginId == NfoPluginId, ct);
        if (plugin is not null)
            await scope.ServiceProvider.GetRequiredService<IPluginService>().UninstallPluginAsync(plugin.Id);

        var pluginDir = Path.Combine(environment.ContentRootPath, "plugins", NfoPluginId);
        if (Directory.Exists(pluginDir))
        {
            try { Directory.Delete(pluginDir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { logger.LogWarning(ex, "Could not delete {Dir}; remove it by hand", pluginDir); }
        }
        return plugin is not null;
    }

    /// <summary>
    /// Removes the legacy NFO partition and the fileScanner NFO keys, in id-ordered batches.
    /// Returns how many items changed and the ids that lost real data (a non-null NFO key or the
    /// legacy partition) and so need re-resolving -- most items only carry a null "nfoPath".
    /// </summary>
    private async Task<(int Cleaned, List<int> ToResolve)> StripNfoDataAsync(CancellationToken ct)
    {
        var toResolve = new List<int>();
        int cleaned = 0, lastId = 0;
        while (true)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var batch = await db.MediaItems
                .Where(m => m.Id > lastId && m.MetadataJson != null &&
                            (m.MetadataJson.Contains(LegacyPartitionKey) || m.MetadataJson.Contains("\"nfo")))
                .OrderBy(m => m.Id)
                .Take(BatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0) break;
            lastId = batch[^1].Id;

            foreach (var item in batch)
            {
                JsonObject? root;
                try { root = JsonNode.Parse(item.MetadataJson!)?.AsObject(); }
                catch (JsonException) { continue; }
                if (root is null) continue;

                var hadData = root.Remove(LegacyPartitionKey);
                var changed = hadData;
                if (root["fileScanner"] is JsonObject fs)
                {
                    foreach (var key in FileScannerNfoKeys)
                    {
                        if (!fs.TryGetPropertyValue(key, out var value)) continue;
                        hadData |= value is not null;
                        fs.Remove(key);
                        changed = true;
                    }
                }
                if (!changed) continue;

                item.MetadataJson = root.ToJsonString();
                cleaned++;
                if (hadData) toResolve.Add(item.Id);
            }
            await db.SaveChangesAsync(ct);
        }
        return (cleaned, toResolve);
    }

    private async Task<(int Credits, int People)> RemoveNfoCreditsAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        // Loaded and removed through the change tracker (a few thousand rows at most) rather than
        // ExecuteDeleteAsync, so the person-stub removal below can't cascade onto credit rows
        // the tracker still thinks exist.
        var credits = await db.MediaCredits.Where(c => c.Source == LegacyCreditSource).ToListAsync(ct);
        var creditPeople = credits
            .Where(c => c.PersonMediaItemId != null)
            .Select(c => c.PersonMediaItemId!.Value)
            .Distinct()
            .ToList();
        db.MediaCredits.RemoveRange(credits);
        await db.SaveChangesAsync(ct);

        var orphaned = (await db.MediaItems
                .Where(m => creditPeople.Contains(m.Id)
                            && !db.MediaCredits.Any(c => c.PersonMediaItemId == m.Id)
                            && !db.UserLibraries.Any(l => l.MediaItemId == m.Id))
                .ToListAsync(ct))
            .Where(HasNoProviderData)
            .ToList();
        db.MediaItems.RemoveRange(orphaned);
        await db.SaveChangesAsync(ct);
        return (credits.Count, orphaned.Count);
    }

    private async Task ResolveAsync(List<int> ids, CancellationToken ct)
    {
        foreach (var chunk in ids.Chunk(BatchSize))
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var resolver = scope.ServiceProvider.GetRequiredService<IMetadataResolutionService>();
            var items = await db.MediaItems.Include(m => m.MediaType)
                .Where(m => chunk.Contains(m.Id)).ToListAsync(ct);
            foreach (var item in items)
            {
                try { await resolver.ResolveAsync(item, db, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { logger.LogWarning(ex, "Re-resolve failed for item {Id} during NFO purge", item.Id); }
            }
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>True when an item carries no provider partition (only reserved "_" keys, if any).</summary>
    private static bool HasNoProviderData(MediaItem item)
    {
        if (string.IsNullOrWhiteSpace(item.MetadataJson)) return true;
        try
        {
            using var doc = JsonDocument.Parse(item.MetadataJson);
            return doc.RootElement.ValueKind != JsonValueKind.Object
                || doc.RootElement.EnumerateObject().All(p => p.Name.StartsWith('_'));
        }
        catch (JsonException) { return false; }
    }
}
