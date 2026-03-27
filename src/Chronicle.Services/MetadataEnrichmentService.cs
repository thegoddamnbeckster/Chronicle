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
        var pluginIds = registry.GetMetadataProviderEntries().Select(e => e.PluginId).ToList();
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

    public async Task<PagedEnrichmentItems> GetItemsAsync(
        string pluginId, string? status, int page, int pageSize, string? search,
        CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        IQueryable<MediaItemEnrichmentStatus> query = db.EnrichmentStatuses
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
        MediaItemEnrichmentStatus row, CancellationToken ct)
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
                    var showEnrichment = await db.EnrichmentStatuses
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
                    var showEnrichment = await db.EnrichmentStatuses
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

                    var searchResults = await provider.SearchAsync(
                        new MediaSearchContext(row.MediaItem.Name, row.MediaItem.Year, HierarchyLevel: 0), ct);

                    // Capture candidates for diagnostics BEFORE GetByIdAsync might overwrite result
                    rawCandidates = searchResults.Take(5).Select(c => c.Metadata).ToList();

                    result = searchResults.OrderByDescending(c => c.Score).FirstOrDefault()?.Metadata;

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
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
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
}
