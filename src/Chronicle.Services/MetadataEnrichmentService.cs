using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Services.Plugins;
using Chronicle.Services.Scan;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

public class MetadataEnrichmentService(
    IServiceScopeFactory scopeFactory,
    ILogger<MetadataEnrichmentService> logger) : IMetadataEnrichmentService
{
    private static readonly TimeSpan RetryWindow = TimeSpan.FromHours(24);
    private static readonly NfoSignalExtractor _nfoExtractor = new();

    // ── Unified entry points ───────────────────────────────────────────────────

    public async Task EnrichItemAsync(
        int mediaItemId, string pluginId,
        EnrichmentOptions options, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db       = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<IPluginRegistry>();

        var item = await db.MediaItems
            .Include(m => m.MediaType)
            .Include(m => m.Parent)
            .FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);
        if (item is null) return;

        var provider = registry.GetMetadataProvider(pluginId);
        if (provider is null) return;

        await EnrichItemCoreAsync(db, provider, pluginId, item, options, ct);
    }

    public async Task EnrichItemAsync(
        int mediaItemId, EnrichmentOptions options, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db       = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<IPluginRegistry>();

        var item = await db.MediaItems
            .Include(m => m.MediaType)
            .Include(m => m.Parent)
            .FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);
        if (item is null) return;

        var mediaTypeName = NormalizeMediaTypeName(item.MediaType?.Name ?? string.Empty);

        foreach (var (pluginId, provider) in registry.GetMetadataProviderEntries())
        {
            ct.ThrowIfCancellationRequested();
            var supported = provider.GetSupportedMediaTypes()
                .Any(t => string.Equals(
                    NormalizeMediaTypeName(t.MediaTypeName), mediaTypeName,
                    StringComparison.OrdinalIgnoreCase));
            if (!supported) continue;

            try { await EnrichItemCoreAsync(db, provider, pluginId, item, options, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "EnrichItemAsync all-plugins: plugin {P} failed for item {Id}", pluginId, mediaItemId);
            }
        }
    }

    public async Task<IReadOnlyList<EnrichmentRecord>> GetEnrichmentRecordsAsync(
        int mediaItemId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        var rows = await db.MediaEnrichments
            .Where(e => e.MediaItemId == mediaItemId)
            .ToListAsync(ct);

        return rows.Select(r => new EnrichmentRecord(
            r.PluginId, r.ExternalId, r.Status,
            r.LastCompletedAt, r.ErrorMessage, r.DiagnosticsJson))
            .ToList();
    }

    // ── Background / batch operations ─────────────────────────────────────────
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

        var supportedTypes = provider.GetSupportedMediaTypes()
            .Select(t => NormalizeMediaTypeName(t.MediaTypeName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var cutoff = DateTime.UtcNow - RetryWindow;
        var rows = await db.MediaEnrichments
            .Include(x => x.MediaItem)
                .ThenInclude(m => m!.MediaType)
            .Include(x => x.MediaItem)
                .ThenInclude(m => m!.Parent)
                    .ThenInclude(p => p!.Parent)
            .Where(x => x.PluginId == pluginId &&
                        (x.Status == EnrichmentStatus.Pending ||
                         (x.Status == EnrichmentStatus.Failed &&
                          (x.LastAttemptedAt == null || x.LastAttemptedAt < cutoff))))
            .ToListAsync(ct);

        // Filter out items whose media type is not supported by this plugin.
        // This prevents e.g. TMDB from processing music items it will never match.
        if (supportedTypes.Count > 0)
            rows = rows.Where(r =>
            {
                var mt = NormalizeMediaTypeName(r.MediaItem?.MediaType?.Name ?? string.Empty);
                return supportedTypes.Contains(mt);
            }).ToList();

        logger.LogInformation("Enriching {Count} items for plugin {PluginId}", rows.Count, pluginId);

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            await EnrichOneAsync(db, provider, row, ct);
        }
    }

    public async Task ResyncAllForPluginAsync(string pluginId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        await using var registryScope = scopeFactory.CreateAsyncScope();
        var registry = registryScope.ServiceProvider.GetRequiredService<IPluginRegistry>();
        var provider = registry.GetMetadataProvider(pluginId);
        var supportedTypes = provider?.GetSupportedMediaTypes()
            .Select(t => NormalizeMediaTypeName(t.MediaTypeName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var libraryItemIds = await db.UserLibraries
            .Select(ul => ul.MediaItemId)
            .Distinct()
            .ToListAsync(ct);

        var rootItems = await db.MediaItems
            .Include(m => m.MediaType)
            .Where(m => libraryItemIds.Contains(m.Id) && m.HierarchyLevel == 0)
            .ToListAsync(ct);

        var rootIds = supportedTypes is { Count: > 0 }
            ? rootItems
                .Where(m => supportedTypes.Contains(NormalizeMediaTypeName(m.MediaType?.Name ?? string.Empty)))
                .Select(m => m.Id)
                .ToList()
            : rootItems.Select(m => m.Id).ToList();

        logger.LogInformation(
            "ResyncAllForPlugin {PluginId}: force-refreshing {Count} root items",
            pluginId, rootIds.Count);

        foreach (var id in rootIds)
        {
            ct.ThrowIfCancellationRequested();
            await EnrichItemAsync(id, pluginId,
                new EnrichmentOptions(EnrichmentMode.Force, Cascade: true), ct);
        }
    }

    public async Task EnrichAllAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<IPluginRegistry>();
        var pluginIds = registry.GetMetadataProviderEntries().Select(e => e.PluginId).ToList();
        foreach (var id in pluginIds)
        {
            ct.ThrowIfCancellationRequested();
            await EnrichPendingAsync(id, ct);
        }
    }

    public async Task SeedEnrichmentRowsFromExternalIdsAsync(CancellationToken ct = default)
    {
        await using var svc = scopeFactory.CreateAsyncScope();
        var db = svc.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        // Build a short-suffix → canonical-plugin-id map from installed plugins.
        // Handles both old short-form sources ("musicbrainz") and new full-form
        // ("chronicle.plugin.musicbrainz") that may appear in media_external_ids.Source.
        var installedPluginIds = await db.Plugins
            .Where(p => p.IsEnabled)
            .Select(p => p.PluginId)
            .ToListAsync(ct);

        var shortToFull = installedPluginIds
            .GroupBy(pid => pid.Contains('.') ? pid.Split('.').Last() : pid,
                     StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        string CanonicalPluginId(string source)
        {
            // Already a full canonical ID (present in installed list verbatim)
            if (installedPluginIds.Contains(source, StringComparer.OrdinalIgnoreCase))
                return source;
            // Try mapping short suffix → full ID
            var suffix = source.Contains('.') ? source.Split('.').Last() : source;
            return shortToFull.GetValueOrDefault(suffix, source);
        }

        // Load all existing enrichment rows (for deduplication)
        var enrichmentSet = (await db.MediaEnrichments
            .Select(me => new { me.MediaItemId, PluginId = me.PluginId.ToLower() })
            .ToListAsync(ct))
            .Select(e => (e.MediaItemId, e.PluginId))
            .ToHashSet();

        // Candidates: external IDs mapped to canonical plugin IDs, deduplicated
        var candidates = (await db.Set<MediaExternalId>()
            .Where(mei => mei.ExternalId != "__suppress__")
            .ToListAsync(ct))
            .Select(mei => (MediaItemId: mei.MediaItemId,
                            PluginId:    CanonicalPluginId(mei.Source),
                            ExternalId:  mei.ExternalId))
            .Where(c => !enrichmentSet.Contains((c.MediaItemId, c.PluginId.ToLower())))
            .GroupBy(c => (c.MediaItemId, c.PluginId))   // dedup same item+plugin
            .Select(g => g.First())
            .ToList();

        if (candidates.Count == 0) return;

        // Load MetadataJson for all affected items in one query
        var itemIds = candidates.Select(c => c.MediaItemId).Distinct().ToList();
        var metadataByItem = await db.MediaItems
            .Where(mi => itemIds.Contains(mi.Id))
            .Select(mi => new { mi.Id, mi.MetadataJson })
            .ToDictionaryAsync(mi => mi.Id, mi => mi.MetadataJson, ct);

        // For each candidate: Completed if plugin data is intact in MetadataJson,
        // Pending if data is absent (wiped by re-scan) so it re-enriches automatically.
        int completedCount = 0, pendingCount = 0;
        var toAdd = new List<MediaItemEnrichment>(candidates.Count);
        foreach (var (mediaItemId, pluginId, externalId) in candidates)
        {
            var json   = metadataByItem.GetValueOrDefault(mediaItemId);
            var status = HasPluginDataInJson(json, pluginId)
                ? EnrichmentStatus.Completed
                : EnrichmentStatus.Pending;

            if (status == EnrichmentStatus.Completed) completedCount++;
            else                                       pendingCount++;

            toAdd.Add(new MediaItemEnrichment
            {
                MediaItemId = mediaItemId,
                PluginId    = pluginId,
                ExternalId  = externalId,
                Status      = status,
                RetryCount  = 0,
                MaxRetries  = 3,
            });
        }

        if (toAdd.Count > 0)
        {
            db.MediaEnrichments.AddRange(toAdd);
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "SeedEnrichmentRows: created {Completed} Completed + {Pending} Pending rows from media_external_ids",
                completedCount, pendingCount);
        }

        // ── Phase 2: reset "stuck" rows ───────────────────────────────────────
        // Completed or Exhausted rows where MetadataJson has no plugin data are
        // broken — either they were seeded incorrectly or data was wiped and the
        // row was never cleared. Reset them to Pending so they re-enrich on the
        // next background pass (which will also apply the CAA release-level fallback
        // and the Lucene operator fixes introduced alongside this change).
        var stuckRows = await db.MediaEnrichments
            .Include(me => me.MediaItem)
            .Where(me => me.Status == EnrichmentStatus.Completed ||
                         me.Status == EnrichmentStatus.Exhausted  ||
                         me.Status == EnrichmentStatus.NotFound)
            .ToListAsync(ct);

        int resetCount = 0;
        foreach (var row in stuckRows)
        {
            if (HasPluginDataInJson(row.MediaItem?.MetadataJson, row.PluginId)) continue;
            row.Status     = EnrichmentStatus.Pending;
            row.RetryCount = 0;
            row.ErrorMessage = null;
            resetCount++;
        }

        if (resetCount > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "SeedEnrichmentRows: reset {Count} Completed/Exhausted/NotFound rows with no plugin data to Pending",
                resetCount);
        }
    }

    /// <summary>
    /// Returns true if <paramref name="json"/> contains a top-level property for the given plugin.
    /// Checks both the full plugin ID (e.g. "chronicle.plugin.musicbrainz") and the short suffix
    /// ("musicbrainz") to handle data written by older code versions.
    /// </summary>
    private static bool HasPluginDataInJson(string? json, string pluginId)
    {
        if (string.IsNullOrEmpty(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty(pluginId, out _)) return true;
            // Also check short-form key written by older code (e.g. "musicbrainz")
            var shortId = pluginId.Contains('.') ? pluginId.Split('.').Last() : null;
            if (shortId is not null && root.TryGetProperty(shortId, out _)) return true;
            return false;
        }
        catch { return false; }
    }

    public async Task ResetAsync(string pluginId, ResetScope scope, int? mediaItemId = null, CancellationToken ct = default)
    {
        await using var svc = scopeFactory.CreateAsyncScope();
        var db = svc.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        IQueryable<MediaItemEnrichment> query = db.MediaEnrichments
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

        var row = await db.MediaEnrichments
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
        var db       = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<IPluginRegistry>();

        // Only show plugins that are actually registered as metadata providers.
        // Plugins like the file scanner are enabled but have no metadata provider,
        // so they must not appear in the enrichment stats table.
        var metadataPluginIds = registry.GetMetadataProviderEntries()
            .Select(e => e.PluginId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var metadataPlugins = await db.Plugins
            .Where(p => p.IsEnabled && metadataPluginIds.Contains(p.PluginId))
            .ToListAsync(ct);

        // Enrichment counts grouped by plugin ID
        var rows = await db.MediaEnrichments
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

    public async Task<PagedEnrichmentItems> GetItemsAsync(
        string pluginId, string? status, int page, int pageSize, string? search,
        CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        IQueryable<MediaItemEnrichment> query = db.MediaEnrichments
            .Include(x => x.MediaItem)
                .ThenInclude(m => m!.MediaType)
            .Include(x => x.MediaItem)
                .ThenInclude(m => m!.Parent)
                    .ThenInclude(p => p!.Parent)
            .Where(x => x.PluginId == pluginId);

        if (!string.IsNullOrEmpty(status) &&
            Enum.TryParse<EnrichmentStatus>(status, ignoreCase: true, out var parsedStatus))
            query = query.Where(x => x.Status == parsedStatus);

        if (!string.IsNullOrEmpty(search))
            query = query.Where(x => x.MediaItem != null &&
                                     x.MediaItem.Name.Contains(search));

        var total = await query.CountAsync(ct);

        var rows = await query
            .OrderBy(x => x.MediaItem != null ? x.MediaItem.Name : string.Empty)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var items = rows.Select(row =>
        {
            string? scannerJson = null;
            if (row.MediaItem?.MetadataJson is { } mj)
            {
                try
                {
                    using var doc = JsonDocument.Parse(mj);
                    if (doc.RootElement.TryGetProperty("fileScanner", out var fs))
                        scannerJson = fs.GetRawText();
                }
                catch { /* ignore */ }
            }

            return new EnrichmentItemResult(
                row.Id,
                row.MediaItemId,
                row.MediaItem?.Name ?? "(unknown)",
                row.MediaItem?.Year,
                row.MediaItem?.MediaType?.DisplayName ?? row.MediaItem?.MediaType?.Name ?? "Unknown",
                row.MediaItem?.HierarchyLevel ?? 0,
                row.MediaItem?.PosterUrl,
                row.ExternalId,
                row.Status,
                row.ErrorMessage,
                row.RetryCount,
                row.MaxRetries,
                row.LastAttemptedAt,
                row.DiagnosticsJson,
                scannerJson,
                row.MediaItem?.Parent?.Name,
                row.MediaItem?.Parent?.Parent?.Name);
        }).ToList();

        return new PagedEnrichmentItems(items, total, page, pageSize);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task EnrichOneAsync(ChronicleDbContext db, IMetadataProvider provider,
        MediaItemEnrichment row, CancellationToken ct)
    {
        row.LastAttemptedAt = DateTime.UtcNow;
        string searchQuery = string.Empty;
        List<MediaMetadata> rawCandidates = [];
        try
        {
            MediaMetadata? result = null;

            // ── Step 1: Validate any stored ExternalId ────────────────────────────
            // Clear IDs whose entity type doesn't match the item's hierarchy level.
            // This handles previously-wrong enrichments (e.g. a bare "tv:63197" stored
            // on an episode item from an earlier name-search that matched the wrong show).
            // After clearing, Step 2 will derive the correct ID from the show hierarchy.
            if (!string.IsNullOrEmpty(row.ExternalId) && row.MediaItem is not null)
            {
                bool idIsValid = true;
                var sep = row.ExternalId.IndexOf(':');
                if (sep > 0)
                {
                    var entityType = row.ExternalId[..sep];
                    if (row.MediaItem.ParentId == null)
                    {
                        // Root item — must be artist (MusicBrainz) or movie/show (TMDB)
                        idIsValid = entityType is "artist" or "movie" or "tv";
                    }
                    else
                    {
                        var parent = await db.MediaItems
                            .AsNoTracking()
                            .FirstOrDefaultAsync(m => m.Id == row.MediaItem.ParentId, ct);
                        if (parent?.ParentId == null)
                        {
                            // Season/album level — season-specific TMDB ID must contain "/season:"
                            if (entityType == "tv")
                                idIsValid = row.ExternalId.Contains("/season:", StringComparison.OrdinalIgnoreCase)
                                         || row.ExternalId.Contains(":s", StringComparison.OrdinalIgnoreCase);
                            else
                                idIsValid = entityType is "release-group" or "season";
                        }
                        else
                        {
                            // Episode/track level — MusicBrainz "recording:" or TMDB "tv:N/season:N/episode:N"
                            idIsValid = entityType is "recording" or "episode"
                                || (entityType == "tv" && row.ExternalId.Contains("/episode:", StringComparison.OrdinalIgnoreCase));
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
                        "entity type does not match hierarchy level; will re-derive.",
                        row.ExternalId, row.MediaItemId);
                    row.ExternalId = null;
                }
            }

            // ── Step 2: TV season/episode hierarchy derivation ────────────────────
            // Runs when there is no valid stored ExternalId (either originally absent
            // or just cleared above).  Constructs the correct TMDB ID from the parent
            // show's enrichment rather than searching by name, which is unreliable.
            //
            // Season:  tv:{showId}/season:{N}
            // Episode: tv:{showId}/season:{S}/episode:{E}
            //   Always use the SHOW's (grandparent's) ExternalId for the bare showId —
            //   the season's ExternalId ("tv:67198/season:5") cannot be split naively.
            bool hierarchyDerivedId = false;
            if (result is null && string.IsNullOrEmpty(row.ExternalId) && row.MediaItem?.ParentId is not null)
            {
                var parentId = row.MediaItem.ParentId;

                var parentItem = await db.MediaItems
                    .AsNoTracking()
                    .FirstOrDefaultAsync(m => m.Id == parentId, ct);

                // Use the item's Number field; fallback: parse from the name ("Season 01" → 1)
                int? itemNumber = row.MediaItem.Number;
                if (itemNumber is null && row.MediaItem.Name is { } nm)
                {
                    var numMatch = System.Text.RegularExpressions.Regex.Match(nm, @"\d+");
                    if (numMatch.Success)
                        itemNumber = int.Parse(numMatch.Value);
                }

                if (parentItem?.ParentId is not null)
                {
                    // ── Episode level: parent=season, grandparent=show ──────────────
                    var showEnrichment = await db.MediaEnrichments
                        .FirstOrDefaultAsync(
                            e => e.MediaItemId == parentItem.ParentId && e.PluginId == row.PluginId,
                            ct);

                    if (showEnrichment?.ExternalId is { } showExternalId
                        && showExternalId.StartsWith("tv:", StringComparison.OrdinalIgnoreCase)
                        && !showExternalId.Contains('/'))
                    {
                        var showTmdbId = showExternalId.Split(':', 2)[1];

                        int? seasonNumber = parentItem.Number;
                        if (seasonNumber is null && parentItem.Name is { } pnm)
                        {
                            var snm = System.Text.RegularExpressions.Regex.Match(pnm, @"\d+");
                            if (snm.Success)
                                seasonNumber = int.Parse(snm.Value);
                        }
                        if (itemNumber.HasValue && seasonNumber.HasValue)
                        {
                            row.ExternalId = $"tv:{showTmdbId}/season:{seasonNumber}/episode:{itemNumber}";
                            hierarchyDerivedId = true;
                            logger.LogInformation(
                                "TV hierarchy lookup (episode): item={ItemId} → {ExternalId}",
                                row.MediaItemId, row.ExternalId);
                        }
                    }
                    else if (showEnrichment is null
                        || showEnrichment.Status == Chronicle.Core.Models.EnrichmentStatus.Pending)
                    {
                        logger.LogDebug(
                            "Skipping episode {ItemId}: grandparent show {ShowId} not yet enriched by {PluginId}",
                            row.MediaItemId, parentItem.ParentId, row.PluginId);
                        return; // leave status as Pending
                    }
                    // else: show enriched but no tv: ID (different plugin) — fall through to search
                }
                else
                {
                    // ── Season level: parent=show ───────────────────────────────────
                    var showEnrichment = await db.MediaEnrichments
                        .FirstOrDefaultAsync(
                            e => e.MediaItemId == parentId && e.PluginId == row.PluginId,
                            ct);

                    if (showEnrichment?.ExternalId is { } showExternalId
                        && showExternalId.StartsWith("tv:", StringComparison.OrdinalIgnoreCase)
                        && !showExternalId.Contains('/'))
                    {
                        var showTmdbId = showExternalId.Split(':', 2)[1];
                        if (itemNumber.HasValue)
                        {
                            row.ExternalId = $"tv:{showTmdbId}/season:{itemNumber}";
                            hierarchyDerivedId = true;
                            logger.LogInformation(
                                "TV hierarchy lookup (season): item={ItemId} → {ExternalId}",
                                row.MediaItemId, row.ExternalId);
                        }
                    }
                    else if (showEnrichment is null
                        || showEnrichment.Status == Chronicle.Core.Models.EnrichmentStatus.Pending)
                    {
                        logger.LogDebug(
                            "Skipping child item {ItemId}: parent {ParentId} not yet enriched by {PluginId}",
                            row.MediaItemId, parentId, row.PluginId);
                        return; // leave status as Pending
                    }
                }

                // If we derived an ID, call the provider now
                if (hierarchyDerivedId && !string.IsNullOrEmpty(row.ExternalId))
                    result = await provider.GetByIdAsync(row.ExternalId, ct);
            }

            // ── NFO sidecar fallback (root items only, TMDB-style plugins) ─────────
            // Before doing a name search, check whether the item's scan folder contains
            // a tvshow.nfo or movie.nfo with a <uniqueid type="tmdb"> element.
            // This handles ambiguous show names (e.g. "What If") where year-based search
            // may still pick the wrong entry — an NFO is an authoritative identifier.
            // Only TMDB-compatible plugins recognise the numeric ID; skip for others.
            if (result is null && string.IsNullOrEmpty(row.ExternalId)
                && row.MediaItem?.ParentId is null            // root item only
                && row.PluginId.Contains("tmdb", StringComparison.OrdinalIgnoreCase))
            {
                var folderPath = TryGetFileScannerFolderPath(row.MediaItem!.MetadataJson);
                if (!string.IsNullOrEmpty(folderPath))
                {
                    var nfoId = TryReadNfoTmdbId(folderPath);
                    if (!string.IsNullOrEmpty(nfoId))
                    {
                        // Determine prefix: TV shows get "tv:", movies get "movie:"
                        var mtName = NormalizeMediaTypeName(row.MediaItem.MediaType?.Name ?? string.Empty);
                        var prefix = mtName == "tv" ? "tv" : "movie";
                        row.ExternalId = $"{prefix}:{nfoId}";
                        logger.LogInformation(
                            "NFO sidecar match: item={ItemId} ({Name}) → {ExternalId}",
                            row.MediaItemId, row.MediaItem.Name, row.ExternalId);
                        try
                        {
                            result = await provider.GetByIdAsync(row.ExternalId, ct);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            logger.LogWarning(
                                "NFO sidecar GetByIdAsync failed for {ExternalId}: {ErrorMessage}",
                                row.ExternalId, ex.Message);
                            row.ExternalId = null; // reset — fall through to name search
                        }
                    }
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

                if (mediaTypeName is not null &&
                    supportedTypes.Any(t => NormalizeMediaTypeName(t) == NormalizeMediaTypeName(mediaTypeName)))
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

                    // For leaf-level items (tracks, episodes), include sibling names so
                    // plugins can use multi-track fingerprinting to pin down the exact release.
                    IReadOnlyList<string>? siblingNames = null;
                    IReadOnlyList<string>? childNames = null;
                    IReadOnlyList<SiblingInfo>? subItemMetadata = null;

                    if (row.MediaItem.HierarchyLevel == 2 && row.MediaItem.ParentId is not null)
                    {
                        // Leaf: fetch siblings for both SiblingNames and SubItemMetadata
                        var siblingItems = await db.MediaItems
                            .Where(m => m.ParentId == row.MediaItem.ParentId
                                     && m.Id       != row.MediaItem.Id)
                            .Take(50)
                            .ToListAsync(ct);
                        if (siblingItems.Count > 0)
                        {
                            siblingNames = siblingItems.Take(8).Select(m => m.Name).ToList().AsReadOnly();
                            subItemMetadata = siblingItems
                                .Select(s => AddDurationTier2(BuildSubItemMetadataTier1(s), s))
                                .ToList()
                                .AsReadOnly();
                        }
                    }
                    else if (row.MediaItem.HierarchyLevel <= 1)
                    {
                        // Parent: fetch children — shared between ChildNames and SubItemMetadata
                        var childItems = await db.MediaItems
                            .Where(m => m.ParentId == row.MediaItem.Id)
                            .Take(200)
                            .ToListAsync(ct);
                        if (childItems.Count > 0)
                        {
                            childNames = childItems.Select(m => m.Name).ToList().AsReadOnly();
                            subItemMetadata = childItems
                                .Select(c => AddDurationTier2(BuildSubItemMetadataTier1(c), c))
                                .ToList()
                                .AsReadOnly();
                        }
                    }

                    var filenameStem = ExtractFilenameStem(row.MediaItem);
                    var searchCtx = new MediaSearchContext(
                            Name:            row.MediaItem.Name,
                            Year:            ValidateYear(row.MediaItem.Year),
                            ParentName:      row.MediaItem.Parent?.Name,
                            GrandparentName: row.MediaItem.Parent?.Parent?.Name,
                            ItemNumber:      row.MediaItem.Number,
                            HierarchyLevel:  row.MediaItem.HierarchyLevel,
                            FilenameStem:    filenameStem,
                            SiblingNames:    siblingNames,
                            AltTitles:       BuildAltTitles(
                                                 row.MediaItem.Name,
                                                 filenameStem,
                                                 null),
                            ChildNames:      childNames,
                            SubItemMetadata: subItemMetadata);

                    logger.LogDebug(
                        "Searching {Plugin} for item {ItemId} \"{Name}\" " +
                        "(level={Level}, year={Year}, parent={Parent})",
                        provider.PluginId, row.MediaItemId, row.MediaItem.Name,
                        searchCtx.HierarchyLevel, searchCtx.Year, searchCtx.ParentName ?? "(none)");

                    var searchResults = await provider.SearchAsync(searchCtx, ct);

                    // Capture candidates for diagnostics BEFORE GetByIdAsync might overwrite result
                    rawCandidates = searchResults.Take(5).Select(c => c.Metadata).ToList();

                    var topCandidate = searchResults.OrderByDescending(c => c.Score).FirstOrDefault();
                    if (topCandidate is not null)
                        logger.LogDebug(
                            "Search returned {Count} candidates for item {ItemId} \"{Name}\" " +
                            "— top: \"{TopTitle}\" ({TopId}) score={Score} reasons={Reasons}",
                            searchResults.Count, row.MediaItemId, row.MediaItem.Name,
                            topCandidate.Metadata.Title, topCandidate.Metadata.ExternalId,
                            topCandidate.Score, topCandidate.ScoreReason);
                    else
                        logger.LogDebug(
                            "Search returned 0 candidates for item {ItemId} \"{Name}\" (plugin={Plugin})",
                            row.MediaItemId, row.MediaItem.Name, provider.PluginId);

                    result = topCandidate?.Metadata;

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
                            // Log without stack trace — GetByIdAsync failures are usually transient
                            // network issues (timeouts, provider outages). The search result is still
                            // usable for identification; we'll get full metadata on the next refresh.
                            logger.LogWarning(
                                "Follow-up GetByIdAsync failed for ExternalId={ExternalId} ({ErrorType}: {ErrorMessage}); keeping search result",
                                result.ExternalId, ex.GetType().Name, ex.Message);
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
                MergeMetadata(row.MediaItem!, row.PluginId, result);
                logger.LogInformation(
                    "Enrichment matched: plugin={Plugin} item={ItemId} \"{Name}\" (level={Level}) → {ExternalId} \"{MatchedTitle}\"",
                    provider.PluginId, row.MediaItemId, row.MediaItem?.Name ?? "?",
                    row.MediaItem?.HierarchyLevel ?? -1, result.ExternalId, result.Title);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // A 404 from the provider means "this item definitively does not exist upstream" —
            // treat as NotFound rather than a transient error so retries are not wasted.
            // Example: TMDB seasons/episodes that are not yet in TMDB's database return 404.
            if (ex is HttpRequestException httpEx &&
                httpEx.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                logger.LogInformation(
                    "Enrichment not found (404): plugin={Plugin} item={ItemId} \"{Name}\" — provider returned 404",
                    row.PluginId, row.MediaItemId, row.MediaItem?.Name ?? "?");
                row.Status       = EnrichmentStatus.NotFound;
                row.ErrorMessage = ex.Message;
            }
            else
            {
                // Include stack trace only for unexpected errors; HTTP/timeout errors are self-describing.
                // Note: HttpClient timeout throws TaskCanceledException with its own internal token —
                // that is NOT an external cancellation and must be caught here so the batch continues.
                var isExpected = ex is HttpRequestException or TaskCanceledException or TimeoutException
                                                            or OperationCanceledException;
                if (isExpected)
                    logger.LogWarning(
                        "Enrichment failed for item {ItemId} plugin {PluginId}: {ErrorType}: {ErrorMessage}",
                        row.MediaItemId, row.PluginId, ex.GetType().Name, ex.Message);
                else
                    logger.LogWarning(ex, "Enrichment failed for item {ItemId} plugin {PluginId}",
                        row.MediaItemId, row.PluginId);
                row.RetryCount++;
                row.ErrorMessage = ex.Message;
                row.Status = row.RetryCount >= row.MaxRetries
                    ? EnrichmentStatus.Exhausted
                    : EnrichmentStatus.Failed;
            }
        }

        // ── Capture diagnostics ────────────────────────────────────────────────
        try
        {
            var queryName  = row.MediaItem?.Name ?? string.Empty;
            var queryYear  = row.MediaItem?.Year;
            var candidates = rawCandidates
                .Select(c =>
                {
                    var (ts, ys, tot) = ScoreCandidate(queryName, queryYear, c);
                    return new EnrichCandidate(c.Title, c.Year, c.ExternalId, ts, ys, tot);
                })
                .OrderByDescending(c => c.TotalScore)
                .ToList();

            var failureReason = row.Status switch
            {
                EnrichmentStatus.NotFound  => "No results returned by the provider for this search query.",
                EnrichmentStatus.Failed    => row.ErrorMessage ?? "Provider call threw an exception.",
                EnrichmentStatus.Exhausted => "Maximum retries reached with no successful match.",
                EnrichmentStatus.Completed => "Matched successfully.",
                _ => string.Empty
            };

            var diag = new EnrichDiagnostics(
                searchQuery,
                rawCandidates.Count,
                failureReason,
                candidates,
                ReadScannerSignals(row.MediaItem));

            row.DiagnosticsJson = JsonSerializer.Serialize(diag,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }
        catch (Exception diagEx)
        {
            logger.LogWarning(diagEx,
                "Failed to build enrichment diagnostics for item {ItemId}", row.MediaItemId);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Scores a search candidate against a query name and optional year.
    /// Title: exact=60pts, contains=30pts. Year exact match: 40pts.
    /// </summary>
    private static (int title, int year, int total) ScoreCandidate(
        string queryName, int? queryYear, MediaMetadata candidate)
    {
        int titleScore = 0;
        var cn = (candidate.Title ?? string.Empty).Trim();
        var qn = queryName.Trim();
        if (string.Equals(cn, qn, StringComparison.OrdinalIgnoreCase))
            titleScore = 60;
        else if (cn.Contains(qn, StringComparison.OrdinalIgnoreCase)
              || qn.Contains(cn, StringComparison.OrdinalIgnoreCase))
            titleScore = 30;

        int yearScore = 0;
        if (queryYear.HasValue && candidate.Year.HasValue && queryYear == candidate.Year)
            yearScore = 40;

        return (titleScore, yearScore, titleScore + yearScore);
    }

    private static EnrichScannerSignals? ReadScannerSignals(MediaItem? item)
    {
        if (item?.MetadataJson is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(item.MetadataJson);
            if (!doc.RootElement.TryGetProperty("fileScanner", out var fs)) return null;
            string? folder = fs.TryGetProperty("folderPath", out var fp) ? fp.GetString() : null;
            bool hasNfo    = fs.TryGetProperty("nfoPosterUrl", out var npo)
                             && npo.ValueKind == JsonValueKind.String
                             && !string.IsNullOrEmpty(npo.GetString());
            bool hasPoster = fs.TryGetProperty("localPosterPath", out var lp)
                             && lp.ValueKind == JsonValueKind.String
                             && !string.IsNullOrEmpty(lp.GetString());
            return new EnrichScannerSignals(folder, hasNfo, hasPoster, null);
        }
        catch { return null; }
    }

    /// <summary>
    /// Normalises a DB media-type name to the canonical form used by plugin
    /// <c>GetSupportedMediaTypes()</c> declarations.  The DB stores "movies" (plural)
    /// while many standalone plugins (e.g. TMDB) declare "movie" (singular).
    /// Both map to the same concept; treat them as equivalent for matching purposes.
    /// </summary>
    private static string NormalizeMediaTypeName(string name) =>
        name.Equals("movies", StringComparison.OrdinalIgnoreCase) ? "movie" : name.ToLowerInvariant();

    /// <summary>
    /// Strips Lucene range/special operators then wraps in double quotes for exact phrase matching.
    /// Operators like &lt;&gt;{}[]^~ break MusicBrainz SOLR if left unescaped in the query string.
    /// </summary>
    private static string MbQuote(string term)
    {
        // Remove Lucene range and boost operators that cannot be safely escaped inside phrases
        term = System.Text.RegularExpressions.Regex.Replace(term, @"[<>{}[\]^~]", "").Trim();
        return $"\"{term.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }

    /// <summary>
    /// Strips a leading "(YYYY) " year prefix from a name before building MusicBrainz queries.
    /// File scanners often prepend the year (e.g. "(2008) 3 Doors Down") for sort order, but
    /// MusicBrainz stores the canonical title without it.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex YearPrefixRe =
        new(@"^\(\d{4}\)\s*", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string StripYearPrefix(string name) =>
        YearPrefixRe.Replace(name, string.Empty);

    private static readonly System.Text.RegularExpressions.Regex YearSuffixRe =
        new(@"\s*\(\d{4}\)\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string StripYearSuffix(string name) =>
        YearSuffixRe.Replace(name, string.Empty);

    /// <summary>
    /// Returns the year if it falls within the plausible media release range
    /// (1900 to current year + 3). Returns null for values outside that range
    /// so plugins do not waste a search attempt on a garbage year.
    /// </summary>
    internal static int? ValidateYear(int? year)
    {
        if (year is null) return null;
        var maxYear = DateTime.UtcNow.Year + 3;
        return year >= 1900 && year <= maxYear ? year : null;
    }

    private static readonly System.Text.RegularExpressions.Regex VersionQualifierEnrichRe =
        new(@"\s*\([^)]+\)\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Builds an ordered, deduplicated list of title forms to try in each search stage.
    /// Order: PreciseName (if any) → year-stripped canonical name → filenameStem (if different) →
    /// version-qualifier-stripped form (if different from already-added forms).
    /// </summary>
    internal static IReadOnlyList<string> BuildAltTitles(
        string name, string? filenameStem, string? preciseName)
    {
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<string>();

        void Add(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            var trimmed = s.Trim();
            if (seen.Add(trimmed)) results.Add(trimmed);
        }

        // 1. Precise name (NFO/reliable source) first
        Add(preciseName);

        // 2. Year-stripped canonical name (strip both prefix and suffix patterns)
        var stripped = StripYearPrefix(StripYearSuffix(name)).Trim();
        Add(string.IsNullOrWhiteSpace(stripped) ? name : stripped);

        // 3. Filename stem (often cleaner than the tag title)
        Add(filenameStem);

        // 4. Version-qualifier-stripped form (e.g. "Kryptonite" from "Kryptonite (LP version)")
        //    Apply to the year-stripped form (results[0] after preciseName, or results[0] overall)
        var baseForStripping = results.Count > 0
            ? results[preciseName != null ? Math.Min(1, results.Count - 1) : 0]
            : name;
        var noQualifier = VersionQualifierEnrichRe.Replace(baseForStripping, string.Empty).Trim();
        Add(noQualifier);

        return results.AsReadOnly();
    }

    // Leading track-number prefix: "01 - ", "02. ", "3 ", "1-01 - " etc.
    private static readonly System.Text.RegularExpressions.Regex TrackNumPrefixRe =
        new(@"^\d{1,2}(?:-\d{1,2})?[\s\-._]+",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Derives a clean search-friendly title from the first file path stored in the item's
    /// fileScanner metadata.  Returns null when no file path is available or when the
    /// resulting stem is not meaningfully different from <paramref name="item"/>.Name.
    /// </summary>
    private static string? ExtractFilenameStem(Chronicle.Core.Models.MediaItem item)
    {
        if (string.IsNullOrEmpty(item.MetadataJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(item.MetadataJson);
            if (!doc.RootElement.TryGetProperty("fileScanner", out var fs)) return null;

            string? filePath = null;
            if (fs.TryGetProperty("filePaths", out var fps) &&
                fps.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                filePath = fps.EnumerateArray().FirstOrDefault().GetString();
            }
            if (filePath is null && fs.TryGetProperty("filePath", out var fp))
                filePath = fp.GetString();
            if (filePath is null) return null;

            var stem = System.IO.Path.GetFileNameWithoutExtension(filePath);
            // Strip leading track numbers: "01 - Duck and Run" → "Duck and Run"
            stem = TrackNumPrefixRe.Replace(stem, string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(stem)) return null;

            // Only return when meaningfully different from the item name (case-insensitive).
            return string.Equals(stem, item.Name, StringComparison.OrdinalIgnoreCase)
                ? null : stem;
        }
        catch { return null; }
    }

    // Track-number prefix with capture group (for BuildSubItemMetadataTier1).
    // Distinct from TrackNumPrefixRe which has no capture group and is used by ExtractFilenameStem.
    private static readonly System.Text.RegularExpressions.Regex TrackPrefixRe =
        new(@"^(\d{1,3})[\s\-\.]+",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // Disc/CD folder pattern — e.g. "Disc 1", "disk2", "CD 3"
    private static readonly System.Text.RegularExpressions.Regex DiscFolderRe =
        new(@"\b(?:disc|disk|cd)\s*(\d+)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Tier 1: extract what we can from filename and folder path alone — no file I/O beyond
    /// what the file scanner has already stored in MetadataJson.
    /// </summary>
    internal static SiblingInfo BuildSubItemMetadataTier1(Chronicle.Core.Models.MediaItem item)
    {
        string? filePath   = null;
        string? folderPath = null;

        if (!string.IsNullOrEmpty(item.MetadataJson))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(item.MetadataJson);
                if (doc.RootElement.TryGetProperty("fileScanner", out var fs))
                {
                    if (fs.TryGetProperty("filePaths", out var fps) && fps.GetArrayLength() > 0)
                        filePath = fps[0].GetString();
                    if (fs.TryGetProperty("folderPath", out var fp))
                        folderPath = fp.GetString();
                }
            }
            catch { /* corrupt JSON — ignore */ }
        }

        int? trackNumber = null;
        int? discNumber  = null;

        if (filePath is not null)
        {
            var fileName = System.IO.Path.GetFileNameWithoutExtension(filePath);
            var tm = TrackPrefixRe.Match(fileName);
            if (tm.Success && int.TryParse(tm.Groups[1].Value, out var tn))
                trackNumber = tn;
        }

        if (folderPath is not null)
        {
            var dm = DiscFolderRe.Match(folderPath);
            if (dm.Success && int.TryParse(dm.Groups[1].Value, out var dn))
                discNumber = dn;
        }

        return new SiblingInfo(
            Name:       item.Name,
            ItemNumber: trackNumber,
            DiscNumber: discNumber);
    }

    /// <summary>
    /// Tier 2: add duration (in seconds) from fileScanner metadata already stored in
    /// MetadataJson. No additional file I/O — the duration was captured during the scan.
    /// </summary>
    internal static SiblingInfo AddDurationTier2(SiblingInfo tier1, Chronicle.Core.Models.MediaItem item)
    {
        if (string.IsNullOrEmpty(item.MetadataJson)) return tier1;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(item.MetadataJson);
            if (doc.RootElement.TryGetProperty("fileScanner", out var fs) &&
                fs.TryGetProperty("duration", out var dur) &&
                dur.TryGetInt32(out var seconds))
            {
                return tier1 with { DurationSeconds = seconds };
            }
        }
        catch { }
        return tier1;
    }

    // ── NFO sidecar helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Extracts the folder path stored by the file scanner in a media item's MetadataJson,
    /// so that enrichment can look for NFO sidecars without re-walking the file system.
    /// </summary>
    private static string? TryGetFileScannerFolderPath(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.TryGetProperty("fileScanner", out var fs) &&
                fs.TryGetProperty("folderPath", out var fp))
                return fp.GetString();
        }
        catch { /* malformed JSON */ }
        return null;
    }

    /// <summary>
    /// Looks for tvshow.nfo / movie.nfo (and any *.nfo as fallback) in
    /// <paramref name="folderPath"/> and returns the numeric TMDB ID from
    /// &lt;uniqueid type="tmdb"&gt; if present.
    /// </summary>
    private static string? TryReadNfoTmdbId(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return null;
        try
        {
            // Prefer well-known names (Kodi/Jellyfin convention)
            foreach (var name in new[] { "tvshow.nfo", "movie.nfo" })
            {
                var path = Path.Combine(folderPath, name);
                if (File.Exists(path))
                {
                    var id = _nfoExtractor.Extract(path)?.ExternalId;
                    if (!string.IsNullOrEmpty(id)) return id;
                }
            }
            // Fallback: first *.nfo found in the folder (excluding sub-folders)
            var any = Directory.EnumerateFiles(folderPath, "*.nfo").FirstOrDefault();
            if (any is not null)
            {
                var id = _nfoExtractor.Extract(any)?.ExternalId;
                if (!string.IsNullOrEmpty(id)) return id;
            }
        }
        catch { /* I/O error — network drive unavailable etc. */ }
        return null;
    }

    // ── Diagnostics DTOs (serialised to DiagnosticsJson) ─────────────────────

    private sealed record EnrichDiagnostics(
        string SearchQuery,
        int CandidatesReturned,
        string FailureReason,
        List<EnrichCandidate> TopCandidates,
        EnrichScannerSignals? ScannerSignals);

    private sealed record EnrichCandidate(
        string? Title,
        int? Year,
        string? ExternalId,
        int TitleScore,
        int YearScore,
        int TotalScore);

    private sealed record EnrichScannerSignals(
        string? FolderPath,
        bool HasNfo,
        bool HasLocalPoster,
        double? ConfidenceScore);

    private static void MergeMetadata(MediaItem item, string pluginId, MediaMetadata result)
    {
        var existing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            item.MetadataJson ?? "{}") ?? [];

        // Remove any short-ID alias key (e.g. "tmdb") that old code may have written.
        var shortId = pluginId.Contains('.') ? pluginId.Split('.').Last() : null;
        if (shortId is not null) existing.Remove(shortId);

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

        if (!string.IsNullOrEmpty(result.PosterUrl))
        {
            // Enrichment has a poster — always apply it (overrides stale or missing values)
            item.PosterUrl = result.PosterUrl;
        }
        else if (item.HierarchyLevel > 0
              && item.PosterUrl?.StartsWith("https://image.tmdb.org/", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Child item (season/episode/track) has a TMDB-hosted poster but the current
            // enrichment returned no poster.  This means the previous poster was from a
            // wrong match (e.g. "Season 02" matched to a random show).  Clear it so the
            // UI shows a placeholder rather than an incorrect image.
            item.PosterUrl = null;
        }
        else if (item.HierarchyLevel == 0 && string.IsNullOrEmpty(item.PosterUrl))
        {
            // Root item with no poster yet — nothing to do (keep null)
        }
        // Root items with existing posters are left unchanged when enrichment has no poster.
        // They may have a file-scanner local/NFO poster that should not be wiped.
    }

    // ── EnrichItemCoreAsync ────────────────────────────────────────────────────

    private const int DefaultConfidenceThreshold = 50;

    private async Task EnrichItemCoreAsync(
        ChronicleDbContext db,
        IMetadataProvider provider,
        string pluginId,
        MediaItem item,
        EnrichmentOptions options,
        CancellationToken ct)
    {
        // 1. Load or create enrichment row
        var row = await db.MediaEnrichments
            .FirstOrDefaultAsync(e => e.MediaItemId == item.Id && e.PluginId == pluginId, ct);
        if (row is null)
        {
            row = new MediaItemEnrichment
                { MediaItemId = item.Id, PluginId = pluginId, MaxRetries = 3 };
            db.MediaEnrichments.Add(row);
        }

        // 2. FillGaps skip
        if (options.Mode == EnrichmentMode.FillGaps
            && row.Status == EnrichmentStatus.Completed
            && options.IdOverride is null)
        {
            if (options.Cascade)
                await CascadeToChildrenAsync(db, provider, pluginId, item, options, ct);
            return;
        }

        row.LastAttemptedAt = DateTime.UtcNow;
        MediaMetadata? result   = null;
        string?        resolvedId = null;

        try
        {
            // 3a. IdOverride
            if (options.IdOverride is not null)
            {
                resolvedId = options.IdOverride.Trim();
            }
            // 3b. Stored ID — validate hierarchy level and parent consistency
            else if (!string.IsNullOrEmpty(row.ExternalId) && IsIdValidForLevel(row.ExternalId, item))
            {
                // For hierarchical items, verify the stored ID's show portion matches the parent's
                // show ID. This catches cases where a child was enriched against the wrong show
                // (e.g. episodes matched to tv:157239 while the parent season is tv:243129/season:1).
                var storedIdConsistent = true;
                if (item.HierarchyLevel > 0 && item.ParentId is not null)
                {
                    var parentRow = await db.MediaEnrichments
                        .FirstOrDefaultAsync(e => e.MediaItemId == item.ParentId && e.PluginId == pluginId, ct);
                    if (parentRow?.ExternalId is not null)
                    {
                        var storedBase  = row.ExternalId.Split('/')[0]; // e.g. "tv:157239"
                        var parentBase  = parentRow.ExternalId.Split('/')[0]; // e.g. "tv:243129"
                        storedIdConsistent = string.Equals(storedBase, parentBase, StringComparison.OrdinalIgnoreCase);
                        if (!storedIdConsistent)
                            logger.LogInformation(
                                "Stored ExternalId {StoredId} for item {ItemId} is inconsistent with parent ({ParentId}); re-deriving",
                                row.ExternalId, item.Id, parentRow.ExternalId);
                    }
                }

                if (storedIdConsistent)
                    resolvedId = row.ExternalId;
            }
            // 3c. Parent-derived ID (also runs when 3b discards an inconsistent stored ID)
            if (resolvedId is null && item.ParentId is not null)
            {
                resolvedId = await TryDeriveFromParentAsync(db, pluginId, item, ct);
            }

            // Fetch if we have a resolved ID from 3a/3b/3c
            if (resolvedId is not null)
            {
                result = await provider.GetByIdAsync(resolvedId, ct);
                if (result is null) resolvedId = null; // provider returned nothing — fall through to search
            }

            // 3d. Search
            // Always runs for root items (no parent).
            // Also runs for HierarchyLevel-1 items (albums/seasons) when derivation returned null —
            // music albums have no derivable ID from the artist MBID, so search is the only path.
            // TV seasons always get a derived ID (show MBID + season number), so derivation
            // succeeds there and this branch is skipped.
            // Tracks/episodes (HierarchyLevel 2) are never searched; they rely on derivation only.
            if (result is null && (item.ParentId is null || (resolvedId is null && item.HierarchyLevel == 1)))
            {
                var childCount = await db.MediaItems.CountAsync(m => m.ParentId == item.Id, ct);
                var filenameStem = ExtractFilenameStem(item);
                var ctx = new Chronicle.Plugins.Models.MediaSearchContext(
                    Name:           NormalizeSearchName(item.Name),
                    Year:           ValidateYear(item.Year),
                    ParentName:     item.Parent?.Name,
                    ChildCount:     childCount > 0 ? childCount : null,
                    HierarchyLevel: item.HierarchyLevel,
                    FilenameStem:   filenameStem,
                    AltTitles:      BuildAltTitles(
                                        item.Name,
                                        filenameStem,
                                        null));

                var candidates = await provider.SearchAsync(ctx, ct);
                var best = candidates.OrderByDescending(c => c.Score).FirstOrDefault();

                StoreDiagnosticsJson(row, ctx.Name, candidates);

                if (best is null || best.Score < DefaultConfidenceThreshold
                    || string.IsNullOrEmpty(best.Metadata.ExternalId))
                {
                    row.Status = EnrichmentStatus.NotFound;
                    await db.SaveChangesAsync(ct);
                    return;
                }

                resolvedId = best.Metadata.ExternalId;
                result = await provider.GetByIdAsync(resolvedId, ct);
                result ??= best.Metadata;
            }

            if (result is null || string.IsNullOrEmpty(resolvedId))
            {
                row.Status = EnrichmentStatus.NotFound;
                await db.SaveChangesAsync(ct);
                return;
            }

            // 6. Merge losslessly
            MergeProviderResult(item, pluginId, result);

            // 7. Update row
            row.ExternalId      = resolvedId;
            row.Status          = EnrichmentStatus.Completed;
            row.LastCompletedAt = DateTime.UtcNow;
            row.ErrorMessage    = null;
            row.RetryCount      = 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var isExpected = ex is HttpRequestException or TaskCanceledException
                                                         or TimeoutException
                                                         or OperationCanceledException;
            if (isExpected)
                logger.LogWarning(
                    "Enrichment transient error for item {ItemId} plugin {PluginId}: {Type}: {Msg}",
                    item.Id, pluginId, ex.GetType().Name, ex.Message);
            else
                logger.LogWarning(ex, "Enrichment failed for item {ItemId} plugin {PluginId}",
                    item.Id, pluginId);

            row.RetryCount++;
            row.ErrorMessage = ex.Message;
            row.Status = row.RetryCount >= row.MaxRetries
                ? EnrichmentStatus.Exhausted
                : EnrichmentStatus.Failed;
        }

        await db.SaveChangesAsync(ct);

        // 8. Cascade to children
        if (options.Cascade)
            await CascadeToChildrenAsync(db, provider, pluginId, item, options, ct);
    }

    private static bool IsIdValidForLevel(string externalId, MediaItem item)
    {
        var sep = externalId.IndexOf(':');
        if (sep <= 0) return true;
        var prefix = externalId[..sep];
        if (item.ParentId is null)
            return prefix is "artist" or "movie" or "tv";
        // Child-level: reject bare show-level IDs on season/episode rows
        if (prefix == "tv" && !externalId.Contains('/'))
            return false;
        return true;
    }

    private async Task<string?> TryDeriveFromParentAsync(
        ChronicleDbContext db, string pluginId, MediaItem item, CancellationToken ct)
    {
        var parentRow = await db.MediaEnrichments
            .FirstOrDefaultAsync(e => e.MediaItemId == item.ParentId && e.PluginId == pluginId, ct);

        if (parentRow?.ExternalId is null || parentRow.Status != EnrichmentStatus.Completed)
            return null;

        if (item.Number is null) return null;

        var parentId = parentRow.ExternalId;

        if (item.HierarchyLevel == 1)
            return $"{parentId}/season:{item.Number}";

        if (item.HierarchyLevel == 2)
        {
            var grandparentRow = await db.MediaEnrichments
                .Include(e => e.MediaItem)
                .FirstOrDefaultAsync(e => e.MediaItem!.Id == item.Parent!.ParentId
                                       && e.PluginId == pluginId, ct);
            if (grandparentRow?.ExternalId is null) return null;
            var seasonNum = item.Parent?.Number;
            if (seasonNum is null) return null;
            return $"{grandparentRow.ExternalId}/season:{seasonNum}/episode:{item.Number}";
        }

        return null;
    }

    private async Task CascadeToChildrenAsync(
        ChronicleDbContext db, IMetadataProvider provider, string pluginId,
        MediaItem parent, EnrichmentOptions options, CancellationToken ct)
    {
        var children = await db.MediaItems
            .Include(m => m.MediaType)
            .Include(m => m.Parent)
            .Where(m => m.ParentId == parent.Id)
            .OrderBy(m => m.Number).ThenBy(m => m.Name)
            .ToListAsync(ct);

        foreach (var child in children)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                await EnrichItemCoreAsync(db, provider, pluginId, child,
                    options with { IdOverride = null }, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cascade: failed enriching child {Id} '{Name}'",
                    child.Id, child.Name);
            }
        }
    }

    private static string NormalizeSearchName(string name) =>
        System.Text.RegularExpressions.Regex
            .Replace(name, @"[:\-,]", " ")   // keep apostrophes and dots — dots are meaningful in names like "shutdown.exe"
            .Replace("  ", " ")
            .Trim()
            .ToLowerInvariant();

    private static void StoreDiagnosticsJson(
        MediaItemEnrichment row, string query,
        IReadOnlyList<Chronicle.Plugins.Models.ScoredCandidate> candidates)
    {
        // Use camelCase property names in anonymous type literals so the JSON matches
        // the field names the frontend's EnrichmentDiagnostics / EnrichmentCandidate
        // TypeScript interfaces expect (searchQuery, totalScore, scoreReason, etc.).
        row.DiagnosticsJson = JsonSerializer.Serialize(new
        {
            searchQuery        = query,
            threshold          = DefaultConfidenceThreshold,
            candidatesReturned = candidates.Count,
            topCandidates      = candidates.OrderByDescending(c => c.Score).Take(5)
                .Select(c => new
                {
                    title       = c.Metadata.Title,
                    year        = c.Metadata.Year,
                    externalId  = c.Metadata.ExternalId,
                    totalScore  = c.Score,
                    scoreReason = c.ScoreReason,
                })
                .ToList(),
        });
    }

    private static void MergeProviderResult(MediaItem item, string pluginId, MediaMetadata meta)
    {
        var existing = JsonSerializer
            .Deserialize<Dictionary<string, JsonElement>>(item.MetadataJson ?? "{}") ?? [];

        var shortId = pluginId.Contains('.') ? pluginId.Split('.').Last() : null;
        if (shortId is not null) existing.Remove(shortId);

        var savedResults = meta.Results;
        var savedTotal   = meta.TotalResults;
        meta.Results      = null;
        meta.TotalResults = 0;
        try   { existing[pluginId] = JsonSerializer.SerializeToElement(meta); }
        finally { meta.Results = savedResults; meta.TotalResults = savedTotal; }
        item.MetadataJson = JsonSerializer.Serialize(existing);

        if (!string.IsNullOrWhiteSpace(meta.PosterUrl))  item.PosterUrl      = meta.PosterUrl;
        if (!string.IsNullOrWhiteSpace(meta.Overview))   item.Overview       = meta.Overview;
        if (meta.RuntimeMinutes.HasValue)                 item.RuntimeMinutes = meta.RuntimeMinutes;

        if (item.HierarchyLevel == 0)
        {
            if (!string.IsNullOrWhiteSpace(meta.Title)) item.Name = meta.Title;
            if (meta.Year.HasValue)                      item.Year = meta.Year;
        }

        item.UpdatedAt = DateTime.UtcNow;
    }
}
