using System.Reflection;
using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Chronicle.Services;

/// <summary>
/// Hosted background service that periodically refreshes metadata for all
/// library items using every active, applicable IMetadataProvider plugin.
/// </summary>
public sealed class MetadataRefreshService : BackgroundService, IMetadataRefreshService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _log = Log.ForContext<MetadataRefreshService>();

    private static readonly TimeSpan StartupDelay    = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(4);

    public MetadataRefreshService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    // ── BackgroundService ─────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.Information("MetadataRefreshService starting (startup delay {Delay}s)", StartupDelay.TotalSeconds);
        await Task.Delay(StartupDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            _log.Information("MetadataRefreshService: starting full library refresh pass");
            try
            {
                await RefreshAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Error(ex, "MetadataRefreshService: unhandled error in refresh pass");
            }

            var interval = await GetIntervalAsync(stoppingToken);
            _log.Information("MetadataRefreshService: next pass in {Hours}h", interval.TotalHours);
            await Task.Delay(interval, stoppingToken);
        }
    }

    // ── IMetadataRefreshService ───────────────────────────────────────────────

    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db       = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<IPluginRegistry>();

        var itemIds = await db.UserLibraries
            .Select(ul => ul.MediaItemId)
            .Distinct()
            .ToListAsync(ct);

        var rootItems = await db.MediaItems
            .Include(m => m.MediaType)
            .Include(m => m.ExternalIds)
            .Where(m => itemIds.Contains(m.Id) && m.HierarchyLevel == 0)
            .ToListAsync(ct);

        _log.Information("MetadataRefreshService: {Count} root items to refresh", rootItems.Count);

        foreach (var item in rootItems)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await RefreshItemCoreAsync(db, registry, item, ct);
                await Task.Delay(500, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Warning(ex, "MetadataRefreshService: error refreshing item {Id} '{Name}'", item.Id, item.Name);
            }
        }
    }

    public async Task RefreshItemAsync(int mediaItemId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db       = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<IPluginRegistry>();

        var item = await db.MediaItems
            .Include(m => m.MediaType)
            .Include(m => m.ExternalIds)
            .FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);

        if (item is null)
        {
            _log.Warning("MetadataRefreshService: item {Id} not found", mediaItemId);
            return;
        }

        await RefreshItemCoreAsync(db, registry, item, ct);
    }

    public async Task<IReadOnlyList<MediaItemRefreshLog>> GetRefreshLogsAsync(
        int mediaItemId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        var all = await db.MediaItemRefreshLogs
            .Where(l => l.MediaItemId == mediaItemId)
            .OrderByDescending(l => l.RefreshedAt)
            .ToListAsync(ct);

        return all
            .GroupBy(l => l.ProviderName)
            .Select(g => g.First())
            .ToList();
    }

    // ── Core refresh logic ────────────────────────────────────────────────────

    private async Task RefreshItemCoreAsync(
        ChronicleDbContext db,
        IPluginRegistry registry,
        MediaItem item,
        CancellationToken ct)
    {
        var providers          = registry.GetMetadataProviders();
        var mediaTypeName      = item.MediaType?.Name ?? string.Empty;
        var normalizedTypeName = ToMediaTypeHint(mediaTypeName);

        foreach (var provider in providers)
        {
            // Normalize the Chronicle media type name (e.g. "movies" → "movie") before
            // checking provider support, so that pluralised DB names don't silently skip items.
            var supported = provider.GetSupportedMediaTypes()
                .Any(m => string.Equals(m.MediaTypeName, normalizedTypeName, StringComparison.OrdinalIgnoreCase));

            if (!supported)
            {
                _log.Debug("Skipping provider {Provider} for item {Id} (type '{Type}' not supported)",
                    provider.Name, item.Id, mediaTypeName);
                continue;
            }

            var log = new MediaItemRefreshLog
            {
                MediaItemId  = item.Id,
                ProviderName = provider.Name,
                RefreshedAt  = DateTime.UtcNow,
                Succeeded    = false
            };

            try
            {
                // Resolve external ID for this provider (fall back to "tmdb" source for backwards compat)
                var extId = item.ExternalIds
                    .FirstOrDefault(e =>
                        string.Equals(e.Source, provider.PluginId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(e.Source, "tmdb", StringComparison.OrdinalIgnoreCase))
                    ?.ExternalId;

                // "__suppress__" means the user explicitly opted out of auto-matching for
                // this provider. Skip silently — no log entry, no search.
                if (string.Equals(extId, "__suppress__", StringComparison.Ordinal))
                {
                    _log.Debug("Skipping suppressed item {Id} '{Name}' for provider {Provider}",
                        item.Id, item.Name, provider.Name);
                    continue;
                }

                if (extId is null)
                {
                    var hint         = ToMediaTypeHint(mediaTypeName);
                    var searchResult = await provider.SearchAsync(item.Name, hint, ct);
                    var best         = searchResult.Results
                        .Select(r => new { r, Score = ScoreByNameYear(r.Title, r.Year, item.Name, item.Year) })
                        .OrderByDescending(x => x.Score)
                        .FirstOrDefault();

                    if (best is null)
                    {
                        _log.Information("No match from {Provider} for '{Name}'", provider.Name, item.Name);
                        log.ErrorMessage = "No search results matched";
                        continue; // finally block adds log; outer SaveChangesAsync persists it
                    }

                    extId = best.r.ExternalId;
                    await UpsertExternalIdAsync(db, item.Id, provider.PluginId, extId, ct);
                    item.ExternalIds = await db.MediaExternalIds
                        .Where(e => e.MediaItemId == item.Id)
                        .ToListAsync(ct);
                }

                var meta = await provider.GetByIdAsync(extId, ct);

                if (!string.IsNullOrWhiteSpace(meta.Title))         item.Name           = meta.Title;
                if (meta.Year.HasValue)                              item.Year           = meta.Year;
                if (!string.IsNullOrWhiteSpace(meta.Overview))      item.Overview       = meta.Overview;
                if (!string.IsNullOrWhiteSpace(meta.PosterUrl))     item.PosterUrl      = meta.PosterUrl;
                if (meta.RuntimeMinutes.HasValue)                    item.RuntimeMinutes = meta.RuntimeMinutes;

                item.MetadataJson = MergeMetadataJson(item.MetadataJson, provider.PluginId, meta);
                item.UpdatedAt    = DateTime.UtcNow;
                log.Succeeded     = true;

                _log.Information("Refreshed '{Name}' via {Provider}", item.Name, provider.Name);
            }
            catch (HttpRequestException ex)
                when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Stored external ID no longer exists on the provider — remove it so the
                // next cycle falls back to a fresh search rather than retrying a dead URL.
                var badIds = item.ExternalIds
                    .Where(e => string.Equals(e.Source, provider.PluginId, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(e.Source, "tmdb", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                db.MediaExternalIds.RemoveRange(badIds);

                var badExtId = badIds.FirstOrDefault()?.ExternalId ?? "unknown";
                log.ErrorMessage = $"Stored ID '{badExtId}' returned 404 from {provider.Name} — this entry no longer exists on the provider. Match cleared; will re-search on next cycle.";
                _log.Warning("{Provider}: stored ID '{ExtId}' for '{Name}' (item {Id}) returned 404 — this entry no longer exists on {Provider}. Match cleared; will re-search on next cycle.",
                    provider.Name, badExtId, item.Name, item.Id, provider.Name);
            }
            catch (Exception ex)
            {
                log.ErrorMessage = ex.Message;
                _log.Warning(ex, "{Provider} failed for item {Id}", provider.Name, item.Id);
            }
            finally
            {
                db.MediaItemRefreshLogs.Add(log);
            }
        }

        // Guard: item may have been deleted by DuplicateCleanupService while this
        // refresh was running. Attempting to insert child rows (external IDs, refresh
        // logs) for a missing parent causes a FK constraint violation. If the item
        // is gone, discard the staged changes and skip silently.
        var itemStillExists = await db.MediaItems.AnyAsync(m => m.Id == item.Id, ct);
        if (!itemStillExists)
        {
            db.ChangeTracker.Clear();
            _log.Information("MetadataRefreshService: item {Id} was removed before save — skipping", item.Id);
            return;
        }

        await db.SaveChangesAsync(ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<TimeSpan> GetIntervalAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var setting = await db.AppSettings
                .FirstOrDefaultAsync(s => s.Key == "metadata_refresh_interval_hours", ct);
            if (setting is not null && double.TryParse(setting.Value, out var hours) && hours > 0)
                return TimeSpan.FromHours(hours);
        }
        catch { /* fall back to default */ }
        return DefaultInterval;
    }

    private static async Task UpsertExternalIdAsync(
        ChronicleDbContext db, int mediaItemId, string source, string externalId, CancellationToken ct)
    {
        var existing = await db.MediaExternalIds
            .FirstOrDefaultAsync(e => e.MediaItemId == mediaItemId && e.Source == source, ct);
        if (existing is null)
        {
            db.MediaExternalIds.Add(new MediaExternalId
            {
                MediaItemId = mediaItemId,
                Source      = source,
                ExternalId  = externalId
            });
        }
        else
        {
            existing.ExternalId = externalId;
        }
        await db.SaveChangesAsync(ct);
    }

    private static string MergeMetadataJson(
        string? existingJson, string pluginId, Chronicle.Plugins.Models.MediaMetadata meta)
    {
        var root = new Dictionary<string, object?>();

        if (!string.IsNullOrEmpty(existingJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existingJson);
                if (parsed is not null)
                    foreach (var kv in parsed)
                        root[kv.Key] = kv.Value;
            }
            catch { /* discard unparseable JSON */ }
        }

        // Derive short namespace key from plugin ID suffix (e.g. "chronicle.plugin.tmdb" → "tmdb")
        var ns = pluginId.Contains('.') ? pluginId.Split('.').Last() : pluginId;

        root[ns] = new
        {
            rating      = meta.Rating,
            genres      = meta.Genres,
            cast        = meta.Cast,
            directors   = meta.Directors,
            posterUrl   = meta.PosterUrl,
            backdropUrl = meta.BackdropUrl,
            overview    = meta.Overview
        };

        return JsonSerializer.Serialize(root);
    }

    private static string ToMediaTypeHint(string mediaTypeName) =>
        mediaTypeName.ToLowerInvariant() switch
        {
            "movies" or "movie" => "movie",
            "tv" or "tv shows"  => "tv",
            "music"             => "music",
            _                   => mediaTypeName.ToLowerInvariant()
        };

    private static int ScoreByNameYear(
        string? candidateTitle, int? candidateYear, string itemName, int? itemYear)
    {
        int score = 0;
        if (string.Equals(candidateTitle, itemName, StringComparison.OrdinalIgnoreCase))
            score += 60;
        else if (candidateTitle?.Contains(itemName, StringComparison.OrdinalIgnoreCase) == true)
            score += 30;
        if (itemYear.HasValue && candidateYear == itemYear)
            score += 40;
        return score;
    }
}
