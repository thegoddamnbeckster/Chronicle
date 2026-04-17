using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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

    private static MediaService MakeService(ChronicleDbContext db)
        => new(db);

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
        var item = new MediaItem { MediaTypeId = 1, Name = "Blade Runner Fan Edit", HierarchyLevel = 0 };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync();

        var svc = MakeService(db);
        await svc.ChangeTypeAsync(item.Id, fanedits.Id);

        var updated = await db.MediaItems.FindAsync(item.Id);
        updated!.MediaTypeId.Should().Be(fanedits.Id);
    }

    [Fact]
    public async Task ChangeTypeAsync_ClearsMetadataAndExternalIds()
    {
        await using var db = MakeDb(nameof(ChangeTypeAsync_ClearsMetadataAndExternalIds));
        var (movies, fanedits) = await SeedTypesAsync(db);
        var item = new MediaItem { MediaTypeId = 1, Name = "Test", HierarchyLevel = 0,
                                   MetadataJson = "{\"tmdb\":{}}" };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync();
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = item.Id, Source = "tmdb", ExternalId = "movie:550" });
        db.MediaEnrichments.Add(new MediaItemEnrichment { MediaItemId = item.Id, PluginId = "chronicle.plugin.tmdb",
                                                           Status = EnrichmentStatus.Completed });
        await db.SaveChangesAsync();

        await MakeService(db).ChangeTypeAsync(item.Id, fanedits.Id);

        var updated = await db.MediaItems.FindAsync(item.Id);
        updated!.MetadataJson.Should().BeNull();
        db.MediaExternalIds.Where(e => e.MediaItemId == item.Id).Should().BeEmpty();
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

        var show    = new MediaItem { MediaTypeId = 2, Name = "Show", HierarchyLevel = 0 };
        db.MediaItems.Add(show); await db.SaveChangesAsync();
        var season  = new MediaItem { MediaTypeId = 2, Name = "S1",   HierarchyLevel = 1, ParentId = show.Id };
        db.MediaItems.Add(season); await db.SaveChangesAsync();
        var episode = new MediaItem { MediaTypeId = 2, Name = "S1E1", HierarchyLevel = 2, ParentId = season.Id };
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
        var parent = new MediaItem { MediaTypeId = 1, Name = "Parent", HierarchyLevel = 0 };
        db.MediaItems.Add(parent); await db.SaveChangesAsync();
        var child  = new MediaItem { MediaTypeId = 1, Name = "Child",  HierarchyLevel = 1, ParentId = parent.Id };
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
        var show   = new MediaItem { MediaTypeId = 2, Name = "Show", HierarchyLevel = 0 };
        db.MediaItems.Add(show); await db.SaveChangesAsync();
        var season = new MediaItem { MediaTypeId = 2, Name = "S1",   HierarchyLevel = 1, ParentId = show.Id };
        db.MediaItems.Add(season); await db.SaveChangesAsync();

        await Invoking(() => MakeService(db).ChangeTypeAsync(show.Id, fanedits.Id))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*incompatible*");
    }
}
