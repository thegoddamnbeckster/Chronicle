using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Chronicle.Services;

/// <summary>
/// Service that refreshes metadata for library items using active metadata plugins.
/// Invoked on-demand via the per-plugin background task system (Task 8/9).
/// </summary>
public sealed class MetadataRefreshService : IMetadataRefreshService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _log = Log.ForContext<MetadataRefreshService>();

    public MetadataRefreshService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
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
                // Refresh root item (show/artist/movie) first
                await RefreshItemCoreAsync(db, registry, item, ct);
                await Task.Delay(500, ct);

                // Then cascade to all descendants (seasons → episodes, albums → tracks, etc.)
                await RefreshDescendantsAsync(db, registry, item.Id, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Warning(ex, "MetadataRefreshService: error refreshing item {Id} '{Name}'", item.Id, item.Name);
            }
        }
    }

    /// <summary>
    /// Recursively refreshes all descendants of a root item in breadth-first order
    /// (seasons before episodes, albums before tracks).  Each child is refreshed via
    /// <see cref="RefreshChildFromRootCoreAsync"/> which uses the root's TMDB ID to call
    /// per-season / per-episode APIs where the provider supports it.
    /// </summary>
    private async Task RefreshDescendantsAsync(
        ChronicleDbContext db,
        IPluginRegistry registry,
        int parentId,
        CancellationToken ct)
    {
        var children = await db.MediaItems
            .Include(m => m.MediaType)
            .Include(m => m.ExternalIds)
            .Where(m => m.ParentId == parentId)
            .OrderBy(m => m.Number)
            .ThenBy(m => m.Name)
            .ToListAsync(ct);

        foreach (var child in children)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                await RefreshChildFromRootCoreAsync(db, registry, child, ct);
                // Brief pause to respect TMDB rate limit (40 req/10s)
                await Task.Delay(300, ct);
                // Recurse for grandchildren (e.g. episodes within a season)
                await RefreshDescendantsAsync(db, registry, child.Id, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _log.Warning(ex, "MetadataRefreshService: error refreshing child {Id} '{Name}'", child.Id, child.Name);
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

    public async Task<MediaItem> RefreshItemForPluginAsync(
        int mediaItemId,
        string pluginId,
        string? input = null,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db       = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<IPluginRegistry>();

        var item = await db.MediaItems
            .Include(m => m.MediaType)
            .Include(m => m.ExternalIds)
            .FirstOrDefaultAsync(m => m.Id == mediaItemId, ct)
            ?? throw new KeyNotFoundException($"Media item {mediaItemId} not found");

        var provider = registry.GetMetadataProvider(pluginId)
            ?? throw new KeyNotFoundException($"Plugin '{pluginId}' not found or not loaded");

        string extId;

        if (input is not null)
        {
            // Fix Match mode: store the user-supplied ID and use it for the fetch
            extId = input.Trim();
            await UpsertExternalIdAsync(db, item.Id, pluginId, extId, ct);
            item.ExternalIds = await db.MediaExternalIds
                .Where(e => e.MediaItemId == item.Id).ToListAsync(ct);
        }
        else
        {
            // Refresh mode: use the existing stored external ID
            var existing = item.ExternalIds
                .FirstOrDefault(e => string.Equals(e.Source, pluginId, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                throw new InvalidOperationException(
                    $"No existing match for plugin '{pluginId}' on item {mediaItemId}. Use Fix Match to set one.");
            extId = existing.ExternalId;
        }

        var meta = await provider.GetByIdAsync(extId, ct);

        if (!string.IsNullOrWhiteSpace(meta.Title))     item.Name           = meta.Title;
        if (meta.Year.HasValue)                          item.Year           = meta.Year;
        if (!string.IsNullOrWhiteSpace(meta.Overview))  item.Overview       = meta.Overview;
        if (!string.IsNullOrWhiteSpace(meta.PosterUrl)) item.PosterUrl      = meta.PosterUrl;
        if (meta.RuntimeMinutes.HasValue)               item.RuntimeMinutes = meta.RuntimeMinutes;

        item.MetadataJson = MergeMetadataJson(item.MetadataJson, pluginId, meta);
        item.UpdatedAt    = DateTime.UtcNow;

        var log = new MediaItemRefreshLog
        {
            MediaItemId  = item.Id,
            ProviderName = provider.Name,
            RefreshedAt  = DateTime.UtcNow,
            Succeeded    = true
        };
        db.MediaItemRefreshLogs.Add(log);
        await db.SaveChangesAsync(ct);

        _log.Information("RefreshItemForPlugin: refreshed '{Name}' (item {Id}) via {Plugin}{Input}",
            item.Name, item.Id, pluginId, input is null ? "" : $" (Fix Match: '{input}')");

        return item;
    }

    public async Task RefreshForPluginAsync(string pluginId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db       = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<IPluginRegistry>();

        var provider = registry.GetMetadataProvider(pluginId);
        if (provider is null)
        {
            _log.Warning("RefreshForPluginAsync: provider {PluginId} not found in registry", pluginId);
            return;
        }

        var itemIds = await db.UserLibraries
            .Select(ul => ul.MediaItemId)
            .Distinct()
            .ToListAsync(ct);

        // Only process root items already matched to this plugin (have its external ID)
        var rootItems = await db.MediaItems
            .Include(m => m.MediaType)
            .Include(m => m.ExternalIds)
            .Where(m => itemIds.Contains(m.Id)
                     && m.HierarchyLevel == 0
                     && m.ExternalIds.Any(e => e.Source == pluginId))
            .ToListAsync(ct);

        _log.Information("RefreshForPluginAsync: {Count} root items matched to plugin {PluginId}",
            rootItems.Count, pluginId);

        foreach (var item in rootItems)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await RefreshItemCoreAsync(db, registry, item, ct);
                await Task.Delay(500, ct);
                await RefreshDescendantsAsync(db, registry, item.Id, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Warning(ex, "RefreshForPluginAsync: error refreshing item {Id}", item.Id);
            }
        }
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
        // ── Child items (Season, Episode, Track, etc.) ─────────────────────────
        // Never independently match child items to TMDB by their generic names
        // (e.g. "Season 01" would match random shows). Walk up to the root item
        // and inherit its TMDB context instead.
        if (item.HierarchyLevel > 0)
        {
            await RefreshChildFromRootCoreAsync(db, registry, item, ct);
            return;
        }

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
                // Resolve external ID for this provider
                var extId = item.ExternalIds
                    .FirstOrDefault(e =>
                        string.Equals(e.Source, provider.PluginId, StringComparison.OrdinalIgnoreCase))
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
                    .Where(e => string.Equals(e.Source, provider.PluginId, StringComparison.OrdinalIgnoreCase))
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

    // ── Child refresh ─────────────────────────────────────────────────────────

    /// <summary>
    /// Refreshes a child item (Season, Episode, Track, etc.) by walking up to the root
    /// and using the root show's TMDB series ID to call per-season / per-episode APIs.
    /// Falls back to inheriting show-level metadata if season/episode numbers cannot be parsed.
    /// </summary>
    private async Task RefreshChildFromRootCoreAsync(
        ChronicleDbContext db,
        IPluginRegistry registry,
        MediaItem item,
        CancellationToken ct)
    {
        var provider = registry.GetMetadataProviders().FirstOrDefault();
        var log = new MediaItemRefreshLog
        {
            MediaItemId  = item.Id,
            ProviderName = provider?.Name ?? string.Empty,
            RefreshedAt  = DateTime.UtcNow,
            Succeeded    = false
        };

        try
        {
            // Clear any stale provider IDs that old code may have written directly onto a child item
            // (but only format-agnostic ones — structured IDs like "tv:1:s1" or "tv:1:s1:e2"
            // are intentionally stored here and should not be wiped).
            var providerPluginId = provider?.PluginId ?? string.Empty;
            var staleIds = item.ExternalIds
                .Where(e => string.Equals(e.Source, providerPluginId, StringComparison.OrdinalIgnoreCase)
                         && e.ExternalId != "__suppress__"
                         && !e.ExternalId.Contains(":s"))   // keep structured season/episode IDs
                .ToList();
            if (staleIds.Count > 0)
            {
                db.MediaExternalIds.RemoveRange(staleIds);
                foreach (var s in staleIds) item.ExternalIds.Remove(s);
                _log.Information("RefreshChild: cleared {Count} stale provider ID(s) from child {Id}", staleIds.Count, item.Id);
            }

            // Walk parent chain to find root item and direct parent
            var currentId = item.ParentId;
            MediaItem? root = null;
            MediaItem? directParent = null;
            while (currentId != null)
            {
                var candidate = await db.MediaItems
                    .Include(m => m.ExternalIds)
                    .FirstOrDefaultAsync(m => m.Id == currentId, ct);
                if (candidate is null) break;
                if (directParent is null) directParent = candidate;
                if (candidate.ParentId is null) { root = candidate; break; }
                currentId = candidate.ParentId;
            }

            if (root is null)
            {
                log.ErrorMessage = "No root item found in parent chain";
                _log.Warning("RefreshChild: no root found for child {Id}", item.Id);
                // fall through to save the failed log entry
            }
            else
            {
                var rootExtId = root.ExternalIds
                    .FirstOrDefault(e => string.Equals(e.Source, providerPluginId, StringComparison.OrdinalIgnoreCase)
                                     && e.ExternalId != "__suppress__")
                    ?.ExternalId;

                if (rootExtId is null)
                {
                    log.ErrorMessage = "Parent show has no TMDB match — refresh the parent first";
                    _log.Information("RefreshChild: root {RootId} has no TMDB ID, skipping child {Id}", root.Id, item.Id);
                    // fall through to save the failed log entry
                }
                else if (provider is null)
                {
                    log.ErrorMessage = "No metadata provider available";
                    // fall through to save the failed log entry
                }
                else
                {
                    await RefreshChildWithProviderAsync(db, provider, item, root, directParent, rootExtId, log, ct);
                }
            }
        }
        catch (Exception ex)
        {
            log.ErrorMessage = ex.Message;
            _log.Warning(ex, "RefreshChild failed for item {Id}", item.Id);
        }
        finally
        {
            db.MediaItemRefreshLogs.Add(log);
        }

        var stillExists = await db.MediaItems.AnyAsync(m => m.Id == item.Id, ct);
        if (!stillExists) { db.ChangeTracker.Clear(); return; }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Inner worker: given a confirmed provider and root external ID, fetches season/episode
    /// data or falls back to inheriting the show's metadata.  Does not call SaveChangesAsync.
    /// </summary>
    private async Task RefreshChildWithProviderAsync(
        ChronicleDbContext db,
        Chronicle.Plugins.IMetadataProvider provider,
        MediaItem item,
        MediaItem root,
        MediaItem? directParent,
        string rootExtId,
        MediaItemRefreshLog log,
        CancellationToken ct)
    {
        // Parse TMDB series ID from root external ID (e.g. "tv:1267" → 1267)
        if (!TryParseSeriesId(rootExtId, out var seriesId))
        {
            _log.Warning("RefreshChild: cannot parse series ID from '{ExtId}' for child {Id} — falling back to show-level data", rootExtId, item.Id);
            await ApplyShowInheritanceAsync(db, provider, item, root, rootExtId, log, ct);
            return;
        }

        var tvProvider = provider as ITvDetailProvider;

        // ── Season (HierarchyLevel == 1) ──────────────────────────────────────
        if (item.HierarchyLevel == 1)
        {
            if (tvProvider is null)
            {
                _log.Information("RefreshChild: provider {P} does not implement ITvDetailProvider; inheriting show data for season {Id}", provider.Name, item.Id);
                await ApplyShowInheritanceAsync(db, provider, item, root, rootExtId, log, ct);
                return;
            }

            if (!TryParseSeasonNumber(item.Name, item.Number, out var seasonNumber))
            {
                _log.Warning("RefreshChild: cannot parse season number from '{Name}' (item {Id}) — falling back to show data", item.Name, item.Id);
                await ApplyShowInheritanceAsync(db, provider, item, root, rootExtId, log, ct);
                return;
            }

            // Brief delay to respect TMDB's 40 req/10s rate limit
            await Task.Delay(250, ct);

            var season = await tvProvider.GetTvSeasonAsync(seriesId, seasonNumber, ct);
            if (season is null)
            {
                _log.Information("RefreshChild: TMDB returned no data for series {SId} season {SN}; inheriting show data for item {Id}", seriesId, seasonNumber, item.Id);
                await ApplyShowInheritanceAsync(db, provider, item, root, rootExtId, log, ct);
                return;
            }

            // Apply season-specific fields
            if (!string.IsNullOrWhiteSpace(season.Overview)) item.Overview = season.Overview;
            if (season.AirDate?.Length >= 4 && int.TryParse(season.AirDate[..4], out var sy)) item.Year = sy;

            // Poster: use season-specific poster path to build full URL
            if (!string.IsNullOrEmpty(season.PosterPath))
                item.PosterUrl = $"https://image.tmdb.org/t/p/w500{season.PosterPath}";

            item.MetadataJson = MergeSeasonMetadataJson(item.MetadataJson, provider.PluginId, season);
            item.UpdatedAt    = DateTime.UtcNow;

            // Store structured external ID (will be saved by caller's SaveChangesAsync)
            await UpsertExternalIdAsync(db, item.Id, provider.PluginId, $"tv:{seriesId}:s{seasonNumber}", ct);

            log.Succeeded = true;
            _log.Information("RefreshChild: updated season {Id} ({Name}) — series {SId} s{SN}", item.Id, item.Name, seriesId, seasonNumber);
            return;
        }

        // ── Episode (HierarchyLevel == 2) ─────────────────────────────────────
        if (item.HierarchyLevel == 2)
        {
            if (tvProvider is null)
            {
                _log.Information("RefreshChild: provider {P} does not implement ITvDetailProvider; inheriting show data for episode {Id}", provider.Name, item.Id);
                await ApplyShowInheritanceAsync(db, provider, item, root, rootExtId, log, ct);
                return;
            }

            // The direct parent of an episode is a season
            if (!TryParseSeasonNumber(directParent?.Name, directParent?.Number, out var seasonNumber))
            {
                _log.Warning("RefreshChild: cannot parse season number from parent '{Name}' for episode {Id} — falling back", directParent?.Name, item.Id);
                await ApplyShowInheritanceAsync(db, provider, item, root, rootExtId, log, ct);
                return;
            }

            int? episodeNumber = await ResolveEpisodeNumberAsync(db, item, ct);
            if (episodeNumber is null)
            {
                _log.Warning("RefreshChild: cannot determine episode number for item {Id} '{Name}' — falling back", item.Id, item.Name);
                await ApplyShowInheritanceAsync(db, provider, item, root, rootExtId, log, ct);
                return;
            }

            // Brief delay to respect TMDB rate limit
            await Task.Delay(250, ct);

            var episode = await tvProvider.GetTvEpisodeAsync(seriesId, seasonNumber, episodeNumber.Value, ct);
            if (episode is null)
            {
                _log.Information("RefreshChild: TMDB returned no data for series {SId} s{SN}e{EN}; inheriting show data for item {Id}", seriesId, seasonNumber, episodeNumber, item.Id);
                await ApplyShowInheritanceAsync(db, provider, item, root, rootExtId, log, ct);
                return;
            }

            // Update name only if the current name is a generic placeholder (e.g. "Episode 01", "E01")
            if (!string.IsNullOrWhiteSpace(episode.Name) && IsGenericEpisodeName(item.Name))
                item.Name = episode.Name;

            if (!string.IsNullOrWhiteSpace(episode.Overview)) item.Overview = episode.Overview;
            if (episode.AirDate?.Length >= 4 && int.TryParse(episode.AirDate[..4], out var ey)) item.Year = ey;
            if (episode.RuntimeMinutes.HasValue) item.RuntimeMinutes = episode.RuntimeMinutes;

            item.MetadataJson = MergeEpisodeMetadataJson(item.MetadataJson, provider.PluginId, episode);
            item.UpdatedAt    = DateTime.UtcNow;

            // Store structured external ID (will be saved by caller's SaveChangesAsync)
            await UpsertExternalIdAsync(db, item.Id, provider.PluginId, $"tv:{seriesId}:s{seasonNumber}:e{episodeNumber}", ct);

            log.Succeeded = true;
            _log.Information("RefreshChild: updated episode {Id} ({Name}) — series {SId} s{SN}e{EN}", item.Id, item.Name, seriesId, seasonNumber, episodeNumber);
            return;
        }

        // ── Deeper levels or non-TV children: inherit show data ───────────────
        await ApplyShowInheritanceAsync(db, provider, item, root, rootExtId, log, ct);
    }

    /// <summary>
    /// Applies show-level metadata to a child item as a fallback.
    /// Inherits poster and MetadataJson from the root show.
    /// Does NOT call SaveChangesAsync — callers are responsible for saving.
    /// </summary>
    private async Task ApplyShowInheritanceAsync(
        ChronicleDbContext db,
        Chronicle.Plugins.IMetadataProvider provider,
        MediaItem item,
        MediaItem root,
        string rootExtId,
        MediaItemRefreshLog log,
        CancellationToken ct)
    {
        var meta = await provider.GetByIdAsync(rootExtId, ct);

        // Inherit poster from parent show if child has none
        if (string.IsNullOrEmpty(item.PosterUrl) && !string.IsNullOrEmpty(meta.PosterUrl))
            item.PosterUrl = meta.PosterUrl;

        // Merge parent show's rich metadata (genres, cast, rating) into child's MetadataJson
        // without touching item.Name, item.Year, or item.Overview — those belong to the child.
        item.MetadataJson = MergeMetadataJson(item.MetadataJson, provider.PluginId, meta);
        item.UpdatedAt    = DateTime.UtcNow;
        log.Succeeded     = true;

        _log.Information("RefreshChild: inherited show data for {Id} ({Name}) from root {RootId} ({ExtId})",
            item.Id, item.Name, root.Id, rootExtId);

        // Persist immediately so that the item.UpdatedAt and MetadataJson changes are durable
        // even if the outer SaveChangesAsync in RefreshChildFromRootCoreAsync hasn't run yet.
        // (The log entry is added by the caller's finally block; we save it there.)
        await db.SaveChangesAsync(ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
        var root = ParseExistingMetaJson(existingJson);

        // Use the full plugin ID as the key (e.g. "chronicle.plugin.tmdb")
        root[pluginId] = new
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

    // ── Season/episode number parsing ─────────────────────────────────────────

    // Matches "Season 01", "Season 1", "Season 0", "season 02" etc.
    private static readonly Regex _seasonRegex =
        new(@"\bSeason\s*0*(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Matches leading episode numbers: "E01", "E1", "01", "1", "01 - Title", etc.
    private static readonly Regex _episodeLeadingRegex =
        new(@"^[Ee]?0*(\d+)\b", RegexOptions.Compiled);

    private static bool TryParseSeriesId(string rootExtId, out int seriesId)
    {
        // Format: "tv:1267"
        seriesId = 0;
        var parts = rootExtId.Split(':');
        return parts.Length >= 2 && int.TryParse(parts[1], out seriesId);
    }

    private static bool TryParseSeasonNumber(string? name, int? numberField, out int seasonNumber)
    {
        seasonNumber = 0;

        // 1. Try the Number field on the item (most reliable when set by scanner)
        if (numberField.HasValue)
        {
            seasonNumber = numberField.Value;
            return true;
        }

        // 2. Parse from name using regex
        if (name is not null)
        {
            var m = _seasonRegex.Match(name);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var parsed))
            {
                seasonNumber = parsed;
                return true;
            }
        }

        return false;
    }

    private static async Task<int?> ResolveEpisodeNumberAsync(
        ChronicleDbContext db, MediaItem item, CancellationToken ct)
    {
        // 1. Use the Number field if set
        if (item.Number.HasValue) return item.Number.Value;

        // 2. Try to parse from name
        var m = _episodeLeadingRegex.Match(item.Name.Trim());
        if (m.Success && int.TryParse(m.Groups[1].Value, out var parsed))
            return parsed;

        // 3. Fall back to position among siblings (sort by Name, 1-based index)
        if (item.ParentId is null) return null;

        var siblings = await db.MediaItems
            .Where(i => i.ParentId == item.ParentId)
            .OrderBy(i => i.Name)
            .Select(i => i.Id)
            .ToListAsync(ct);

        var idx = siblings.IndexOf(item.Id);
        if (idx >= 0) return idx + 1;

        return null;
    }

    /// <summary>
    /// Returns <c>true</c> if the episode name looks like a generic placeholder
    /// (e.g. "Episode 01", "E01", "01", "1") that should be replaced with the
    /// actual TMDB title.
    /// </summary>
    private static readonly Regex _genericEpisodeNameRegex =
        new(@"^(Episode\s*\d+|[Ee]\d+|\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsGenericEpisodeName(string name) =>
        _genericEpisodeNameRegex.IsMatch(name.Trim());

    // ── Season/episode MetadataJson merge ─────────────────────────────────────

    private static string MergeSeasonMetadataJson(string? existingJson, string pluginId, TvSeasonDetail season)
    {
        var root = ParseExistingMetaJson(existingJson);

        root[pluginId] = new
        {
            seasonId     = season.SeasonId,
            posterPath   = season.PosterPath,
            airDate      = season.AirDate,
            episodeCount = season.EpisodeCount,
            overview     = season.Overview,
            voteAverage  = season.VoteAverage
        };

        return JsonSerializer.Serialize(root);
    }

    private static string MergeEpisodeMetadataJson(string? existingJson, string pluginId, TvEpisodeDetail episode)
    {
        var root = ParseExistingMetaJson(existingJson);

        root[pluginId] = new
        {
            seasonNumber  = episode.SeasonNumber,
            episodeNumber = episode.EpisodeNumber,
            stillPath     = episode.StillPath,
            airDate       = episode.AirDate,
            overview      = episode.Overview,
            voteAverage   = episode.VoteAverage,
            guestStars    = episode.GuestStars,
            crew          = episode.Crew
        };

        return JsonSerializer.Serialize(root);
    }

    private static Dictionary<string, object?> ParseExistingMetaJson(string? json)
    {
        var root = new Dictionary<string, object?>();
        if (!string.IsNullOrEmpty(json))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (parsed is not null)
                    foreach (var kv in parsed)
                        root[kv.Key] = kv.Value;
            }
            catch { /* discard unparseable JSON */ }
        }
        return root;
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
