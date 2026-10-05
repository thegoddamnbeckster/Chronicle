using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// Music plays imported from a listening service (Last.fm) must land on the library's own Artist → Album →
/// Track items. Names must match exactly (case-insensitive); only a MusicBrainz recording id matches otherwise.
/// </summary>
public class SyncMusicMatchingTests : IDisposable
{
    private const string PluginId = "chronicle.plugin.lastfm";
    private readonly ChronicleDbContext _db;
    private readonly SyncOrchestrationService _svc;

    public SyncMusicMatchingTests()
    {
        _db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var provider = new Mock<IMetadataProvider>();
        provider.Setup(p => p.GetSupportedMediaTypes())
            .Returns([new MediaTypeSupport { MediaTypeName = "music" }]);
        var registry = new Mock<IPluginRegistry>();
        registry.Setup(r => r.GetMetadataProviderEntries())
            .Returns([("chronicle.plugin.lastfm", provider.Object, (string?)null)]);

        _svc = new SyncOrchestrationService(
            Mock.Of<IServiceScopeFactory>(), registry.Object, Mock.Of<IMetadataResolutionService>(),
            NullLogger<SyncOrchestrationService>.Instance, Mock.Of<IHostApplicationLifetime>());

        if (!_db.MediaTypes.Any(t => t.Name == "music"))
        {
            _db.MediaTypes.Add(new MediaType { Id = 3, Name = "music", DisplayName = "Music", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow });
            _db.SaveChanges();
        }
    }

    public void Dispose() => _db.Dispose();

    private MediaItem Add(string name, int level, MediaItem? parent = null)
    {
        var item = new MediaItem
        {
            Name = name, MediaTypeId = 3, HierarchyLevel = level, ParentId = parent?.Id,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.MediaItems.Add(item);
        _db.SaveChanges();
        return item;
    }

    private static ImportedWatchEvent Play(string artist, string? album, string title,
        Dictionary<string, string>? ids = null) => new(
        ExternalId: $"track:{artist}/{album}/{title}",
        AdditionalIds: ids ?? new Dictionary<string, string>(),
        MediaType: "track", Title: title, Year: null,
        WatchedAt: DateTimeOffset.FromUnixTimeSeconds(1700000000), ProgressPercent: 100,
        ArtistName: artist, AlbumName: album);

    [Fact]
    public async Task ExistingTrack_IsMatched_WhenNamesMatchExactlyIgnoringCase()
    {
        var artist = Add("3 Doors Down", 0);
        var album  = Add("The Better Life", 1, artist);
        var track  = Add("Kryptonite", 2, album);

        var (item, isNew) = await _svc.MatchOrCreateAsync(_db, Play("3 doors down", "the better life", "KRYPTONITE"), PluginId, default);

        isNew.Should().BeFalse();
        item.Id.Should().Be(track.Id);
        (await _db.MediaItems.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task YearPrefixedAlbum_IsNotTheSameAlbum()
    {
        var artist = Add("3 Doors Down", 0);
        var album  = Add("(2000) The Better Life", 1, artist);
        Add("Kryptonite", 2, album);

        var (item, isNew) = await _svc.MatchOrCreateAsync(_db, Play("3 Doors Down", "The Better Life", "Kryptonite"), PluginId, default);

        isNew.Should().BeTrue();
        item.ParentId.Should().NotBe(album.Id);
    }

    [Fact]
    public async Task TrackWithAReleaseQualifier_IsNotTheSameTrack()
    {
        var artist = Add("3 Doors Down", 0);
        var album  = Add("Kryptonite", 1, artist);
        var track  = Add("Kryptonite (LP version)", 2, album);

        var (item, isNew) = await _svc.MatchOrCreateAsync(_db, Play("3 Doors Down", "Kryptonite", "Kryptonite"), PluginId, default);

        isNew.Should().BeTrue();
        item.Id.Should().NotBe(track.Id);
    }

    [Fact]
    public async Task UnknownTrack_CreatesAnArtistAlbumTrackChain_WithPendingEnrichment()
    {
        var (item, isNew) = await _svc.MatchOrCreateAsync(_db, Play("New Band", "First LP", "Opener"), PluginId, default);

        isNew.Should().BeTrue();
        item.HierarchyLevel.Should().Be(2);
        var album  = await _db.MediaItems.SingleAsync(i => i.Id == item.ParentId);
        var artist = await _db.MediaItems.SingleAsync(i => i.Id == album.ParentId);
        (album.Name, album.HierarchyLevel).Should().Be(("First LP", 1));
        (artist.Name, artist.HierarchyLevel, artist.ParentId).Should().Be(("New Band", 0, (int?)null));
        (await _db.MediaEnrichments.CountAsync(e => e.PluginId == PluginId)).Should().Be(3);
    }

    [Fact]
    public async Task SecondPlayOfTheSameNewTrack_ReusesTheChain()
    {
        var first  = await _svc.MatchOrCreateAsync(_db, Play("New Band", "First LP", "Opener"), PluginId, default);
        var second = await _svc.MatchOrCreateAsync(_db, Play("New Band", "First LP", "Opener"), PluginId, default);

        second.isNew.Should().BeFalse();
        second.item.Id.Should().Be(first.item.Id);
        (await _db.MediaItems.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task MusicBrainzRecordingId_MatchesWithoutAnyNameAgreement()
    {
        var artist = Add("Some Band", 0);
        var album  = Add("Some Album", 1, artist);
        var track  = Add("Real Title", 2, album);
        _db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = track.Id, Source = "musicbrainz", ExternalId = "recording:abc" });
        await _db.SaveChangesAsync();

        var (item, isNew) = await _svc.MatchOrCreateAsync(
            _db, Play("Totally Different", "Other", "Renamed", new() { ["musicbrainz"] = "recording:abc" }), PluginId, default);

        isNew.Should().BeFalse();
        item.Id.Should().Be(track.Id);
        (await _db.MediaItems.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task PlayWithNoAlbum_FindsTheTrackUnderTheArtistsRealAlbum()
    {
        var artist = Add("Some Band", 0);
        var album  = Add("Some Album", 1, artist);
        var track  = Add("Deep Cut", 2, album);

        var (item, isNew) = await _svc.MatchOrCreateAsync(_db, Play("Some Band", null, "Deep Cut"), PluginId, default);

        isNew.Should().BeFalse();
        item.Id.Should().Be(track.Id);
        (await _db.MediaItems.AnyAsync(i => i.Name == SyncOrchestrationService.UnknownAlbumName)).Should().BeFalse();
    }

    [Fact]
    public async Task PlayWithNoAlbumAndNoMatch_GoesUnderAnUnknownAlbum()
    {
        var (item, isNew) = await _svc.MatchOrCreateAsync(_db, Play("Some Band", null, "Single"), PluginId, default);

        isNew.Should().BeTrue();
        (await _db.MediaItems.SingleAsync(i => i.Id == item.ParentId)).Name
            .Should().Be(SyncOrchestrationService.UnknownAlbumName);
    }

    [Fact]
    public async Task SameTitleOnADifferentAlbum_IsADifferentTrack()
    {
        var artist = Add("Some Band", 0);
        var album  = Add("Studio", 1, artist);
        Add("Intro", 2, album);

        var (item, isNew) = await _svc.MatchOrCreateAsync(_db, Play("Some Band", "Live", "Intro"), PluginId, default);

        isNew.Should().BeTrue();
        (await _db.MediaItems.CountAsync(i => i.Name == "Intro")).Should().Be(2);
        item.ParentId.Should().NotBe(album.Id);
    }

    [Fact]
    public async Task ExistingItem_DoesNotGetThePlayNameBuiltIdGrafted()
    {
        var artist = Add("Some Band", 0);
        var album  = Add("Some Album", 1, artist);
        var track  = Add("Song", 2, album);
        _db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = track.Id, Source = "lastfm", ExternalId = "track:rich-id" });
        await _db.SaveChangesAsync();

        await _svc.MatchOrCreateAsync(_db, Play("Some Band", "Some Album", "Song"), PluginId, default);

        (await _db.MediaExternalIds.SingleAsync(e => e.MediaItemId == track.Id && e.Source == "lastfm"))
            .ExternalId.Should().Be("track:rich-id");
    }

    [Fact]
    public async Task PunctuationOnlyNames_AreMatchedNotDuplicatedOnEveryPlay()
    {
        var first  = await _svc.MatchOrCreateAsync(_db, Play("!!!", "?", "!"), PluginId, default);
        var second = await _svc.MatchOrCreateAsync(_db, Play("!!!", "?", "!"), PluginId, default);

        second.isNew.Should().BeFalse();
        second.item.Id.Should().Be(first.item.Id);
        (await _db.MediaItems.CountAsync()).Should().Be(3);
    }
}
