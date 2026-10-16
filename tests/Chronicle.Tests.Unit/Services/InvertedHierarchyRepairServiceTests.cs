using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Chronicle.Tests.Unit.Services;

public class InvertedHierarchyRepairServiceTests
{
    private const int MusicTypeId = 3;

    private static string Scanned(string folder) =>
        JsonSerializer.Serialize(new { fileScanner = new { filePaths = Array.Empty<string>(), folderPath = folder } });

    private static string Folder(params string[] parts) => Path.Combine(["E:", .. parts]);

    private sealed class Rig : IDisposable
    {
        public ChronicleDbContext Db { get; } = new(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Mock<IFileScanService> FileScan { get; } = new();
        private readonly ServiceProvider _provider;
        public InvertedHierarchyRepairService Service { get; }

        public Rig()
        {
            Db.MediaTypes.Add(new MediaType { Id = MusicTypeId, Name = "music", DisplayName = "Music", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow });
            Db.SaveChanges();
            var services = new ServiceCollection();
            services.AddSingleton(Db);
            services.AddSingleton(FileScan.Object);
            _provider = services.BuildServiceProvider();
            Service = new InvertedHierarchyRepairService(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<InvertedHierarchyRepairService>.Instance);
        }

        public MediaItem Add(string name, int level, int? parentId = null, string? json = null)
        {
            var item = new MediaItem
            {
                MediaTypeId = MusicTypeId, Name = name, HierarchyLevel = level, ParentId = parentId, MetadataJson = json,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            Db.MediaItems.Add(item);
            Db.SaveChanges();
            return item;
        }

        public void Dispose() { _provider.Dispose(); Db.Dispose(); }
    }

    /// <summary>Another artist's album track called "Genesis", with the band's albums wrongly filed under it.</summary>
    private static (MediaItem Track, MediaItem[] Albums) WrongParentShape(Rig rig)
    {
        var devin = rig.Add("Devin Townsend", 0);
        var devinAlbum = rig.Add("Genesis", 1, devin.Id);
        var track = rig.Add("Genesis", 2, devinAlbum.Id);
        var albums = new[]
        {
            rig.Add("Foxtrot", 1, track.Id, Scanned(Folder("Music", "Genesis", "Foxtrot"))),
            rig.Add("Duke", 1, track.Id, Scanned(Folder("Music", "Genesis", "Duke"))),
        };
        return (track, albums);
    }

    [Fact]
    public async Task AlbumsUnderATrackNamedLikeTheirArtistFolder_MoveUnderANewArtist_WhichIsSeededForEnrichment()
    {
        using var rig = new Rig();
        var (track, albums) = WrongParentShape(rig);

        await rig.Service.ExecuteAsync(CancellationToken.None);

        var artist = await rig.Db.MediaItems.SingleAsync(m => m.HierarchyLevel == 0 && m.Name == "Genesis");
        artist.ParentId.Should().BeNull();
        JsonDocument.Parse(artist.MetadataJson!).RootElement.GetProperty("fileScanner").GetProperty("folderPath").GetString()
            .Should().Be(Folder("Music", "Genesis"));
        foreach (var album in albums)
            (await rig.Db.MediaItems.FindAsync(album.Id))!.ParentId.Should().Be(artist.Id);
        (await rig.Db.MediaItems.FindAsync(track.Id))!.HierarchyLevel.Should().Be(2, "the track itself is not touched");
        rig.FileScan.Verify(f => f.SeedPendingEnrichmentAsync(It.Is<List<int>>(l => l.Single() == artist.Id), "music", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AnExistingRootOfThatName_IsReused_AndRunningAgainChangesNothing()
    {
        using var rig = new Rig();
        var (_, albums) = WrongParentShape(rig);
        var existing = rig.Add("genesis", 0, null);   // a different capitalisation of the same name

        await rig.Service.ExecuteAsync(CancellationToken.None);
        await rig.Service.ExecuteAsync(CancellationToken.None);

        (await rig.Db.MediaItems.CountAsync(m => m.HierarchyLevel == 0 && m.Name.ToLower() == "genesis")).Should().Be(1);
        foreach (var album in albums)
            (await rig.Db.MediaItems.FindAsync(album.Id))!.ParentId.Should().Be(existing.Id);
        rig.FileScan.Verify(f => f.SeedPendingEnrichmentAsync(It.IsAny<List<int>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ChildrenWhoseFoldersDoNotCarryTheParentsName_AreLeftAlone()
    {
        using var rig = new Rig();
        var devin = rig.Add("Devin Townsend", 0);
        var album = rig.Add("Some Album", 1, devin.Id);
        var episode = rig.Add("Episode 5", 2, album.Id);
        var season = rig.Add("Snatch Me If You Can", 1, episode.Id, Scanned(Folder("TV", "Some Show", "Season 1")));

        await rig.Service.ExecuteAsync(CancellationToken.None);

        (await rig.Db.MediaItems.FindAsync(season.Id))!.ParentId.Should().Be(episode.Id);
        (await rig.Db.MediaItems.CountAsync(m => m.HierarchyLevel == 0)).Should().Be(1);
    }

    [Fact]
    public async Task ASeasonAProviderCreatedUnderAnEpisode_MovesUpToTheShow_WhenTheEpisodeSitsDirectlyUnderIt()
    {
        using var rig = new Rig();
        var show = rig.Add("Some Reality Show", 0);
        var episode = rig.Add("Snatch Me If You Can", 2, show.Id);
        var season = rig.Add("Season 8", 1, episode.Id, "{\"_resolved\":{}}");

        await rig.Service.ExecuteAsync(CancellationToken.None);

        (await rig.Db.MediaItems.FindAsync(season.Id))!.ParentId.Should().Be(show.Id);
        (await rig.Db.MediaItems.FindAsync(episode.Id))!.ParentId.Should().Be(show.Id, "the episode stays where it was");
    }

    [Fact]
    public async Task ChildrenInDifferentFolders_AreLeftAlone()
    {
        using var rig = new Rig();
        var (track, albums) = WrongParentShape(rig);
        albums[1].MetadataJson = Scanned(Folder("Music", "Various", "Duke"));
        rig.Db.SaveChanges();

        await rig.Service.ExecuteAsync(CancellationToken.None);

        (await rig.Db.MediaItems.FindAsync(albums[0].Id))!.ParentId.Should().Be(track.Id);
        (await rig.Db.MediaItems.CountAsync(m => m.HierarchyLevel == 0)).Should().Be(1);
    }

    [Fact]
    public async Task TwoRootsOfTheSameName_IsAmbiguous_AndLeftAlone()
    {
        using var rig = new Rig();
        var (track, albums) = WrongParentShape(rig);
        rig.Add("Genesis", 0);
        rig.Add("GENESIS", 0);

        await rig.Service.ExecuteAsync(CancellationToken.None);

        (await rig.Db.MediaItems.FindAsync(albums[0].Id))!.ParentId.Should().Be(track.Id);
    }
}
