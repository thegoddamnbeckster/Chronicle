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

        // All installed metadata plugins — we want a row for every plugin,
        // even if it has no enrichment records yet (shows all-zeros).
        var metadataPlugins = await db.Plugins
            .Where(p => p.IsEnabled)
            .ToListAsync(ct);

        // Enrichment counts grouped by plugin ID
        var rows = await db.EnrichmentStatuses
            .GroupBy(x => x.PluginId)
            .Select(g => new
            {
                PluginId  = g.Key,
                Pending   = g.Count(x => x.Status == EnrichmentStatus.Pending),
                Completed = g.Count(x => x.Status == EnrichmentStatus.Completed),
                Failed    = g.Count(x => x.Status == EnrichmentStatus.Failed),
                Exhausted = g.Count(x => x.Status == EnrichmentStatus.Exhausted),
                NotFound  = g.Count(x => x.Status == EnrichmentStatus.NotFound),
                Skipped   = g.Count(x => x.Status == EnrichmentStatus.Skipped),
            })
            .ToListAsync(ct);

        var rowLookup = rows.ToDictionary(r => r.PluginId, StringComparer.OrdinalIgnoreCase);

        // Return one entry per installed metadata plugin, defaulting to zeros
        return metadataPlugins
            .Select(p =>
            {
                rowLookup.TryGetValue(p.PluginId, out var r);
                return new EnrichmentStats(
                    p.PluginId,
                    p.Name,
                    r?.Pending   ?? 0,
                    r?.Completed ?? 0,
                    r?.Failed    ?? 0,
                    r?.Exhausted ?? 0,
                    r?.NotFound  ?? 0,
                    r?.Skipped   ?? 0);
            })
            .ToList();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task EnrichOneAsync(ChronicleDbContext db, IMetadataProvider provider,
        MediaItemEnrichmentStatus row, CancellationToken ct)
    {
        row.LastAttemptedAt = DateTime.UtcNow;
        try
        {
            MediaMetadata? result = null;
            string searchQuery = string.Empty;

            if (!string.IsNullOrEmpty(row.ExternalId))
            {
                // Validate the stored ExternalId's entity type is appropriate for
                // this item's position in the hierarchy.  For MusicBrainz IDs the
                // format is "{type}:{mbid}"; a recording: ID on an artist-level item
                // (ParentId == null) means a prior enrichment made a wrong match —
                // discard it and re-search so the correct entity type is used.
                bool idIsValid = true;
                if (row.MediaItem is not null)
                {
                    var sep = row.ExternalId.IndexOf(':');
                    if (sep > 0)
                    {
                        var entityType = row.ExternalId[..sep];
                        if (row.MediaItem.ParentId == null)
                        {
                            // Root item — must be an artist (or movie/show-level TMDB id)
                            idIsValid = entityType is "artist" or "movie" or "tv";
                        }
                        else
                        {
                            // Check parent depth: load parent once
                            var parent = await db.MediaItems
                                .AsNoTracking()
                                .FirstOrDefaultAsync(m => m.Id == row.MediaItem.ParentId, ct);
                            if (parent?.ParentId == null)
                                // Direct child of root = album/season level
                                idIsValid = entityType is "release-group" or "season" or "tv";
                            else
                                // Grandchild = track/episode level
                                idIsValid = entityType is "recording" or "episode";
                        }
                    }
                }

                if (idIsValid)
                {
                    result = await provider.GetByIdAsync(row.ExternalId, ct);
                }
                else
                {
                    logger.LogWarning(
                        "Discarding stale ExternalId {ExternalId} for item {ItemId} — " +
                        "entity type does not match hierarchy level; will re-search.",
                        row.ExternalId, row.MediaItemId);
                    row.ExternalId = null;
                }
            }

            if (result is null && row.ExternalId is null && row.MediaItem is not null)
            {
                var supportedTypes = provider.GetSupportedMediaTypes()
                    .Select(t => t.MediaTypeName)
                    .ToList();

                var mediaTypeName = await db.MediaTypes
                    .Where(t => t.Id == row.MediaItem.MediaTypeId)
                    .Select(t => t.Name)
                    .FirstOrDefaultAsync(ct);

                if (mediaTypeName is not null && supportedTypes.Contains(mediaTypeName))
                {
                    // For music items all hierarchy levels share the same media type name.
                    // We determine what MusicBrainz entity to search for from ParentId depth
                    // and build a Lucene-style query with parent/grandparent context so that
                    // album and track searches are precise rather than bare-name lookups.
                    //   no parent  → artist search: artist:"Metallica"
                    //   parent is root → album search: album:"Load" AND artist:"Metallica"
                    //   has grandparent → track search: track:"Until It Sleeps" AND artist:"Metallica" AND release:"Load"
                    searchQuery = row.MediaItem.Name;
                    if (mediaTypeName is "music" or "album" or "artist")
                    {
                        if (row.MediaItem.ParentId == null)
                        {
                            searchQuery = $"artist:{MbQuote(row.MediaItem.Name)}";
                        }
                        else
                        {
                            var parent = await db.MediaItems
                                .FirstOrDefaultAsync(m => m.Id == row.MediaItem.ParentId, ct);

                            if (parent?.ParentId == null)
                            {
                                // Album level — add artist context for precision.
                                // Strip leading "(YYYY) " from the album name before searching
                                // because file scanners prepend the year for sort order but
                                // MusicBrainz stores the canonical title without it.
                                var artistClause = !string.IsNullOrWhiteSpace(parent?.Name)
                                    ? $" AND artist:{MbQuote(parent.Name)}"
                                    : string.Empty;
                                searchQuery = $"album:{MbQuote(StripYearPrefix(row.MediaItem.Name))}{artistClause}";
                            }
                            else
                            {
                                // Track level — add artist and album context for precision.
                                // Strip "(YYYY) " prefix from the album (release) name.
                                var grandparent = await db.MediaItems
                                    .FirstOrDefaultAsync(m => m.Id == parent.ParentId, ct);
                                var artistClause = !string.IsNullOrWhiteSpace(grandparent?.Name)
                                    ? $" AND artist:{MbQuote(grandparent.Name)}"
                                    : string.Empty;
                                var releaseClause = !string.IsNullOrWhiteSpace(parent.Name)
                                    ? $" AND release:{MbQuote(StripYearPrefix(parent.Name))}"
                                    : string.Empty;
                                searchQuery = $"track:{MbQuote(row.MediaItem.Name)}{artistClause}{releaseClause}";
                            }
                        }
                    }

                    result = await provider.SearchAsync(searchQuery, mediaTypeName, ct);

                    // SearchAsync returns only search-index fields (no cover art).
                    // If we got a match, fetch the full entity so that PosterUrl
                    // and all other extended fields (genres, overview, etc.) are populated.
                    if (result is not null && !string.IsNullOrEmpty(result.ExternalId))
                    {
                        try
                        {
                            var fullResult = await provider.GetByIdAsync(result.ExternalId, ct);
                            if (fullResult is not null && !string.IsNullOrEmpty(fullResult.ExternalId))
                                result = fullResult;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex,
                                "Follow-up GetByIdAsync failed for ExternalId={ExternalId}; keeping search result",
                                result.ExternalId);
                            // Keep the search result — enrichment still succeeds; cover art just won't be set.
                        }
                    }
                }
            }

            if (result is null || string.IsNullOrEmpty(result.ExternalId))
            {
                logger.LogInformation(
                    "Enrichment not found: plugin={Plugin} item={ItemId} name={Name} query={Query} totalResults={Total}",
                    provider.PluginId, row.MediaItemId, row.MediaItem?.Name ?? "?",
                    searchQuery, result?.TotalResults ?? 0);
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

    /// <summary>
    /// Wraps a MusicBrainz Lucene search term in double quotes for exact phrase matching,
    /// escaping any embedded double quotes. Example: Load → "Load", AC/DC → "AC/DC".
    /// </summary>
    private static string MbQuote(string term) =>
        $"\"{term.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    /// <summary>
    /// Strips a leading "(YYYY) " year prefix from a name before building MusicBrainz queries.
    /// File scanners often prepend the year (e.g. "(2008) 3 Doors Down") for sort order, but
    /// MusicBrainz stores the canonical title without it.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex YearPrefixRe =
        new(@"^\(\d{4}\)\s*", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string StripYearPrefix(string name) =>
        YearPrefixRe.Replace(name, string.Empty);

    private static async Task MergeMetadataAsync(ChronicleDbContext db, MediaItem item,
        string pluginId, MediaMetadata result, CancellationToken ct)
    {
        var existing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            item.MetadataJson ?? "{}") ?? [];

        // Clear the Results/TotalResults fields before serializing — they are search-result
        // list data used by the UI and must not be persisted (they also create a circular
        // reference when the best result points back into its own Results list).
        var savedResults = result.Results;
        var savedTotal   = result.TotalResults;
        result.Results      = null;
        result.TotalResults = 0;
        try
        {
            existing[pluginId] = JsonSerializer.SerializeToElement(result);
            item.MetadataJson  = JsonSerializer.Serialize(existing);
        }
        finally
        {
            result.Results      = savedResults;
            result.TotalResults = savedTotal;
        }

        if (!string.IsNullOrEmpty(result.PosterUrl) && string.IsNullOrEmpty(item.PosterUrl))
            item.PosterUrl = result.PosterUrl;

        await db.SaveChangesAsync(ct);
    }
}
