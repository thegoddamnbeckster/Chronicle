using System.Text.Json;
using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using EnrichmentStatus = Chronicle.Core.Models.EnrichmentStatus;


namespace Chronicle.Services;

public class MovieCollectionService(
    IServiceScopeFactory scopeFactory,
    ILogger<MovieCollectionService> logger) : IMovieCollectionService
{
    private const int BulkBatchSize = 200;
    public async Task EnsureCollectionParentAsync(
        ChronicleDbContext db,
        MediaItem movieItem,
        string? pluginId = null,
        CancellationToken ct = default)
    {
        // Lazily load MediaType if the caller didn't eagerly include it
        if (movieItem.MediaType is null)
            await db.Entry(movieItem).Reference(m => m.MediaType).LoadAsync(ct);

        // Only group "movies" type — not fanedits, not tv, not anything else
        if (movieItem.MediaType is null ||
            !string.Equals(movieItem.MediaType.Name, "movies", StringComparison.OrdinalIgnoreCase))
            return;

        var collectionData = ExtractCollectionData(movieItem.MetadataJson);
        if (collectionData is null)
        {
            // Movie has no collection data — ensure it is at root level
            if (movieItem.ParentId is not null)
            {
                movieItem.ParentId = null;
                movieItem.HierarchyLevel = 0;
                movieItem.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            return;
        }

        // External ID: "collection:{id}" scoped to its source plugin
        var externalIdValue = $"collection:{collectionData.Id}";
        // Pass movieItem so that a brand-new collection's first child is re-parented
        // inside the same transaction, eliminating the window where the collection exists
        // without children and both items appear as standalone entries in the flat library view.
        var (collection, alreadyReparented) = await FindOrCreateCollectionAsync(
            db, movieItem.MediaTypeId, collectionData, externalIdValue, movieItem, ct);

        // Collection containers are NOT enriched by plugins — their name, poster, and external ID
        // are fully populated from the movie's belongsToCollection data at creation time.
        // Seeding a Pending row would cause plugins to attempt enrichment and produce spurious
        // "No match found" results on the collection detail page.

        // Re-parent the movie if needed (skipped when FindOrCreateCollectionAsync already did it
        // as part of a new-collection transaction)
        if (!alreadyReparented && movieItem.ParentId != collection.Id)
        {
            var oldParentId = movieItem.ParentId;

            movieItem.ParentId = collection.Id;
            movieItem.HierarchyLevel = 1;
            movieItem.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Movie {ItemId} \"{Name}\" re-parented under collection {CollectionId} \"{CollectionName}\" (source={Source})",
                movieItem.Id, movieItem.Name, collection.Id, collection.Name, collectionData.Source);

            // If the movie was previously under a different collection and that
            // collection is now childless, remove the orphaned container.
            if (oldParentId.HasValue && oldParentId.Value != collection.Id)
                await RemoveOrphanedCollectionAsync(db, oldParentId.Value, movieItem.MediaTypeId, ct);
        }
        else if (alreadyReparented)
        {
            logger.LogInformation(
                "Movie {ItemId} \"{Name}\" re-parented under new collection {CollectionId} \"{CollectionName}\" (source={Source})",
                movieItem.Id, movieItem.Name, collection.Id, collection.Name, collectionData.Source);
        }
    }

    /// <summary>Deletes all IsStub=true children of a collection container.</summary>
    private async Task PurgeStubsAsync(ChronicleDbContext db, int collectionId, CancellationToken ct)
    {
        var stubs = await db.MediaItems
            .Include(m => m.ExternalIds)
            .Where(m => m.ParentId == collectionId && m.IsStub)
            .ToListAsync(ct);

        if (stubs.Count == 0) return;

        foreach (var stub in stubs)
            db.MediaExternalIds.RemoveRange(stub.ExternalIds);
        db.MediaItems.RemoveRange(stubs);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Purged {Count} wrong stub(s) from collection {CollectionId}", stubs.Count, collectionId);
    }

    private async Task RemoveOrphanedCollectionAsync(
        ChronicleDbContext db, int candidateId, int mediaTypeId, CancellationToken ct)
    {
        var candidate = await db.MediaItems
            .FirstOrDefaultAsync(m => m.Id == candidateId &&
                                      m.MediaTypeId == mediaTypeId &&
                                      m.HierarchyLevel == 0, ct);
        if (candidate is null) return;

        var hasChildren = await db.MediaItems.AnyAsync(m => m.ParentId == candidateId, ct);
        if (hasChildren) return;

        // Remove associated external IDs then the item itself
        var extIds = await db.MediaExternalIds
            .Where(e => e.MediaItemId == candidateId)
            .ToListAsync(ct);
        db.MediaExternalIds.RemoveRange(extIds);
        db.MediaItems.Remove(candidate);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Removed orphaned collection MediaItem {Id} \"{Name}\" — no remaining children",
            candidate.Id, candidate.Name);
    }

    /// <summary>
    /// Extracts collection data from <paramref name="metadataJson"/>.
    ///
    /// Strategy (in priority order):
    /// <list type="number">
    ///   <item><description>
    ///     Read <c>_resolved.belongsToCollection</c> — this is written by
    ///     <c>MetadataResolutionService</c> according to the Metadata Assignment configuration,
    ///     so whatever plugin the operator configured as highest priority wins automatically.
    ///     The source is determined by finding which plugin blob contains the same collection ID.
    ///   </description></item>
    ///   <item><description>
    ///     Fall back to scanning every plugin blob in order — used for items enriched before
    ///     the resolution step wrote a <c>_resolved</c> entry, or when no assignment config exists.
    ///   </description></item>
    /// </list>
    ///
    /// Expected shape inside any plugin blob:
    /// <code>
    /// {
    ///   "belongsToCollection": {
    ///     "id":         "748",          // string or number
    ///     "name":       "Some Collection",
    ///     "posterPath": "https://..."   // optional full URL
    ///   }
    /// }
    /// </code>
    /// </summary>
    internal static CollectionData? ExtractCollectionData(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return null;

            // ── Pass 1: use the pre-resolved blob written by MetadataResolutionService ──
            // When the operator has configured Metadata Assignment for the "collection" field,
            // _resolved.belongsToCollection already reflects the priority-ordered winner.
            if (root.TryGetProperty("_resolved", out var resolvedEl) &&
                resolvedEl.ValueKind == JsonValueKind.Object &&
                resolvedEl.TryGetProperty("belongsToCollection", out var resolvedColl) &&
                resolvedColl.ValueKind == JsonValueKind.Object)
            {
                var parsed = ParseCollectionElementNoSource(resolvedColl);
                if (parsed is not null)
                {
                    // Identify which plugin blob this collection came from (for Source labelling).
                    // We match by collection ID across all plugin blobs.
                    var sourcePlugin = FindSourcePlugin(root, parsed.Value.Id);
                    if (sourcePlugin is not null)
                        return new CollectionData(parsed.Value.Id, parsed.Value.Name, parsed.Value.PosterUrl, sourcePlugin);
                    // Source couldn't be identified (e.g. the plugin that wrote _resolved is no
                    // longer installed). Fall through to Pass 2 so we use the raw plugin blob
                    // directly — that preserves a correct Source and avoids creating a duplicate
                    // collection container with source = "unknown".
                }
            }

            // ── Pass 2: scan every plugin blob in document order (no assignment config) ──
            foreach (var pluginProp in root.EnumerateObject())
            {
                if (pluginProp.Name.StartsWith('_')) continue; // skip _resolved, _fileScanner, etc.

                var pluginEl = pluginProp.Value;
                if (pluginEl.ValueKind != JsonValueKind.Object) continue;

                // belongsToCollection may be at the top level OR nested inside extendedData
                JsonElement collEl;
                if (pluginEl.TryGetProperty("belongsToCollection", out var directCollEl) &&
                    directCollEl.ValueKind == JsonValueKind.Object)
                {
                    collEl = directCollEl;
                }
                else if (pluginEl.TryGetProperty("extendedData", out var extEl) &&
                         extEl.ValueKind == JsonValueKind.Object &&
                         extEl.TryGetProperty("belongsToCollection", out var nestedCollEl) &&
                         nestedCollEl.ValueKind == JsonValueKind.Object)
                {
                    collEl = nestedCollEl;
                }
                else
                    continue;

                var source = PluginIdHelper.ToSource(pluginProp.Name);
                var data = ParseCollectionElement(collEl, source);
                if (data is not null) return data;
            }

            return null;
        }
        catch { return null; }
    }

    /// <param name="source">
    ///   Short source name (e.g. "tmdb"). Must not be null — use the overload that
    ///   omits source when the caller determines the source separately (Pass 1 path).
    /// </param>
    private static CollectionData? ParseCollectionElement(JsonElement collEl, string source)
    {
        if (!collEl.TryGetProperty("name", out var nameEl)) return null;
        var name = nameEl.GetString();
        if (string.IsNullOrEmpty(name)) return null;

        string? idStr = null;
        if (collEl.TryGetProperty("id", out var idEl))
        {
            idStr = idEl.ValueKind == JsonValueKind.Number
                ? idEl.GetRawText().Trim()  // e.g. "748" — Trim() guards against whitespace in raw token
                : idEl.GetString();
        }
        if (string.IsNullOrEmpty(idStr)) return null;

        string? posterUrl = collEl.TryGetProperty("posterPath", out var pEl)
            ? pEl.GetString() : null;

        return new CollectionData(idStr, name, posterUrl, source);
    }

    /// <summary>
    /// Parses only the ID and name from a collection element (no source).
    /// Used by Pass 1 where the source is resolved separately via <see cref="FindSourcePlugin"/>.
    /// </summary>
    private static (string Id, string Name, string? PosterUrl)? ParseCollectionElementNoSource(JsonElement collEl)
    {
        if (!collEl.TryGetProperty("name", out var nameEl)) return null;
        var name = nameEl.GetString();
        if (string.IsNullOrEmpty(name)) return null;

        string? idStr = null;
        if (collEl.TryGetProperty("id", out var idEl))
        {
            idStr = idEl.ValueKind == JsonValueKind.Number
                ? idEl.GetRawText().Trim()
                : idEl.GetString();
        }
        if (string.IsNullOrEmpty(idStr)) return null;

        string? posterUrl = collEl.TryGetProperty("posterPath", out var pEl)
            ? pEl.GetString() : null;

        return (idStr, name, posterUrl);
    }

    /// <summary>
    /// Finds which plugin blob contributed the collection with the given ID.
    /// Returns the short source name (e.g. "tmdb") or <c>null</c> if not found.
    /// </summary>
    private static string? FindSourcePlugin(JsonElement root, string collectionId)
    {
        foreach (var pluginProp in root.EnumerateObject())
        {
            if (pluginProp.Name.StartsWith('_')) continue;
            var pluginEl = pluginProp.Value;
            if (pluginEl.ValueKind != JsonValueKind.Object) continue;
            JsonElement collEl;
            if (pluginEl.TryGetProperty("belongsToCollection", out var directEl) &&
                directEl.ValueKind == JsonValueKind.Object)
                collEl = directEl;
            else if (pluginEl.TryGetProperty("extendedData", out var extEl2) &&
                     extEl2.ValueKind == JsonValueKind.Object &&
                     extEl2.TryGetProperty("belongsToCollection", out var nestedEl) &&
                     nestedEl.ValueKind == JsonValueKind.Object)
                collEl = nestedEl;
            else continue;
            if (!collEl.TryGetProperty("id", out var idEl)) continue;
            var idStr = idEl.ValueKind == JsonValueKind.Number
                ? idEl.GetRawText().Trim()
                : idEl.GetString();
            if (string.Equals(idStr, collectionId, StringComparison.Ordinal))
                return PluginIdHelper.ToSource(pluginProp.Name);
        }
        return null;
    }

    // Returns (collection, wasReparented). wasReparented=true means movieItem.ParentId was
    // already set inside the new-collection transaction — caller must not save again.
    private async Task<(MediaItem Collection, bool WasReparented)> FindOrCreateCollectionAsync(
        ChronicleDbContext db,
        int mediaTypeId,
        CollectionData data,
        string externalIdValue,
        MediaItem movieItem,
        CancellationToken ct)
    {
        // ── Primary lookup: (source, externalId) — unique within a plugin's namespace ──
        // Two-step to avoid EF identity-map returning a null navigation when the
        // MediaExternalId row is already tracked but MediaItem was not eagerly loaded.
        var extIdRow = await db.MediaExternalIds
            .Where(e => e.ExternalId == externalIdValue && e.Source == data.Source)
            .FirstOrDefaultAsync(ct);

        if (extIdRow is not null)
        {
            var existing = await db.MediaItems.FindAsync([extIdRow.MediaItemId], ct);
            if (existing is not null)
            {
                // Update poster if the plugin now returns a better URL
                if (data.PosterUrl is not null && existing.PosterUrl != data.PosterUrl)
                {
                    existing.PosterUrl = data.PosterUrl;
                    existing.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
                return (existing, wasReparented: false);
            }
        }

        // ── Name-based fallback: only match items already acting as collection parents ──
        // Requires at least one existing child to distinguish a collection container
        // from a standalone movie that happens to share the name. This prevents a movie
        // named e.g. "The Avengers" from being treated as the "The Avengers Collection".
        //
        // Concurrent-create note: the enrichment service processes items sequentially
        // per plugin (enforced by a per-plugin SemaphoreSlim in MetadataEnrichmentService).
        // Within a single enrichment batch, two calls for different movies in the same
        // collection will not overlap, so the race window between this check and the
        // create-transaction below is not a practical concern. If the execution model
        // ever becomes parallel, add a unique DB constraint on (MediaTypeId, HierarchyLevel, Name)
        // and handle the resulting DbUpdateException by re-fetching on constraint violation.
        var byName = await db.MediaItems
            .Where(m => m.MediaTypeId == mediaTypeId &&
                        m.HierarchyLevel == 0 &&
                        m.Name == data.Name &&
                        db.MediaItems.Any(child => child.ParentId == m.Id))
            .FirstOrDefaultAsync(ct);

        if (byName is not null)
        {
            // Cross-link this plugin's external ID to the existing collection item
            var alreadyLinked = await db.MediaExternalIds
                .AnyAsync(e => e.MediaItemId == byName.Id && e.Source == data.Source, ct);
            if (!alreadyLinked)
            {
                db.MediaExternalIds.Add(new MediaExternalId
                {
                    MediaItemId = byName.Id,
                    Source      = data.Source,
                    ExternalId  = externalIdValue,
                });
                await db.SaveChangesAsync(ct);
            }
            return (byName, wasReparented: false);
        }

        // ── Race-condition guard: final re-check before insert ───────────────────
        // Concurrent enrichment workers can both pass the lookups above before either
        // commits, resulting in duplicate collection containers. Re-read by ExternalId
        // inside the transaction to catch this.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Re-read inside the transaction — another worker may have committed between our
            // lookup above and the transaction start.
            var raceCheck = await db.MediaExternalIds
                .Where(e => e.ExternalId == externalIdValue && e.Source == data.Source)
                .FirstOrDefaultAsync(ct);
            if (raceCheck is not null)
            {
                var raceItem = await db.MediaItems.FindAsync([raceCheck.MediaItemId], ct);
                if (raceItem is not null)
                {
                    await tx.RollbackAsync(ct);
                    return (raceItem, wasReparented: false);
                }
            }

            // Also re-check by name inside the transaction (catches the case where the
            // container's ExternalId was cleared but the container still exists by name).
            var raceByName = await db.MediaItems
                .Where(m => m.MediaTypeId == mediaTypeId &&
                            m.HierarchyLevel == 0 &&
                            m.Name == data.Name &&
                            db.MediaItems.Any(child => child.ParentId == m.Id))
                .FirstOrDefaultAsync(ct);
            if (raceByName is not null)
            {
                // Cross-link this ExternalId to the existing container and bail out.
                var alreadyLinked = await db.MediaExternalIds
                    .AnyAsync(e => e.MediaItemId == raceByName.Id && e.Source == data.Source, ct);
                if (!alreadyLinked)
                {
                    db.MediaExternalIds.Add(new MediaExternalId
                    {
                        MediaItemId = raceByName.Id,
                        Source      = data.Source,
                        ExternalId  = externalIdValue,
                    });
                    await db.SaveChangesAsync(ct);
                }
                await tx.RollbackAsync(ct);
                return (raceByName, wasReparented: false);
            }

            var now = DateTime.UtcNow;
            var collection = new MediaItem
            {
                MediaTypeId    = mediaTypeId,
                Name           = data.Name,
                HierarchyLevel = 0,
                PosterUrl      = data.PosterUrl,
                CreatedAt      = now,
                UpdatedAt      = now,
            };
            db.MediaItems.Add(collection);
            // First save: get the collection's DB-assigned Id so ExternalId can reference it.
            await db.SaveChangesAsync(ct);

            db.MediaExternalIds.Add(new MediaExternalId
            {
                MediaItemId = collection.Id,
                Source      = data.Source,
                ExternalId  = externalIdValue,
            });

            // Bundle the movie re-parenting into the same commit so the collection is never
            // visible to other readers without its first child (eliminates the race window).
            movieItem.ParentId = collection.Id;
            movieItem.HierarchyLevel = 1;
            movieItem.UpdatedAt = now;

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            logger.LogInformation(
                "Created collection MediaItem {Id} \"{Name}\" (source={Source}, ExternalId={ExternalId})",
                collection.Id, collection.Name, data.Source, externalIdValue);

            return (collection, wasReparented: true);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<bool> EnsureCollectionStubsAsync(
        ChronicleDbContext db,
        MediaItem collection,
        IMetadataProvider provider,
        CancellationToken ct = default,
        IReadOnlyList<(string PluginId, IMetadataProvider Provider)>? allProviders = null)
    {
        // Only use a collection ExternalId that came from this specific provider's source,
        // matching by the last segment of the provider name (e.g. "tmdb").
        var providerSource = PluginIdHelper.ToSource(provider.Name);
        var collectionExtId = collection.ExternalIds
            .FirstOrDefault(e => string.Equals(e.Source, providerSource, StringComparison.OrdinalIgnoreCase)
                              && e.ExternalId.StartsWith("collection:", StringComparison.OrdinalIgnoreCase));

        if (collectionExtId is null)
            return true;

        MediaMetadata collectionMeta;
        try
        {
            collectionMeta = await provider.GetByIdAsync(collectionExtId.ExternalId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to fetch collection parts for {ExternalId} from provider {Provider} — removing bad ExternalId to prevent repeat failures",
                collectionExtId.ExternalId, provider.Name);
            // Remove the bad ExternalId so this collection is skipped on future runs.
            db.MediaExternalIds.Remove(collectionExtId);
            await db.SaveChangesAsync(ct);
            return true;
        }

        // Sanity-check: the collection name returned by the provider must match the container's
        // stored name. A mismatch means the movie was enriched against the wrong TMDB entry
        // (e.g. a Chinese "Alien Invasion" film incorrectly matched to the Alien franchise).
        // Delete any existing stubs (they're all wrong) and return false so the caller knows
        // to reset enrichment for real children (force a fresh TMDB search, not a re-fetch
        // of the same wrong ID).
        if (!string.IsNullOrEmpty(collectionMeta.Title) &&
            !string.Equals(collectionMeta.Title, collection.Name, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Collection {CollectionId} \"{ContainerName}\": provider returned name '{ProviderName}' — " +
                "ExternalId {ExternalId} is a wrong match. Removing any stubs under this container.",
                collection.Id, collection.Name, collectionMeta.Title, collectionExtId.ExternalId);

            await PurgeStubsAsync(db, collection.Id, ct);

            // Also clear the container's wrong ExternalId. Without this, when the re-enriched
            // movies call EnsureCollectionParentAsync they would find THIS container (by
            // collection:{id}) and re-parent back into the same wrongly-named container instead
            // of creating a new, correctly-named one.
            db.MediaExternalIds.Remove(collectionExtId);
            await db.SaveChangesAsync(ct);

            return false;
        }

        if (collectionMeta.Results is null || collectionMeta.Results.Count == 0)
            return true;

        var mediaTypeId = collection.MediaTypeId;

        // The authoritative set of ExternalIds for this collection according to the provider.
        var authoritative = collectionMeta.Results
            .Where(p => !string.IsNullOrEmpty(p.ExternalId))
            .Select(p => p.ExternalId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Remove stale stubs: stubs whose ExternalId is no longer in the provider's member list.
        // Real library items (IsStub=false) are never touched.
        var staleStubs = await db.MediaItems
            .Include(m => m.ExternalIds)
            .Where(m => m.ParentId == collection.Id && m.IsStub)
            .ToListAsync(ct);

        foreach (var stale in staleStubs)
        {
            var hasValidId = stale.ExternalIds.Any(e => authoritative.Contains(e.ExternalId));
            if (!hasValidId)
            {
                db.MediaExternalIds.RemoveRange(stale.ExternalIds);
                db.MediaItems.Remove(stale);
                logger.LogInformation(
                    "Removed stale stub {Id} \"{Name}\" from collection {CollectionId} \"{CollectionName}\"",
                    stale.Id, stale.Name, collection.Id, collection.Name);
            }
        }
        await db.SaveChangesAsync(ct);

        // Load existing ExternalIds for children of this collection to avoid duplicates.
        var existingChildExtIds = await db.MediaExternalIds
            .Where(e => db.MediaItems.Any(m => m.Id == e.MediaItemId && m.ParentId == collection.Id))
            .Select(e => e.ExternalId)
            .ToHashSetAsync(ct);

        int created = 0;
        var now = DateTime.UtcNow;

        foreach (var part in collectionMeta.Results)
        {
            if (string.IsNullOrEmpty(part.ExternalId) || string.IsNullOrEmpty(part.Title)) continue;

            // Skip if a child of THIS collection already has this ExternalId.
            if (existingChildExtIds.Contains(part.ExternalId)) continue;

            // Skip if the movie already exists anywhere in the DB by this ExternalId
            // (it will be re-parented by EnsureCollectionParentAsync when it gets enriched).
            if (await db.MediaExternalIds.AnyAsync(e => e.ExternalId == part.ExternalId, ct))
                continue;

            // Also skip if a movie with the same title + year already exists in this media type.
            // This prevents creating stubs when the movie is already in the library under a
            // different source's ExternalId (e.g. imported via Trakt before TMDB enrichment ran).
            if (part.Year.HasValue &&
                await db.MediaItems.AnyAsync(m =>
                    m.MediaTypeId == mediaTypeId &&
                    m.Name == part.Title &&
                    m.Year == part.Year, ct))
                continue;

            var stub = new MediaItem
            {
                MediaTypeId    = mediaTypeId,
                ParentId       = collection.Id,
                HierarchyLevel = 1,
                Name           = part.Title,
                Year           = part.Year,
                PosterUrl      = part.PosterUrl,
                IsStub         = true,
                CreatedAt      = now,
                UpdatedAt      = now,
            };
            db.MediaItems.Add(stub);
            await db.SaveChangesAsync(ct);

            db.MediaExternalIds.Add(new MediaExternalId
            {
                MediaItemId = stub.Id,
                Source      = collectionExtId.Source,
                ExternalId  = part.ExternalId,
            });

            // Seed enrichment rows immediately so all plugins process this stub
            // without waiting for the next SeedEnrichmentRowsAsync cycle.
            var mediaTypeName = collection.MediaType?.Name ?? string.Empty;
            foreach (var (pluginId, pluginProvider) in allProviders ?? [])
            {
                var supported = pluginProvider.GetSupportedMediaTypes()
                    .Any(s => string.Equals(s.MediaTypeName, mediaTypeName, StringComparison.OrdinalIgnoreCase));
                if (!supported) continue;

                var alreadyHasRow = await db.MediaEnrichments
                    .AnyAsync(e => e.MediaItemId == stub.Id && e.PluginId == pluginId, ct);
                if (!alreadyHasRow)
                {
                    db.MediaEnrichments.Add(new MediaItemEnrichment
                    {
                        MediaItemId = stub.Id,
                        PluginId    = pluginId,
                        Status      = EnrichmentStatus.Pending,
                        MaxRetries  = 3,
                    });
                }
            }

            await db.SaveChangesAsync(ct);

            existingChildExtIds.Add(part.ExternalId);
            created++;
            logger.LogInformation(
                "Created collection stub {StubId} \"{Name}\" under collection {CollectionId} \"{CollectionName}\"",
                stub.Id, stub.Name, collection.Id, collection.Name);
        }

        if (created > 0)
            logger.LogInformation(
                "Created {Count} stub(s) for collection {CollectionId} \"{CollectionName}\"",
                created, collection.Id, collection.Name);

        return true;
    }

    public async Task RebuildSingleCollectionAsync(
        int collectionId,
        IReadOnlyList<(string PluginId, IMetadataProvider Provider)> providers,
        CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        var collection = await db.MediaItems
            .Include(m => m.MediaType)
            .Include(m => m.ExternalIds)
            .FirstOrDefaultAsync(m => m.Id == collectionId && m.HierarchyLevel == 0, ct);

        if (collection is null || collection.MediaType?.Name != "movies")
        {
            logger.LogWarning("RebuildSingleCollection: item {Id} not found or not a movie collection", collectionId);
            return;
        }

        // Re-parent all non-stub children — any that have mismatched belongsToCollection data
        // will be moved to their correct collection by EnsureCollectionParentAsync.
        var children = await db.MediaItems
            .Include(m => m.MediaType)
            .Include(m => m.ExternalIds)
            .Where(m => m.ParentId == collectionId && !m.IsStub)
            .ToListAsync(ct);

        foreach (var child in children)
        {
            try
            {
                await EnsureCollectionParentAsync(db, child, pluginId: null, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "RebuildSingleCollection: re-parent failed for child {Id} \"{Name}\"",
                    child.Id, child.Name);
            }
        }

        // Reload the collection — it may have been cleaned up as orphaned if all children moved
        collection = await db.MediaItems
            .Include(m => m.MediaType)
            .Include(m => m.ExternalIds)
            .FirstOrDefaultAsync(m => m.Id == collectionId, ct);
        if (collection is null) return;

        // Find the provider matching this collection's source
        var collExtId = collection.ExternalIds.FirstOrDefault(e => e.ExternalId.StartsWith("collection:"));
        if (collExtId is null)
        {
            // No collection ExternalId — stubs can't be validated, purge them all.
            var orphanedStubs = await db.MediaItems
                .Where(m => m.ParentId == collectionId && m.IsStub)
                .ToListAsync(ct);
            if (orphanedStubs.Count > 0)
            {
                db.MediaItems.RemoveRange(orphanedStubs);
                await db.SaveChangesAsync(ct);
                logger.LogInformation(
                    "RebuildSingleCollection: purged {Count} unverifiable stub(s) from collection {Id} (no collection ExternalId)",
                    orphanedStubs.Count, collectionId);
            }
            await RemoveOrphanedCollectionAsync(db, collectionId, collection.MediaTypeId, ct);
            return;
        }

        var providerEntry = providers.FirstOrDefault(p =>
            collExtId.Source?.EndsWith(
                p.PluginId.Split('.').Last(),
                StringComparison.OrdinalIgnoreCase) == true);
        if (providerEntry.Provider is null) return;

        var nameMatched = await EnsureCollectionStubsAsync(db, collection, providerEntry.Provider, ct, providers);

        if (!nameMatched)
        {
            // The collection ExternalId points to the wrong TMDB collection. Clear the stored
            // TMDB ExternalIds for all non-stub children so that re-enrichment does a fresh name
            // search instead of re-fetching the same wrong ID (which would repeat the bad match).
            var providerSource = PluginIdHelper.ToSource(providerEntry.Provider.Name);
            var childIds = children.Select(c => c.Id).ToList();

            var wrongExtIds = await db.MediaExternalIds
                .Where(e => childIds.Contains(e.MediaItemId) &&
                            string.Equals(e.Source, providerSource, StringComparison.OrdinalIgnoreCase))
                .ToListAsync(ct);
            db.MediaExternalIds.RemoveRange(wrongExtIds);

            var enrichmentRows = await db.MediaEnrichments
                .Where(e => childIds.Contains(e.MediaItemId) && e.PluginId == providerEntry.PluginId)
                .ToListAsync(ct);
            foreach (var row in enrichmentRows)
            {
                row.ExternalId      = null;
                row.Status          = EnrichmentStatus.Pending;
                row.RetryCount      = 0;
                row.ErrorMessage    = null;
                row.LastAttemptedAt = null;
            }
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "RebuildSingleCollection: reset TMDB enrichment for {Count} child(ren) of wrong-matched collection {Id}",
                childIds.Count, collectionId);
        }

        // If stubs were purged and no real children remain, remove the empty container.
        await RemoveOrphanedCollectionAsync(db, collectionId, collection.MediaTypeId, ct);

        logger.LogInformation("RebuildSingleCollection: finished collection {Id} \"{Name}\"",
            collectionId, collection.Name);
    }

    public async Task DeduplicateCollectionsAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        // Find all (MediaTypeId, Name) groups that have more than one Level-0 container.
        var duplicateGroups = await db.MediaItems
            .Where(m => m.HierarchyLevel == 0)
            .GroupBy(m => new { m.MediaTypeId, m.Name })
            .Where(g => g.Count() > 1)
            .Select(g => new { g.Key.MediaTypeId, g.Key.Name })
            .ToListAsync(ct);

        if (duplicateGroups.Count == 0) return;

        logger.LogInformation("DeduplicateCollections: found {Count} duplicate collection name(s)", duplicateGroups.Count);

        foreach (var group in duplicateGroups)
        {
            ct.ThrowIfCancellationRequested();

            var containers = await db.MediaItems
                .Include(m => m.ExternalIds)
                .Where(m => m.MediaTypeId == group.MediaTypeId &&
                            m.HierarchyLevel == 0 &&
                            m.Name == group.Name)
                .OrderBy(m => m.Id) // keep the oldest (lowest Id)
                .ToListAsync(ct);

            if (containers.Count < 2) continue;

            var keeper = containers[0];
            var dupes  = containers.Skip(1).ToList();

            foreach (var dupe in dupes)
            {
                // Re-parent all children to the keeper
                var children = await db.MediaItems
                    .Where(m => m.ParentId == dupe.Id)
                    .ToListAsync(ct);
                foreach (var child in children)
                {
                    child.ParentId  = keeper.Id;
                    child.UpdatedAt = DateTime.UtcNow;
                }

                // Move ExternalIds that aren't already on the keeper
                foreach (var extId in dupe.ExternalIds.ToList())
                {
                    var alreadyOnKeeper = keeper.ExternalIds
                        .Any(e => e.ExternalId == extId.ExternalId && e.Source == extId.Source);
                    if (!alreadyOnKeeper)
                    {
                        extId.MediaItemId = keeper.Id;
                    }
                    else
                    {
                        db.MediaExternalIds.Remove(extId);
                    }
                }

                // Merge poster/overview if keeper is missing them
                if (keeper.PosterUrl is null && dupe.PosterUrl is not null)
                    keeper.PosterUrl = dupe.PosterUrl;
                if (keeper.Overview is null && dupe.Overview is not null)
                    keeper.Overview = dupe.Overview;

                await db.SaveChangesAsync(ct);

                db.MediaItems.Remove(dupe);
                await db.SaveChangesAsync(ct);

                logger.LogInformation(
                    "Merged duplicate collection {DupeId} \"{Name}\" into keeper {KeeperId}",
                    dupe.Id, dupe.Name, keeper.Id);
            }
        }

        logger.LogInformation("DeduplicateCollections: complete");
    }

    public async Task ProcessAllExistingMovieCollectionsAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Starting rebuild: processing movie collections from existing metadata");

        int lastId = 0, processed = 0, reparented = 0;

        // Process all movie items (Level 0 standalone AND Level 1 already-parented) that have
        // belongsToCollection in metadata — this corrects movies that were enriched before the
        // collection feature existed AND movies that ended up under the wrong collection.
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

            var batch = await db.MediaItems
                .Include(m => m.MediaType)
                .Include(m => m.ExternalIds)
                .Where(m => m.MediaType!.Name == "movies" &&
                            m.HierarchyLevel <= 1 &&
                            m.IsStub == false &&
                            m.Id > lastId &&
                            m.MetadataJson != null &&
                            m.MetadataJson.Contains("belongsToCollection"))
                .OrderBy(m => m.Id)
                .Take(BulkBatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0) break;

            foreach (var item in batch)
            {
                try
                {
                    var hadParent = item.ParentId;
                    await EnsureCollectionParentAsync(db, item, pluginId: null, ct);
                    if (item.ParentId != hadParent)
                        reparented++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Collection rebuild failed for item {Id} \"{Name}\"", item.Id, item.Name);
                }
            }

            processed += batch.Count;
            lastId     = batch[^1].Id;
        }

        logger.LogInformation(
            "Collection rebuild pass 1 complete: {Processed} movies examined, {Reparented} re-parented",
            processed, reparented);
    }

    /// <summary>
    /// Second pass of the rebuild: iterates all Level-0 collection containers and creates stubs
    /// for any missing members using the supplied metadata providers.
    /// </summary>
    public async Task CreateStubsForAllCollectionsAsync(
        IReadOnlyList<(string PluginId, IMetadataProvider Provider)> providers,
        CancellationToken ct = default)
    {
        int lastId = 0, totalStubs = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

            var batch = await db.MediaItems
                .Include(m => m.MediaType)
                .Include(m => m.ExternalIds)
                .Where(m => m.MediaType!.Name == "movies" &&
                            m.HierarchyLevel == 0 &&
                            m.Id > lastId &&
                            m.ExternalIds.Any(e => e.ExternalId.StartsWith("collection:")))
                .OrderBy(m => m.Id)
                .Take(BulkBatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0) break;

            foreach (var collection in batch)
            {
                // Find the provider whose source matches this collection's ExternalId
                var collExtId = collection.ExternalIds
                    .FirstOrDefault(e => e.ExternalId.StartsWith("collection:"));
                if (collExtId is null) continue;

                // Match provider by source suffix (e.g. "chronicle.plugin.tmdb" → "tmdb")
                var provider = providers.FirstOrDefault(p =>
                    collExtId.Source?.EndsWith(
                        p.PluginId.Split('.').Last(),
                        StringComparison.OrdinalIgnoreCase) == true).Provider;
                if (provider is null) continue;

                var stubsBefore = await db.MediaItems.CountAsync(
                    m => m.ParentId == collection.Id && m.IsStub, ct);

                try
                {
                    await EnsureCollectionStubsAsync(db, collection, provider, ct, providers);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Stub creation failed for collection {Id} \"{Name}\"",
                        collection.Id, collection.Name);
                }

                var stubsAfter = await db.MediaItems.CountAsync(
                    m => m.ParentId == collection.Id && m.IsStub, ct);
                totalStubs += stubsAfter - stubsBefore;
            }

            lastId = batch[^1].Id;
        }

        logger.LogInformation("Collection rebuild pass 2 complete: {Stubs} new stubs created", totalStubs);
    }

    /// <param name="Id">Plugin-specific collection identifier (string for portability).</param>
    /// <param name="Name">Display name of the collection.</param>
    /// <param name="PosterUrl">
    ///   Optional poster URL. Must be a fully-qualified HTTP/S URL — plugins are responsible
    ///   for resolving relative CDN paths before writing to <c>metadata_json</c>.
    ///   (TMDB plugin satisfies this via <c>TmdbClient.BuildImageUrl</c> in <c>MapMovie</c>.)
    /// </param>
    /// <param name="Source">Short source name derived from the plugin key (e.g. "tmdb").</param>
    internal record CollectionData(string Id, string Name, string? PosterUrl, string Source);
}
