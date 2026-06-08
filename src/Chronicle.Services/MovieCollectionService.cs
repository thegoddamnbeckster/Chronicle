using System.Text.Json;
using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

public class MovieCollectionService(ILogger<MovieCollectionService> logger) : IMovieCollectionService
{
    public async Task EnsureCollectionParentAsync(
        ChronicleDbContext db,
        MediaItem movieItem,
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
        var collection = await FindOrCreateCollectionAsync(
            db, movieItem.MediaTypeId, collectionData, externalIdValue, ct);

        // Re-parent the movie if needed
        if (movieItem.ParentId != collection.Id)
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
    /// Scans every plugin blob in <paramref name="metadataJson"/> for a
    /// <c>belongsToCollection</c> object.  Returns the first match found,
    /// regardless of which plugin wrote it — so any future plugin that stores
    /// collection data in this standard shape will be supported automatically.
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

            // Iterate every top-level plugin blob (e.g. "chronicle.plugin.tmdb", "chronicle.plugin.hardcover")
            foreach (var pluginProp in root.EnumerateObject())
            {
                var pluginEl = pluginProp.Value;
                if (pluginEl.ValueKind != JsonValueKind.Object) continue;

                if (!pluginEl.TryGetProperty("belongsToCollection", out var collEl) ||
                    collEl.ValueKind != JsonValueKind.Object)
                    continue;

                if (!collEl.TryGetProperty("name", out var nameEl)) continue;
                var name = nameEl.GetString();
                if (string.IsNullOrEmpty(name)) continue;

                // ID may be stored as a number (TMDB) or a string (other plugins)
                string? idStr = null;
                if (collEl.TryGetProperty("id", out var idEl))
                {
                    idStr = idEl.ValueKind == JsonValueKind.Number
                        ? idEl.GetRawText()          // e.g. "748"
                        : idEl.GetString();           // already a string
                }
                if (string.IsNullOrEmpty(idStr)) continue;

                string? posterUrl = collEl.TryGetProperty("posterPath", out var pEl)
                    ? pEl.GetString() : null;

                // Derive a short source name from the plugin key
                // e.g. "chronicle.plugin.tmdb" → "tmdb", "tmdb" → "tmdb"
                var source = PluginIdHelper.ToSource(pluginProp.Name);

                return new CollectionData(idStr, name, posterUrl, source);
            }

            return null;
        }
        catch { return null; }
    }

    private async Task<MediaItem> FindOrCreateCollectionAsync(
        ChronicleDbContext db,
        int mediaTypeId,
        CollectionData data,
        string externalIdValue,
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
                return existing;
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
            return byName;
        }

        // ── Create a new collection item — wrapped in a transaction for atomicity ──
        // Without this, a failure between the two SaveChangesAsync calls would leave
        // an orphaned collection item with no external ID, causing duplicates on retry.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
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
            await db.SaveChangesAsync(ct);

            db.MediaExternalIds.Add(new MediaExternalId
            {
                MediaItemId = collection.Id,
                Source      = data.Source,
                ExternalId  = externalIdValue,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            logger.LogInformation(
                "Created collection MediaItem {Id} \"{Name}\" (source={Source}, ExternalId={ExternalId})",
                collection.Id, collection.Name, data.Source, externalIdValue);

            return collection;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
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
