using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

public class MovieCollectionServiceTests
{
    private static ChronicleDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ChronicleDbContext(options);
    }

    private static MediaType MoviesType() => new()
    {
        Id = 1, Name = "movies", DisplayName = "Movies",
        HierarchyLevels = 2, HierarchyLabels = "Collection,Movie",
        CreatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task EnsureCollectionParentAsync_NoCollectionData_LeavesItemAtRoot()
    {
        await using var db = CreateInMemoryDb();
        var mt = MoviesType();
        db.MediaTypes.Add(mt);
        var movie = new MediaItem
        {
            Id = 1, Name = "Inception", MediaTypeId = mt.Id, MediaType = mt,
            HierarchyLevel = 0, MetadataJson = "{}", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(movie);
        await db.SaveChangesAsync();

        var svc = new MovieCollectionService(NullLogger<MovieCollectionService>.Instance);
        await svc.EnsureCollectionParentAsync(db, movie);

        movie.ParentId.Should().BeNull();
        movie.HierarchyLevel.Should().Be(0);
    }

    [Fact]
    public async Task EnsureCollectionParentAsync_WithCollectionData_CreatesCollectionAndReparents()
    {
        await using var db = CreateInMemoryDb();
        var mt = MoviesType();
        db.MediaTypes.Add(mt);
        const string metadataJson = """
        {
          "chronicle.plugin.tmdb": {
            "title": "Inception",
            "belongsToCollection": {
              "id": 748,
              "name": "Inception Collection",
              "posterPath": "https://image.tmdb.org/t/p/w500/poster.jpg"
            }
          }
        }
        """;
        var movie = new MediaItem
        {
            Id = 1, Name = "Inception", MediaTypeId = mt.Id, MediaType = mt,
            HierarchyLevel = 0, MetadataJson = metadataJson, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(movie);
        await db.SaveChangesAsync();

        var svc = new MovieCollectionService(NullLogger<MovieCollectionService>.Instance);
        await svc.EnsureCollectionParentAsync(db, movie);

        // Collection item should exist
        var collection = await db.MediaItems.FirstOrDefaultAsync(m => m.Name == "Inception Collection");
        collection.Should().NotBeNull();
        collection!.HierarchyLevel.Should().Be(0);
        collection.MediaTypeId.Should().Be(mt.Id);

        // External ID should be stored
        var extId = await db.MediaExternalIds.FirstOrDefaultAsync(e => e.MediaItemId == collection.Id);
        extId.Should().NotBeNull();
        extId!.ExternalId.Should().Be("collection:748");
        extId.Source.Should().Be("tmdb");

        // Movie should be re-parented
        movie.ParentId.Should().Be(collection.Id);
        movie.HierarchyLevel.Should().Be(1);
    }

    [Fact]
    public async Task EnsureCollectionParentAsync_CollectionAlreadyExists_DoesNotDuplicate()
    {
        await using var db = CreateInMemoryDb();
        var mt = MoviesType();
        db.MediaTypes.Add(mt);
        var collection = new MediaItem
        {
            Id = 10, Name = "Inception Collection", MediaTypeId = mt.Id, MediaType = mt,
            HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(collection);
        db.MediaExternalIds.Add(new MediaExternalId
        {
            MediaItemId = 10, Source = "tmdb", ExternalId = "collection:748"
        });
        const string metadataJson = """
        {"chronicle.plugin.tmdb":{"belongsToCollection":{"id":748,"name":"Inception Collection","posterPath":null}}}
        """;
        var movie = new MediaItem
        {
            Id = 1, Name = "Inception", MediaTypeId = mt.Id, MediaType = mt,
            HierarchyLevel = 0, MetadataJson = metadataJson, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(movie);
        await db.SaveChangesAsync();

        var svc = new MovieCollectionService(NullLogger<MovieCollectionService>.Instance);
        await svc.EnsureCollectionParentAsync(db, movie);

        // No duplicate collection items
        var collections = await db.MediaItems.Where(m => m.Name == "Inception Collection").ToListAsync();
        collections.Should().HaveCount(1);

        // Movie parented to the existing collection
        movie.ParentId.Should().Be(10);
    }

    [Fact]
    public async Task EnsureCollectionParentAsync_NonMoviesType_IsNoOp()
    {
        await using var db = CreateInMemoryDb();
        var mt = new MediaType
        {
            Id = 2, Name = "tv", DisplayName = "TV Shows",
            HierarchyLevels = 3, CreatedAt = DateTime.UtcNow
        };
        db.MediaTypes.Add(mt);
        var item = new MediaItem
        {
            Id = 1, Name = "Some Show", MediaTypeId = mt.Id, MediaType = mt,
            HierarchyLevel = 0,
            MetadataJson = """{"chronicle.plugin.tmdb":{"belongsToCollection":{"id":1,"name":"Some Collection","posterPath":null}}}""",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync();

        var svc = new MovieCollectionService(NullLogger<MovieCollectionService>.Instance);
        await svc.EnsureCollectionParentAsync(db, item);

        // Should not create any collection items
        db.MediaItems.Count().Should().Be(1);
        item.ParentId.Should().BeNull();
    }
}
