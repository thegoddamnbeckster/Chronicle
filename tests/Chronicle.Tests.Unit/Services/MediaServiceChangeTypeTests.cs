using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using static FluentAssertions.FluentActions;

namespace Chronicle.Tests.Unit.Services;

public class MediaServiceChangeTypeTests
{
    private static ChronicleDbContext MakeDb(string name)
    {
        var opts = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new ChronicleDbContext(opts);
    }

    /// <summary>
    /// Creates a MediaService with an empty plugin registry (no metadata providers).
    /// Use when the test doesn't care about enrichment row seeding.
    /// </summary>
    private static MediaService MakeService(ChronicleDbContext db)
    {
        var registry = new Mock<IPluginRegistry>();
        registry.Setup(r => r.GetMetadataProviderEntries())
                .Returns([]);
        return new MediaService(db, registry.Object);
    }

    /// <summary>
    /// Creates a MediaService with a mock registry that includes one provider
    /// supporting the given media type names.
    /// </summary>
    private static MediaService MakeServiceWithProvider(
        ChronicleDbContext db, string pluginId, params string[] supportedTypes)
    {
        var provider = new Mock<IMetadataProvider>();
        provider.Setup(p => p.GetSupportedMediaTypes())
                .Returns(supportedTypes.Select(t => new MediaTypeSupport { MediaTypeName = t }).ToArray());

        var registry = new Mock<IPluginRegistry>();
        registry.Setup(r => r.GetMetadataProviderEntries())
                .Returns([(pluginId, provider.Object, (string?)null)]);
        return new MediaService(db, registry.Object);
    }

    private static async Task<(MediaType movies, MediaType fanedits)> SeedTypesAsync(ChronicleDbContext db)
    {
        var movies   = new MediaType { Id = 1, Name = "movies",   DisplayName = "Movies",    HierarchyLevels = 1 };
        var fanedits = new MediaType { Id = 4, Name = "fanedits", DisplayName = "Fan Edits", HierarchyLevels = 1 };
        var tv       = new MediaType { Id = 2, Name = "tv",       DisplayName = "TV",        HierarchyLevels = 3 };
        db.Set<MediaType>().AddRange(movies, fanedits, tv);
        await db.SaveChangesAsync();
        return (movies, fanedits);
    }

    [Fact]
    public async Task ChangeTypeAsync_UpdatesMediaTypeId_OnFlatItem()
    {
        await using var db = MakeDb(nameof(ChangeTypeAsync_UpdatesMediaTypeId_OnFlatItem));
        var (movies, fanedits) = await SeedTypesAsync(db);
        var item = new MediaItem { MediaTypeId = movies.Id, Name = "Blade Runner Fan Edit", HierarchyLevel = 0 };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync();

        await MakeService(db).ChangeTypeAsync(item.Id, fanedits.Id);

        var updated = await db.MediaItems.FindAsync(item.Id);
        updated!.MediaTypeId.Should().Be(fanedits.Id);
    }

    [Fact]
    public async Task ChangeTypeAsync_ClearsMetadataAndExternalIds()
    {
        await using var db = MakeDb(nameof(ChangeTypeAsync_ClearsMetadataAndExternalIds));
        var (movies, fanedits) = await SeedTypesAsync(db);
        var item = new MediaItem { MediaTypeId = movies.Id, Name = "Test", HierarchyLevel = 0,
                                   MetadataJson = "{\"tmdb\":{}}" };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync();
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = item.Id, Source = "tmdb", ExternalId = "movie:550" });
        db.MediaEnrichments.Add(new MediaItemEnrichment { MediaItemId = item.Id, PluginId = "chronicle.plugin.tmdb",
                                                           Status = EnrichmentStatus.Completed });
        await db.SaveChangesAsync();

        // Registry returns no metadata providers → old rows cleared, no new rows seeded.
        await MakeService(db).ChangeTypeAsync(item.Id, fanedits.Id);

        var updated = await db.MediaItems.FindAsync(item.Id);
        updated!.MetadataJson.Should().BeNull();
        db.MediaExternalIds.Where(e => e.MediaItemId == item.Id).Should().BeEmpty();
        // Old TMDB row is gone; no new rows because registry is empty.
        db.MediaEnrichments.Where(e => e.MediaItemId == item.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task ChangeTypeAsync_SeedsPendingEnrichmentRows_ForSupportingPlugins()
    {
        await using var db = MakeDb(nameof(ChangeTypeAsync_SeedsPendingEnrichmentRows_ForSupportingPlugins));
        var (movies, fanedits) = await SeedTypesAsync(db);
        var item = new MediaItem { MediaTypeId = movies.Id, Name = "Blade Runner Fan Edit", HierarchyLevel = 0 };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync();

        // Plugin supports "fanedits" — should get a Pending row after type change.
        var svc = MakeServiceWithProvider(db, "chronicle.plugin.fanedit", "fanedits");
        await svc.ChangeTypeAsync(item.Id, fanedits.Id);

        var rows = db.MediaEnrichments.Where(e => e.MediaItemId == item.Id).ToList();
        rows.Should().HaveCount(1);
        rows[0].PluginId.Should().Be("chronicle.plugin.fanedit");
        rows[0].Status.Should().Be(EnrichmentStatus.Pending);
    }

    [Fact]
    public async Task ChangeTypeAsync_DoesNotSeedRows_ForNonSupportingPlugins()
    {
        await using var db = MakeDb(nameof(ChangeTypeAsync_DoesNotSeedRows_ForNonSupportingPlugins));
        var (movies, fanedits) = await SeedTypesAsync(db);
        var item = new MediaItem { MediaTypeId = movies.Id, Name = "Test", HierarchyLevel = 0 };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync();

        // Plugin only supports "music" — should NOT get a Pending row when changing to fanedits.
        var svc = MakeServiceWithProvider(db, "chronicle.plugin.musicbrainz", "music");
        await svc.ChangeTypeAsync(item.Id, fanedits.Id);

        db.MediaEnrichments.Where(e => e.MediaItemId == item.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task ChangeTypeAsync_CascadesToDescendants()
    {
        await using var db = MakeDb(nameof(ChangeTypeAsync_CascadesToDescendants));
        var typeA = new MediaType { Id = 2, Name = "tv",    DisplayName = "TV",    HierarchyLevels = 3 };
        var typeB = new MediaType { Id = 5, Name = "other", DisplayName = "Other", HierarchyLevels = 3 };
        db.Set<MediaType>().AddRange(typeA, typeB);
        await db.SaveChangesAsync();

        var show = new MediaItem { MediaTypeId = typeA.Id, Name = "Show", HierarchyLevel = 0 };
        db.MediaItems.Add(show); await db.SaveChangesAsync();
        var season = new MediaItem { MediaTypeId = typeA.Id, Name = "S1", HierarchyLevel = 1, ParentId = show.Id };
        db.MediaItems.Add(season); await db.SaveChangesAsync();
        var episode = new MediaItem { MediaTypeId = typeA.Id, Name = "S1E1", HierarchyLevel = 2, ParentId = season.Id };
        db.MediaItems.Add(episode); await db.SaveChangesAsync();

        await MakeService(db).ChangeTypeAsync(show.Id, typeB.Id);

        (await db.MediaItems.FindAsync(show.Id))!.MediaTypeId.Should().Be(typeB.Id);
        (await db.MediaItems.FindAsync(season.Id))!.MediaTypeId.Should().Be(typeB.Id);
        (await db.MediaItems.FindAsync(episode.Id))!.MediaTypeId.Should().Be(typeB.Id);
    }

    [Fact]
    public async Task ChangeTypeAsync_ThrowsInvalidOperation_WhenChildItem()
    {
        await using var db = MakeDb(nameof(ChangeTypeAsync_ThrowsInvalidOperation_WhenChildItem));
        var (movies, fanedits) = await SeedTypesAsync(db);
        var parent = new MediaItem { MediaTypeId = movies.Id, Name = "Parent", HierarchyLevel = 0 };
        db.MediaItems.Add(parent); await db.SaveChangesAsync();
        var child = new MediaItem { MediaTypeId = movies.Id, Name = "Child", HierarchyLevel = 1, ParentId = parent.Id };
        db.MediaItems.Add(child); await db.SaveChangesAsync();

        await Invoking(() => MakeService(db).ChangeTypeAsync(child.Id, fanedits.Id))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*root*");
    }

    [Fact]
    public async Task ChangeTypeAsync_ThrowsInvalidOperation_WhenIncompatibleHierarchy()
    {
        await using var db = MakeDb(nameof(ChangeTypeAsync_ThrowsInvalidOperation_WhenIncompatibleHierarchy));
        var tv       = new MediaType { Id = 2, Name = "tv",       DisplayName = "TV",       HierarchyLevels = 3 };
        var fanedits = new MediaType { Id = 4, Name = "fanedits", DisplayName = "Fan Edits", HierarchyLevels = 1 };
        db.Set<MediaType>().AddRange(tv, fanedits);
        await db.SaveChangesAsync();
        var show   = new MediaItem { MediaTypeId = tv.Id, Name = "Show", HierarchyLevel = 0 };
        db.MediaItems.Add(show); await db.SaveChangesAsync();
        var season = new MediaItem { MediaTypeId = tv.Id, Name = "S1",   HierarchyLevel = 1, ParentId = show.Id };
        db.MediaItems.Add(season); await db.SaveChangesAsync();

        await Invoking(() => MakeService(db).ChangeTypeAsync(show.Id, fanedits.Id))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*incompatible*");
    }
}
