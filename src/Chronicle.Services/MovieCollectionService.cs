using System.Text.Json;
using Chronicle.Core.Exceptions;
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
    private const string CollectionExternalIdPrefix = "collection:";

    private const int BulkBatchSize = 200;

    /// <summary>
    /// Sentinel MediaExternalId.ExternalId (Source "chronicle") marking that a movie's current
    /// collection membership was explicitly chosen by the user via ReparentIntoCollectionAsync,
    /// not inferred from a provider's belongsToCollection data. See EnsureCollectionParentAsync.
    /// </summary>
    private const string ManualCollectionMemberMarker = "manual-collection-member";
    public async Task EnsureCollectionParentAsync(
        ChronicleDbContext db,
        MediaItem movieItem,
        string? pluginId = null,
        CancellationToken ct = default)
    {
        // Lazily load MediaType if the caller didn't eagerly include it
        if (movieItem.MediaType is null)
            await db.Entry(movieItem).Reference(m => m.MediaType).LoadAsync(ct);

        // Only group movie-like types (movies, fanedits, anime) — not tv, not anything else
        if (movieItem.MediaType is null || !IsMovieLikeTypeName(movieItem.MediaType.Name))
            return;

        // A user who manually placed this item into a collection (via ReparentIntoCollectionAsync)
        // must never have that choice silently undone by a later automatic enrichment pass.
        // Concretely: manually reparenting resets the item's enrichment rows to Pending so it
        // re-enriches in its new context; when that re-enrichment runs and the item's own TMDB
        // match has no belongsToCollection data (e.g. a compilation/anthology movie TMDB doesn't
        // formally list as part of the collection, like "Evil Bong-A-Thon!"), the branch below
        // would force it straight back to root -- undoing the manual placement within minutes,
        // with no visible error. The ManualCollectionMemberMarker below opts an item out of ALL
        // auto-parenting logic (both directions) until the user explicitly removes it themselves
        // via UnparentFromCollectionAsync, which clears the marker.
        var isManuallyPlaced = await db.MediaExternalIds.AnyAsync(
            e => e.MediaItemId == movieItem.Id && e.ExternalId == ManualCollectionMemberMarker, ct);
        if (isManuallyPlaced)
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
                return (existing, WasReparented: false);
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
            return (byName, WasReparented: false);
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
                    return (raceItem, WasReparented: false);
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
                return (raceByName, WasReparented: false);
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

            return (collection, WasReparented: true);
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
        // Derive the source key from the provider's full plugin ID (e.g. "chronicle.plugin.tmdb" → "tmdb").
        // allProviders carries the canonical plugin IDs; fall back to provider.Name for callers that
        // don't supply allProviders (comparison is OrdinalIgnoreCase so "TMDB" matches source "tmdb").
        var callerEntry  = allProviders?.FirstOrDefault(p => ReferenceEquals(p.Provider, provider));
        var providerSource = callerEntry.HasValue
            ? PluginIdHelper.ToSource(callerEntry.Value.PluginId)
            : PluginIdHelper.ToSource(provider.Name);
        var collectionExtId = collection.ExternalIds
            .FirstOrDefault(e => string.Equals(e.Source, providerSource, StringComparison.OrdinalIgnoreCase)
                              && e.ExternalId.StartsWith("collection:", StringComparison.OrdinalIgnoreCase));

        if (collectionExtId is null)
            return true;

        var collectionPluginId = callerEntry.HasValue ? callerEntry.Value.PluginId : provider.Name;
        MediaMetadata? collectionMeta;
        try
        {
            collectionMeta = await ProviderCallGuard.CallAsync<MediaMetadata?>(
                t => provider.GetByIdAsync(collectionExtId.ExternalId, t), collectionPluginId, "GetByIdAsync", null,
                msg => logger.LogWarning("{Msg}", msg), msg => logger.LogError("{Msg}", msg), ct);
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

        if (collectionMeta is null)
        {
            // Timed out inside ProviderCallGuard -- treat like any other transient failure,
            // but don't remove the ExternalId since the data itself was never proven bad.
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

        if (allProviders is null || allProviders.Count == 0)
            logger.LogWarning(
                "EnsureCollectionStubsAsync: allProviders not supplied for collection {Id} \"{Name}\" — " +
                "enrichment rows will not be seeded for newly created stubs",
                collection.Id, collection.Name);

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

            // The movie already exists somewhere in the DB (by ExternalId, or by normalized
            // title+year — see ReparentExistingMemberIfNeededAsync for why both checks are
            // needed and why raw Name == was not enough). Reparent it into this collection
            // directly instead of just skipping stub creation and hoping something else
            // reparents it later.
            //
            // Confirmed directly (2026-08-03): the ORIGINAL code only skipped stub creation
            // here ("it will be re-parented by EnsureCollectionParentAsync when it gets
            // enriched") without actually reparenting anything. That assumption only holds if
            // the item's OWN enrichment runs again after the collection is discovered — an
            // item enriched in the past (which is HOW it already has this ExternalId/title
            // match) never automatically re-triggers that. The result was exactly what got
            // reported: the collection looked like it was "missing" a movie the user
            // definitely owned, because the real file was sitting standalone, correctly
            // matched to TMDB, and simply never told to join this collection.
            if (await ReparentExistingMemberIfNeededAsync(db, collection, part, mediaTypeId, ct))
                continue;

            // Save stub + ExternalId in one atomic write so the stub is never
            // visible without its ExternalId. A concurrent run seeing a stub
            // without an ExternalId would treat it as stale and delete it,
            // then recreate it — causing the duplicates we're preventing here.
            // Seed a minimal MetadataJson with the rating from the collection-members endpoint
            // so the stub shows a rating in the UI without waiting for a full enrichment pass.
            string? stubMetadataJson = null;
            if (part.Rating.HasValue && callerEntry.HasValue)
                stubMetadataJson = JsonSerializer.Serialize(
                    new Dictionary<string, object>
                        { [callerEntry.Value.PluginId] = new { rating = part.Rating.Value } });

            var stub = new MediaItem
            {
                MediaTypeId    = mediaTypeId,
                ParentId       = collection.Id,
                HierarchyLevel = 1,
                Name           = part.Title,
                Year           = part.Year,
                PosterUrl      = part.PosterUrl,
                IsStub         = true,
                MetadataJson   = stubMetadataJson,
                CreatedAt      = now,
                UpdatedAt      = now,
                ExternalIds    =
                [
                    new MediaExternalId
                    {
                        Source     = collectionExtId.Source,
                        ExternalId = part.ExternalId,
                    }
                ],
            };
            // Seed enrichment rows for the new stub in the same SaveChangesAsync so
            // the stub and its rows are committed atomically. Use the MediaItem nav
            // property instead of MediaItemId — EF Core resolves the FK after insert.
            // The stub was just created so there are no pre-existing rows to check for.
            var mediaTypeName = collection.MediaType?.Name ?? string.Empty;
            foreach (var (pluginId, pluginProvider) in allProviders ?? [])
            {
                var supported = pluginProvider.GetSupportedMediaTypes()
                    .Any(s => string.Equals(s.MediaTypeName, mediaTypeName, StringComparison.OrdinalIgnoreCase));
                if (!supported) continue;

                db.MediaEnrichments.Add(new MediaItemEnrichment
                {
                    MediaItem  = stub,
                    PluginId   = pluginId,
                    Status     = EnrichmentStatus.Pending,
                    MaxRetries = 3,
                });
            }

            db.MediaItems.Add(stub);
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

    /// <summary>
    /// If a real (non-stub) item matching this collection part already exists elsewhere in the
    /// library -- by ExternalId, or by normalized title+year when no ExternalId match exists --
    /// reparents it into <paramref name="collection"/> (unless already correctly parented there)
    /// and returns true so the caller skips creating a duplicate stub. Returns false when no
    /// existing item was found, meaning the caller should create a new stub instead.
    ///
    /// Confirmed directly (2026-08-03): before this existed, both the ExternalId match and the
    /// title+year match below only ever skipped stub *creation* — neither actually moved the
    /// real item into the collection. That relied on the item's own enrichment re-running and
    /// discovering belongsToCollection on its own, which never happens for an item that was
    /// already enriched (and thus already has the matching ExternalId/title) before this
    /// collection was even discovered. The visible symptom was a collection missing a movie the
    /// user definitely had a file for, sitting correctly-matched but un-reparented elsewhere.
    /// </summary>
    private async Task<bool> ReparentExistingMemberIfNeededAsync(
        ChronicleDbContext db, MediaItem collection, MediaMetadata part, int mediaTypeId, CancellationToken ct)
    {
        var existing = await db.MediaItems
            .Where(m => m.MediaTypeId == mediaTypeId && !m.IsStub)
            .Where(m => db.MediaExternalIds.Any(e => e.MediaItemId == m.Id && e.ExternalId == part.ExternalId))
            .FirstOrDefaultAsync(ct);

        if (existing is null && part.Year.HasValue)
        {
            var normalizedPartTitle = MediaItemNormalizer.NormalizeName(part.Title);
            if (!string.IsNullOrEmpty(normalizedPartTitle))
            {
                var sameYearCandidates = await db.MediaItems
                    .Where(m => m.MediaTypeId == mediaTypeId && m.Year == part.Year && !m.IsStub)
                    .ToListAsync(ct);
                existing = sameYearCandidates.FirstOrDefault(
                    m => MediaItemNormalizer.NormalizeName(m.Name) == normalizedPartTitle);
            }
        }

        if (existing is null) return false;
        if (existing.Id == collection.Id) return true; // paranoia guard, shouldn't happen

        if (existing.ParentId != collection.Id)
        {
            var oldParentId = existing.ParentId;
            existing.ParentId       = collection.Id;
            existing.HierarchyLevel = 1;
            existing.UpdatedAt      = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Reparented existing item {ItemId} \"{Name}\" into collection {CollectionId} \"{CollectionName}\" " +
                "(was previously parent={OldParent}) -- found already in the library rather than creating a duplicate stub",
                existing.Id, existing.Name, collection.Id, collection.Name,
                oldParentId?.ToString() ?? "root");

            if (oldParentId.HasValue && oldParentId.Value != collection.Id)
                await RemoveOrphanedCollectionAsync(db, oldParentId.Value, mediaTypeId, ct);
        }

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

        if (collection is null || !IsMovieLikeTypeName(collection.MediaType?.Name))
        {
            logger.LogWarning("RebuildSingleCollection: item {Id} not found or not a movie-like collection", collectionId);
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
        var mergeService = scope.ServiceProvider.GetRequiredService<IMergeService>();

        // Only Level-0 items that are genuinely collection CONTAINERS (marked with a
        // "collection:{id}" external ID by EnsureCollectionParentAsync/FindOrCreateCollectionAsync)
        // are eligible here. This previously grouped ALL root-level items library-wide by
        // (MediaTypeId, Name) with no such check — meaning two entirely unrelated movies/shows
        // that happened to share an exact title (not rare: remakes, reboots, generic titles)
        // were treated as duplicates and one was permanently deleted (cascade-deleting that
        // item's ratings/watch history) with no merge log and no way to reverse it. Confirmed
        // 2026-08-05 during a systematic review of the merge/collection/TV-episode system.
        var collectionContainerIds = await db.MediaExternalIds
            .Where(e => e.ExternalId.StartsWith(CollectionExternalIdPrefix))
            .Select(e => e.MediaItemId)
            .Distinct()
            .ToListAsync(ct);

        if (collectionContainerIds.Count == 0) return;

        // Find all (MediaTypeId, Name) groups that have more than one Level-0 collection container.
        var duplicateGroups = await db.MediaItems
            .Where(m => m.HierarchyLevel == 0 && collectionContainerIds.Contains(m.Id))
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
                .Where(m => m.MediaTypeId == group.MediaTypeId &&
                            m.HierarchyLevel == 0 &&
                            m.Name == group.Name &&
                            collectionContainerIds.Contains(m.Id))
                .OrderBy(m => m.Id) // keep the oldest (lowest Id)
                .ToListAsync(ct);

            if (containers.Count < 2) continue;

            var keeperId = containers[0].Id;

            foreach (var dupe in containers.Skip(1))
            {
                // Delegate to MergeService.MergeAsync instead of hand-rolling the merge: it
                // transfers UserLibrary/InteractionEvents/MediaCredits/MediaListItems (not just
                // children/ExternalIds/poster/overview like the old inline logic did), and
                // writes a MediaItemMerges audit row so this is reversible via Unmerge if the
                // name match ever turns out to be wrong.
                //
                // Caught per-pair rather than left to propagate: MergeAsync can legitimately
                // throw InvalidOperationException (e.g. one side was concurrently deleted or
                // already merged by EnsureCollectionParentAsync/RemoveOrphanedCollectionAsync
                // running from a live enrichment pass). An uncaught throw here would abort the
                // whole DeduplicateCollectionsAsync call mid-loop, silently leaving every
                // remaining duplicate group unmerged and skipping the rebuild passes that run
                // after it in RebuildMovieCollectionsService — mirrors the try/catch already
                // used around EnsureCollectionParentAsync/EnsureCollectionStubsAsync elsewhere
                // in this file.
                try
                {
                    await mergeService.MergeAsync(keeperId, dupe.Id, mergedByUserId: null, ct);

                    logger.LogInformation(
                        "Merged duplicate collection {DupeId} \"{Name}\" into keeper {KeeperId}",
                        dupe.Id, dupe.Name, keeperId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex,
                        "DeduplicateCollections: failed to merge {DupeId} \"{Name}\" into keeper {KeeperId}; skipping this pair",
                        dupe.Id, dupe.Name, keeperId);
                }
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
                .Where(m => (m.MediaType!.Name == "movies" || m.MediaType!.Name == "fanedits" || m.MediaType!.Name == "anime_movies") &&
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
                .Where(m => (m.MediaType!.Name == "movies" || m.MediaType!.Name == "fanedits" || m.MediaType!.Name == "anime_movies") &&
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

    public async Task UnparentFromCollectionAsync(ChronicleDbContext db, int itemId, CancellationToken ct = default)
    {
        var item = await db.MediaItems
            .Include(m => m.MediaType)
            .FirstOrDefaultAsync(m => m.Id == itemId, ct);

        if (item is null || item.HierarchyLevel != 1 || item.ParentId is null)
            return;
        // Only flat media types (HierarchyLevels == 1) use the collection grouping concept — a
        // Level-1 item of a hierarchical type (e.g. a TV season, a music album) is structural,
        // not a collection member, and must never be pulled out via this path.
        if (item.MediaType?.HierarchyLevels != 1)
            return;

        var oldParentId = item.ParentId.Value;

        item.ParentId       = null;
        item.HierarchyLevel = 0;
        item.UpdatedAt      = DateTime.UtcNow;

        // Clear the manual-placement marker (if this item was ever manually reparented in) so
        // auto-parenting logic applies normally again — the user has now made a different
        // explicit choice (removing it), not left it in some auto-inferred state.
        var marker = await db.MediaExternalIds.FirstOrDefaultAsync(
            e => e.MediaItemId == item.Id && e.ExternalId == ManualCollectionMemberMarker, ct);
        if (marker is not null)
            db.MediaExternalIds.Remove(marker);

        // Reset enrichment so the item re-enriches at root level (fresh name/year search)
        var enrichmentRows = await db.MediaEnrichments
            .Where(e => e.MediaItemId == item.Id)
            .ToListAsync(ct);
        foreach (var row in enrichmentRows)
        {
            row.Status          = EnrichmentStatus.Pending;
            row.RetryCount      = 0;
            row.LastAttemptedAt = null;
            row.ErrorMessage    = null;
        }

        await db.SaveChangesAsync(ct);

        // If the old collection container is now empty, remove it
        await RemoveOrphanedCollectionAsync(db, oldParentId, item.MediaTypeId, ct);
    }

    public async Task ReparentIntoCollectionAsync(
        ChronicleDbContext db, int movieId, int collectionId, CancellationToken ct = default)
    {
        var movie = await db.MediaItems
            .Include(m => m.MediaType)
            .FirstOrDefaultAsync(m => m.Id == movieId, ct)
            ?? throw new MediaNotFoundException(movieId);

        var collection = await db.MediaItems
            .Include(m => m.MediaType)
            .FirstOrDefaultAsync(m => m.Id == collectionId, ct)
            ?? throw new MediaNotFoundException(collectionId);

        if (movie.MediaType?.HierarchyLevels != 1 || collection.MediaType?.HierarchyLevels != 1)
            throw new InvalidOperationException(
                "Both items must be a flat (non-hierarchical) media type to use collections — " +
                "types with a natural multi-level hierarchy (e.g. TV, Music) can't be grouped this way.");
        if (movie.MediaTypeId != collection.MediaTypeId)
            throw new InvalidOperationException("The movie and the collection must be the same media type.");
        if (movie.ParentId is not null)
            throw new InvalidOperationException("This item already belongs to a collection — remove it from that one first.");
        if (collection.HierarchyLevel != 0 || collection.ParentId is not null)
            throw new InvalidOperationException("The target is not a collection root.");
        if (movie.Id == collection.Id)
            throw new InvalidOperationException("An item can't be its own collection.");

        movie.ParentId       = collection.Id;
        movie.HierarchyLevel = 1;
        movie.UpdatedAt      = DateTime.UtcNow;

        // Mark this placement as user-chosen so EnsureCollectionParentAsync never auto-reverts
        // it — without this, the enrichment reset below (needed so the item re-enriches in its
        // new context) causes the very next enrichment pass to see no belongsToCollection data
        // on this movie's own match and force it straight back to root, silently undoing the
        // move the user just made. Confirmed happening to "Evil Bong-A-Thon!" (a compilation
        // TMDB doesn't formally list under the Evil Bong Collection).
        var alreadyMarked = await db.MediaExternalIds.AnyAsync(
            e => e.MediaItemId == movie.Id && e.ExternalId == ManualCollectionMemberMarker, ct);
        if (!alreadyMarked)
            db.MediaExternalIds.Add(new MediaExternalId
            {
                MediaItemId = movie.Id,
                Source      = "chronicle",
                ExternalId  = ManualCollectionMemberMarker,
            });

        // Reset enrichment so the item re-enriches in its new context, mirroring
        // UnparentFromCollectionAsync's reset on the way out.
        var enrichmentRows = await db.MediaEnrichments
            .Where(e => e.MediaItemId == movie.Id)
            .ToListAsync(ct);
        foreach (var row in enrichmentRows)
        {
            row.Status          = EnrichmentStatus.Pending;
            row.RetryCount      = 0;
            row.LastAttemptedAt = null;
            row.ErrorMessage    = null;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Returns true for the media type names that get TMDB-style automatic collection grouping
    /// (via a plugin's <c>belongsToCollection</c> metadata): movies, fanedits, and anime_movies.
    /// All three are flat (HierarchyLevels == 1), so generic flat-type collection logic already
    /// covers them — this helper exists specifically for <see cref="EnsureCollectionParentAsync"/>
    /// and <see cref="RebuildSingleCollectionAsync"/>, which must NOT run against every flat type
    /// (a hypothetical future flat type with no metadata provider supplying belongsToCollection
    /// data has nothing to auto-group against). Note "anime" (the TV-hierarchical type,
    /// HierarchyLevels == 3) is deliberately excluded — standalone anime films live on the flat
    /// anime_movies type instead, so "anime" itself never needs TMDB collection grouping.
    /// </summary>
    private static bool IsMovieLikeTypeName(string? name) =>
        name is not null &&
        (name.Equals("movies",       StringComparison.OrdinalIgnoreCase) ||
         name.Equals("fanedits",     StringComparison.OrdinalIgnoreCase) ||
         name.Equals("anime_movies", StringComparison.OrdinalIgnoreCase));

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
