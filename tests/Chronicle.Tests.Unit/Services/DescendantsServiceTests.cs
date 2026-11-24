using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// Whatever the user adds by hand is added with everything beneath it: an artist's albums and their tracks, an author's
/// series and their books. Which types have anything beneath them comes from the type's own hierarchy, not from a name.
/// </summary>
public class DescendantsServiceTests
{
    private const string PluginId = "chronicle.plugin.fake";

    private sealed class Fixture
    {
        public ChronicleDbContext Db { get; } = new(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Mock<IMetadataProvider> Provider { get; } = new();
        public Mock<IFileScanService> FileScan { get; } = new();
        public Mock<IShowStructureService> Shows { get; } = new();
        public DescendantsService Service { get; }

        public Fixture()
        {
            RegistryFixtures.LoadBuiltIns();
            Db.MediaTypes.AddRange(
                new MediaType { Id = 1, Name = "music", DisplayName = "Music", HierarchyLevels = 3, ImportShape = "artist-album-track", CreatedAt = DateTime.UtcNow },
                new MediaType { Id = 2, Name = "movies", DisplayName = "Movies", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow },
                new MediaType { Id = 3, Name = "tv", DisplayName = "TV", HierarchyLevels = 3, ImportShape = "episodic", CreatedAt = DateTime.UtcNow },
                new MediaType { Id = 4, Name = "books", DisplayName = "Books", HierarchyLevels = 3, ImportShape = "author-series-work", CreatedAt = DateTime.UtcNow });
            Db.SaveChanges();

            var registry = new Mock<IPluginRegistry>();
            registry.Setup(r => r.GetMetadataProvider(PluginId)).Returns(Provider.Object);
            registry.Setup(r => r.GetMetadataProviderEntries())
                .Returns([(PluginId, Provider.Object, (string?)null)]);

            var media = new MediaService(Db, registry.Object);
            Service = new DescendantsService(Db, media, registry.Object, Shows.Object, FileScan.Object,
                NullLogger<DescendantsService>.Instance);
        }

        public MediaItem AddRoot(int typeId, string name, string externalId)
        {
            var item = new MediaItem { MediaTypeId = typeId, Name = name, HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Db.MediaItems.Add(item);
            Db.SaveChanges();
            Db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = item.Id, Source = "fake", ExternalId = externalId });
            Db.SaveChanges();
            return item;
        }

        public void Provides(string externalId, int level, params ProviderChild[] children) =>
            Provider.Setup(p => p.GetChildrenAsync(externalId, level, It.IsAny<CancellationToken>()))
                .ReturnsAsync(children);
    }

    [Fact]
    public async Task AnArtistBringsItsAlbumsAndTheirTracks()
    {
        var f = new Fixture();
        var artist = f.AddRoot(1, "Artist", "artist:1");
        f.Provides("artist:1", 0, new ProviderChild("First", 1, "release-group:a", 2001), new ProviderChild("Second", 2, "release-group:b", 2003));
        f.Provides("release-group:a", 1, new ProviderChild("A1", 1), new ProviderChild("A2", 2));
        f.Provides("release-group:b", 1, new ProviderChild("B1", 1));

        var created = await f.Service.EnsureDescendantsAsync(artist.Id);

        Assert.Equal(5, created);
        var albums = await f.Db.MediaItems.Where(m => m.ParentId == artist.Id).OrderBy(m => m.Number).ToListAsync();
        Assert.Equal(["First", "Second"], albums.Select(a => a.Name));
        Assert.All(albums, a => Assert.Equal(1, a.HierarchyLevel));
        var tracks = await f.Db.MediaItems.Where(m => m.ParentId == albums[0].Id).OrderBy(m => m.Number).ToListAsync();
        Assert.Equal(["A1", "A2"], tracks.Select(t => t.Name));
        Assert.All(tracks, t => Assert.Equal(2, t.HierarchyLevel));
        Assert.Contains(f.Db.MediaExternalIds, e => e.MediaItemId == albums[0].Id && e.ExternalId == "release-group:a");
        f.FileScan.Verify(s => s.SeedPendingEnrichmentAsync(It.Is<List<int>>(l => l.Count == 5), "music", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunningItAgainCreatesNothingNew()
    {
        var f = new Fixture();
        var artist = f.AddRoot(1, "Artist", "artist:1");
        f.Provides("artist:1", 0, new ProviderChild("First", 1, "release-group:a"));
        f.Provides("release-group:a", 1, new ProviderChild("A1", 1));

        await f.Service.EnsureDescendantsAsync(artist.Id);
        var second = await f.Service.EnsureDescendantsAsync(artist.Id);

        Assert.Equal(0, second);
        Assert.Equal(2, await f.Db.MediaItems.CountAsync(m => m.ParentId != null));
    }

    [Fact]
    public async Task WhatTheLibraryAlreadyHoldsIsKeptNotDuplicated()
    {
        var f = new Fixture();
        var artist = f.AddRoot(1, "Artist", "artist:1");
        var album = new MediaItem { MediaTypeId = 1, ParentId = artist.Id, Name = "First", HierarchyLevel = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        f.Db.MediaItems.Add(album);
        await f.Db.SaveChangesAsync();
        f.Provides("artist:1", 0, new ProviderChild("First", 1, "release-group:a"), new ProviderChild("Second", 2, "release-group:b"));

        var created = await f.Service.EnsureDescendantsAsync(artist.Id);

        Assert.Equal(1, created);
        Assert.Single(await f.Db.MediaItems.Where(m => m.Name == "First").ToListAsync());
    }

    [Fact]
    public async Task AnAuthorBringsItsSeriesAndStandaloneBooks()
    {
        var f = new Fixture();
        var author = f.AddRoot(4, "Author", "hardcover:author:1");
        f.Provides("hardcover:author:1", 0,
            new ProviderChild("The Series", null, "hardcover:series:9"),
            new ProviderChild("Lone Book", null, "hardcover:5", 2010));
        f.Provides("hardcover:series:9", 1, new ProviderChild("Book One", 1, "hardcover:11"), new ProviderChild("Book Two", 2, "hardcover:12"));

        var created = await f.Service.EnsureDescendantsAsync(author.Id);

        Assert.Equal(4, created);
        var series = await f.Db.MediaItems.SingleAsync(m => m.Name == "The Series");
        Assert.Equal(2, await f.Db.MediaItems.CountAsync(m => m.ParentId == series.Id));
        Assert.Equal(author.Id, (await f.Db.MediaItems.SingleAsync(m => m.Name == "Lone Book")).ParentId);
    }

    [Fact]
    public async Task ATypeWithNothingBeneathItIsLeftAlone()
    {
        var f = new Fixture();
        var movie = f.AddRoot(2, "Movie", "movie:1");

        Assert.Equal(0, await f.Service.EnsureDescendantsAsync(movie.Id));
        f.Provider.Verify(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AShowGoesThroughItsEpisodeGuide()
    {
        var f = new Fixture();
        var show = f.AddRoot(3, "Show", "tv:1");

        await f.Service.EnsureDescendantsAsync(show.Id);

        f.Shows.Verify(s => s.EnsureEpisodesResolvedAsync(It.Is<MediaItem>(m => m.Id == show.Id), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AProviderThatCannotAnswerLeavesTheItemAsItWas()
    {
        var f = new Fixture();
        var artist = f.AddRoot(1, "Artist", "artist:1");
        f.Provider.Setup(p => p.GetChildrenAsync("artist:1", 0, It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("down"));

        Assert.Equal(0, await f.Service.EnsureDescendantsAsync(artist.Id));
        Assert.Empty(await f.Db.MediaItems.Where(m => m.ParentId != null).ToListAsync());
    }
}
