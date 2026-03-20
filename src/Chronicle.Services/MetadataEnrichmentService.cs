using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

public class MetadataEnrichmentService(
    IServiceScopeFactory scopeFactory,
    ILogger<MetadataEnrichmentService> logger) : IMetadataEnrichmentService
{
    private static readonly TimeSpan RetryWindow = TimeSpan.FromHours(24);

    public async Task EnrichPendingAsync(string pluginId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<IPluginRegistry>();

        var provider = registry.GetMetadataProvider(pluginId);
        if (provider is null)
        {
            logger.LogWarning("Plugin {PluginId} not found in registry", pluginId);
            return;
        }

        var cutoff = DateTime.UtcNow - RetryWindow;
        var rows = await db.EnrichmentStatuses
            .Include(x => x.MediaItem)
            .Where(x => x.PluginId == pluginId &&
                        (x.Status == EnrichmentStatus.Pending ||
                         (x.Status == EnrichmentStatus.Failed &&
                          (x.LastAttemptedAt == null || x.LastAttemptedAt < cutoff))))
            .ToListAsync(ct);

        logger.LogInformation("Enriching {Count} items for plugin {PluginId}", rows.Count, pluginId);

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            await EnrichOneAsync(db, provider, row, ct);
        }
    }

    public async Task EnrichAllAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<IPluginRegistry>();
        var pluginIds = registry.GetMetadataProviders().Select(p => p.PluginId).ToList();
        foreach (var id in pluginIds)
        {
            ct.ThrowIfCancellationRequested();
            await EnrichPendingAsync(id, ct);
        }
    }

    public async Task ResetAsync(string pluginId, ResetScope scope, int? mediaItemId = null, CancellationToken ct = default)
    {
        await using var svc = scopeFactory.CreateAsyncScope();
        var db = svc.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        IQueryable<MediaItemEnrichmentStatus> query = db.EnrichmentStatuses
            .Where(x => x.PluginId == pluginId);

        query = scope switch
        {
            ResetScope.Single       => query.Where(x => x.MediaItemId == mediaItemId),
            ResetScope.AllExhausted => query.Where(x => x.Status == EnrichmentStatus.Exhausted),
            ResetScope.AllForPlugin => query.Where(x => x.Status != EnrichmentStatus.Skipped),
            _                       => query
        };

        // Load entities and update in memory to stay compatible with the EF InMemory provider
        // (which does not support ExecuteUpdateAsync bulk operations).
        var rows = await query.ToListAsync(ct);
        foreach (var row in rows)
        {
            row.Status          = EnrichmentStatus.Pending;
            row.RetryCount      = 0;
            row.ErrorMessage    = null;
            row.LastAttemptedAt = null;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task SkipAsync(int mediaItemId, string pluginId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        var row = await db.EnrichmentStatuses
            .FirstOrDefaultAsync(x => x.MediaItemId == mediaItemId && x.PluginId == pluginId, ct);
        if (row is not null)
        {
            row.Status = EnrichmentStatus.Skipped;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<IReadOnlyList<EnrichmentStats>> GetStatsAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        return await db.EnrichmentStatuses
            .GroupBy(x => x.PluginId)
            .Select(g => new EnrichmentStats(
                g.Key,
                g.Count(x => x.Status == EnrichmentStatus.Pending),
                g.Count(x => x.Status == EnrichmentStatus.Completed),
                g.Count(x => x.Status == EnrichmentStatus.Failed),
                g.Count(x => x.Status == EnrichmentStatus.Exhausted),
                g.Count(x => x.Status == EnrichmentStatus.NotFound),
                g.Count(x => x.Status == EnrichmentStatus.Skipped)
            ))
            .ToListAsync(ct);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task EnrichOneAsync(ChronicleDbContext db, IMetadataProvider provider,
        MediaItemEnrichmentStatus row, CancellationToken ct)
    {
        row.LastAttemptedAt = DateTime.UtcNow;
        try
        {
            MediaMetadata? result = null;

            if (!string.IsNullOrEmpty(row.ExternalId))
            {
                result = await provider.GetByIdAsync(row.ExternalId, ct);
            }
            else if (row.MediaItem is not null)
            {
                var supportedTypes = provider.GetSupportedMediaTypes()
                    .Select(t => t.MediaTypeName)
                    .ToList();

                var mediaTypeName = await db.MediaTypes
                    .Where(t => t.Id == row.MediaItem.MediaTypeId)
                    .Select(t => t.Name)
                    .FirstOrDefaultAsync(ct);

                if (mediaTypeName is not null && supportedTypes.Contains(mediaTypeName))
                    result = await provider.SearchAsync(row.MediaItem.Name, mediaTypeName, ct);
            }

            if (result is null || string.IsNullOrEmpty(result.ExternalId))
            {
                row.Status = EnrichmentStatus.NotFound;
            }
            else
            {
                row.ExternalId      = result.ExternalId;
                row.Status          = EnrichmentStatus.Completed;
                row.LastCompletedAt = DateTime.UtcNow;
                row.ErrorMessage    = null;
                await MergeMetadataAsync(db, row.MediaItem!, provider.PluginId, result, ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Enrichment failed for item {ItemId} plugin {PluginId}",
                row.MediaItemId, row.PluginId);
            row.RetryCount++;
            row.ErrorMessage = ex.Message;
            row.Status = row.RetryCount >= row.MaxRetries
                ? EnrichmentStatus.Exhausted
                : EnrichmentStatus.Failed;
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task MergeMetadataAsync(ChronicleDbContext db, MediaItem item,
        string pluginId, MediaMetadata result, CancellationToken ct)
    {
        var existing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            item.MetadataJson ?? "{}") ?? [];
        existing[pluginId] = JsonSerializer.SerializeToElement(result);
        item.MetadataJson  = JsonSerializer.Serialize(existing);

        if (!string.IsNullOrEmpty(result.PosterUrl) && string.IsNullOrEmpty(item.PosterUrl))
            item.PosterUrl = result.PosterUrl;

        await db.SaveChangesAsync(ct);
    }
}
