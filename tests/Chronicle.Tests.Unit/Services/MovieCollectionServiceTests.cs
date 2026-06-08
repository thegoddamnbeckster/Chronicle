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
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
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

    [Fact]
    public async Task EnsureCollectionParentAsync_HasParentButNoCollectionData_ResetsToRoot()
    {
        await using var db = CreateInMemoryDb();
        var mt = MoviesType();
        db.MediaTypes.Add(mt);

        // Pre-existing orphan collection container
        var collection = new MediaItem
        {
            Id = 99, Name = "Old Collection", MediaTypeId = mt.Id, MediaType = mt,
            HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(collection);

        // Movie currently parented under that collection, but metadata no longer has collection data
        var movie = new MediaItem
        {
            Id = 1, Name = "Inception", MediaTypeId = mt.Id, MediaType = mt,
            ParentId = 99, HierarchyLevel = 1,
            MetadataJson = """{"chronicle.plugin.tmdb": {"title": "Inception"}}""",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(movie);
        await db.SaveChangesAsync();

        var svc = new MovieCollectionService(NullLogger<MovieCollectionService>.Instance);
        await svc.EnsureCollectionParentAsync(db, movie);

        movie.ParentId.Should().BeNull();
        movie.HierarchyLevel.Should().Be(0);
    }

    [Fact]
    public async Task EnsureCollectionParentAsync_NameFallback_ParentsToExistingCollectionAndCrossLinks()
    {
        await using var db = CreateInMemoryDb();
        var mt = MoviesType();
        db.MediaTypes.Add(mt);

        // Existing collection that has one child (qualifying it as a real collection container)
        var collection = new MediaItem
        {
            Id = 10, Name = "Inception Collection", MediaTypeId = mt.Id, MediaType = mt,
            HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(collection);
        var existingChild = new MediaItem
        {
            Id = 2, Name = "Inception", MediaTypeId = mt.Id, MediaType = mt,
            ParentId = 10, HierarchyLevel = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(existingChild);
        // No MediaExternalId for the collection — forces the name-fallback path

        const string metadataJson = """
        {
          "chronicle.plugin.tmdb": {
            "belongsToCollection": { "id": 748, "name": "Inception Collection", "posterPath": null }
          }
        }
        """;
        var movie2 = new MediaItem
        {
            Id = 3, Name = "Inception 2", MediaTypeId = mt.Id, MediaType = mt,
            HierarchyLevel = 0, MetadataJson = metadataJson,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(movie2);
        await db.SaveChangesAsync();

        var svc = new MovieCollectionService(NullLogger<MovieCollectionService>.Instance);
        await svc.EnsureCollectionParentAsync(db, movie2);

        // Should be parented to the existing collection (not a new one)
        movie2.ParentId.Should().Be(10);
        db.MediaItems.Count(m => m.Name == "Inception Collection").Should().Be(1);

        // ExternalId should have been cross-linked
        var extId = await db.MediaExternalIds.FirstOrDefaultAsync(
            e => e.MediaItemId == 10 && e.Source == "tmdb");
        extId.Should().NotBeNull();
        extId!.ExternalId.Should().Be("collection:748");
    }

    [Fact]
    public async Task EnsureCollectionParentAsync_NameFallback_DoesNotDuplicateExternalId_OnSecondCall()
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
        var existingChild = new MediaItem
        {
            Id = 2, Name = "Inception", MediaTypeId = mt.Id, MediaType = mt,
            ParentId = 10, HierarchyLevel = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(existingChild);

        const string metadataJson = """
        {"chronicle.plugin.tmdb":{"belongsToCollection":{"id":748,"name":"Inception Collection","posterPath":null}}}
        """;
        var movie2 = new MediaItem
        {
            Id = 3, Name = "Inception 2", MediaTypeId = mt.Id, MediaType = mt,
            HierarchyLevel = 0, MetadataJson = metadataJson,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(movie2);
        await db.SaveChangesAsync();

        var svc = new MovieCollectionService(NullLogger<MovieCollectionService>.Instance);
        // Call twice — second call should not insert a duplicate ExternalId row
        await svc.EnsureCollectionParentAsync(db, movie2);
        await svc.EnsureCollectionParentAsync(db, movie2);

        var extIds = await db.MediaExternalIds
            .Where(e => e.MediaItemId == 10 && e.Source == "tmdb")
            .ToListAsync();
        extIds.Should().HaveCount(1);
    }

    [Fact]
    public async Task EnsureCollectionParentAsync_UpdatesPosterUrlWhenChanged()
    {
        await using var db = CreateInMemoryDb();
        var mt = MoviesType();
        db.MediaTypes.Add(mt);

        var collection = new MediaItem
        {
            Id = 10, Name = "Inception Collection", MediaTypeId = mt.Id, MediaType = mt,
            HierarchyLevel = 0, PosterUrl = null,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(collection);
        db.MediaExternalIds.Add(new MediaExternalId
        {
            MediaItemId = 10, Source = "tmdb", ExternalId = "collection:748"
        });
        const string metadataJson = """
        {"chronicle.plugin.tmdb":{"belongsToCollection":{"id":748,"name":"Inception Collection","posterPath":"https://img/new.jpg"}}}
        """;
        var movie = new MediaItem
        {
            Id = 1, Name = "Inception", MediaTypeId = mt.Id, MediaType = mt,
            HierarchyLevel = 0, MetadataJson = metadataJson,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(movie);
        await db.SaveChangesAsync();

        var svc = new MovieCollectionService(NullLogger<MovieCollectionService>.Instance);
        await svc.EnsureCollectionParentAsync(db, movie);

        collection.PosterUrl.Should().Be("https://img/new.jpg");
    }

    [Fact]
    public async Task EnsureCollectionParentAsync_RemovesOrphanedOldCollection_WhenMovieMovesToNewCollection()
    {
        await using var db = CreateInMemoryDb();
        var mt = MoviesType();
        db.MediaTypes.Add(mt);

        // Old collection — currently has one child (the movie we're moving)
        var oldCollection = new MediaItem
        {
            Id = 10, Name = "Old Collection", MediaTypeId = mt.Id, MediaType = mt,
            HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(oldCollection);
        db.MediaExternalIds.Add(new MediaExternalId
            { MediaItemId = 10, Source = "tmdb", ExternalId = "collection:999" });

        // New collection (already exists from a previous enrichment)
        var newCollection = new MediaItem
        {
            Id = 20, Name = "New Collection", MediaTypeId = mt.Id, MediaType = mt,
            HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(newCollection);
        db.MediaExternalIds.Add(new MediaExternalId
            { MediaItemId = 20, Source = "tmdb", ExternalId = "collection:748" });

        // Movie currently under old collection
        const string metadataJson = """
        {"chronicle.plugin.tmdb":{"belongsToCollection":{"id":748,"name":"New Collection","posterPath":null}}}
        """;
        var movie = new MediaItem
        {
            Id = 1, Name = "Inception", MediaTypeId = mt.Id, MediaType = mt,
            ParentId = 10, HierarchyLevel = 1, MetadataJson = metadataJson,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(movie);
        await db.SaveChangesAsync();

        var svc = new MovieCollectionService(NullLogger<MovieCollectionService>.Instance);
        await svc.EnsureCollectionParentAsync(db, movie);

        // Movie should now be under the new collection
        movie.ParentId.Should().Be(20);

        // Old collection should have been removed (it has no remaining children)
        var orphan = await db.MediaItems.FindAsync(10);
        orphan.Should().BeNull();
    }

    // ── ExtractCollectionData: plugin-agnostic unit tests ─────────────────────

    [Fact]
    public void ExtractCollectionData_TmdbNumericId_ReturnsCorrectSource()
    {
        const string json = """
        {
          "chronicle.plugin.tmdb": {
            "belongsToCollection": { "id": 748, "name": "Inception Collection", "posterPath": "https://img/p.jpg" }
          }
        }
        """;

        var result = MovieCollectionService.ExtractCollectionData(json);

        result.Should().NotBeNull();
        result!.Id.Should().Be("748");
        result.Name.Should().Be("Inception Collection");
        result.PosterUrl.Should().Be("https://img/p.jpg");
        result.Source.Should().Be("tmdb");
    }

    [Fact]
    public void ExtractCollectionData_NonTmdbPlugin_ReturnsCorrectSource()
    {
        // A hypothetical future plugin that stores collection data in the same shape
        const string json = """
        {
          "chronicle.plugin.someplugin": {
            "belongsToCollection": { "id": "col-abc-123", "name": "Some Saga", "posterPath": null }
          }
        }
        """;

        var result = MovieCollectionService.ExtractCollectionData(json);

        result.Should().NotBeNull();
        result!.Id.Should().Be("col-abc-123");
        result.Name.Should().Be("Some Saga");
        result.PosterUrl.Should().BeNull();
        result.Source.Should().Be("someplugin");
    }

    [Fact]
    public void ExtractCollectionData_MultiplePlugins_ReturnsFirstWithCollection()
    {
        // Only the second plugin has collection data — should still be found
        const string json = """
        {
          "chronicle.plugin.fanart": { "logoUrl": "https://img/logo.png" },
          "chronicle.plugin.tmdb": {
            "belongsToCollection": { "id": 10, "name": "Test Collection", "posterPath": null }
          }
        }
        """;

        var result = MovieCollectionService.ExtractCollectionData(json);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Test Collection");
        result.Source.Should().Be("tmdb");
    }

    [Fact]
    public void ExtractCollectionData_NullCollection_ReturnsNull()
    {
        const string json = """
        { "chronicle.plugin.tmdb": { "belongsToCollection": null } }
        """;

        MovieCollectionService.ExtractCollectionData(json).Should().BeNull();
    }

    [Fact]
    public void ExtractCollectionData_NoCollection_ReturnsNull()
    {
        const string json = """{ "chronicle.plugin.tmdb": { "title": "Inception" } }""";

        MovieCollectionService.ExtractCollectionData(json).Should().BeNull();
    }

    [Fact]
    public async Task EnsureCollectionParentAsync_NonTmdbPlugin_CreatesCollectionWithCorrectSource()
    {
        await using var db = CreateInMemoryDb();
        var mt = MoviesType();
        db.MediaTypes.Add(mt);
        const string metadataJson = """
        {
          "chronicle.plugin.someplugin": {
            "belongsToCollection": { "id": "saga-001", "name": "Some Saga", "posterPath": null }
          }
        }
        """;
        var movie = new MediaItem
        {
            Id = 1, Name = "Some Movie", MediaTypeId = mt.Id, MediaType = mt,
            HierarchyLevel = 0, MetadataJson = metadataJson,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(movie);
        await db.SaveChangesAsync();

        var svc = new MovieCollectionService(NullLogger<MovieCollectionService>.Instance);
        await svc.EnsureCollectionParentAsync(db, movie);

        var collection = await db.MediaItems.FirstOrDefaultAsync(m => m.Name == "Some Saga");
        collection.Should().NotBeNull();

        var extId = await db.MediaExternalIds.FirstOrDefaultAsync(e => e.MediaItemId == collection!.Id);
        extId.Should().NotBeNull();
        extId!.ExternalId.Should().Be("collection:saga-001");
        extId.Source.Should().Be("someplugin");

        movie.ParentId.Should().Be(collection!.Id);
    }
}
