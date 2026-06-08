using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

public class MovieCollectionService(ILogger<MovieCollectionService> logger) : IMovieCollectionService
{
    private const string TmdbPluginKey = "chronicle.plugin.tmdb";

    public async Task EnsureCollectionParentAsync(
        ChronicleDbContext db,
        MediaItem movieItem,
        CancellationToken ct = default)
    {
        // Only group "movies" type — not fanedits, not tv, not anything else
        if (movieItem.MediaType is null ||
            !string.Equals(movieItem.MediaType.Name, "movies", StringComparison.OrdinalIgnoreCase))
            return;

        var collectionData = ExtractCollectionData(movieItem.MetadataJson);
        if (collectionData is null)
        {
            // Movie has no collection — ensure it is at root level
            if (movieItem.ParentId is not null)
            {
                movieItem.ParentId = null;
                movieItem.HierarchyLevel = 0;
                movieItem.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            return;
        }

        // Find or create the collection MediaItem
        var externalIdValue = $"collection:{collectionData.Id}";
        var collection = await FindOrCreateCollectionAsync(
            db, movieItem.MediaTypeId, collectionData, externalIdValue, ct);

        // Re-parent the movie if needed
        if (movieItem.ParentId != collection.Id)
        {
            movieItem.ParentId = collection.Id;
            movieItem.HierarchyLevel = 1;
            movieItem.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Movie {ItemId} \"{Name}\" re-parented under collection {CollectionId} \"{CollectionName}\"",
                movieItem.Id, movieItem.Name, collection.Id, collection.Name);
        }
    }

    private static CollectionData? ExtractCollectionData(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;

            // Try full plugin key first, then short "tmdb" fallback
            JsonElement tmdbEl;
            if (!root.TryGetProperty(TmdbPluginKey, out tmdbEl) &&
                !root.TryGetProperty("tmdb", out tmdbEl))
                return null;

            if (!tmdbEl.TryGetProperty("belongsToCollection", out var collEl) ||
                collEl.ValueKind == JsonValueKind.Null)
                return null;

            if (!collEl.TryGetProperty("id", out var idEl) ||
                !collEl.TryGetProperty("name", out var nameEl))
                return null;

            var name = nameEl.GetString();
            if (string.IsNullOrEmpty(name)) return null;

            string? posterUrl = collEl.TryGetProperty("posterPath", out var pEl)
                ? pEl.GetString() : null;

            return new CollectionData(
                idEl.GetInt32(),
                name,
                posterUrl);
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
        // Try to find by external ID first (most reliable)
        var existing = await db.MediaExternalIds
            .Where(e => e.ExternalId == externalIdValue && e.Source == "tmdb")
            .Select(e => e.MediaItem)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            // Update poster if it changed
            if (existing.PosterUrl != data.PosterUrl && data.PosterUrl is not null)
            {
                existing.PosterUrl = data.PosterUrl;
                existing.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            return existing;
        }

        // Create new collection MediaItem
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

        // Store external ID
        db.MediaExternalIds.Add(new MediaExternalId
        {
            MediaItemId = collection.Id,
            Source      = "tmdb",
            ExternalId  = externalIdValue,
        });
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Created collection MediaItem {Id} \"{Name}\" (ExternalId={ExternalId})",
            collection.Id, collection.Name, externalIdValue);

        return collection;
    }

    private record CollectionData(int Id, string Name, string? PosterUrl);
}
