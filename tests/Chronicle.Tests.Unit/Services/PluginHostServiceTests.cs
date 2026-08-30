using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// Covers BackfillCreditsFromCachedMetadataAsync -- resolving cast/crew already sitting in a
/// media_item's cached metadata_json into real media_credits rows, without any network call.
/// Confirmed live (2026-08-30): almost the entire library had this cast/crew data cached from
/// enrichment runs that predate the People feature, with zero media_credits rows to show for it,
/// because credit resolution only ever ran on a FRESH enrichment result.
/// </summary>
public class PluginHostServiceTests : IDisposable
{
    private readonly ChronicleDbContext _db;
    private readonly PersonResolutionService _personResolutionService;
    private readonly PluginHostService _svc;

    public PluginHostServiceTests()
    {
        var opts = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new ChronicleDbContext(opts);

        var registry = new Mock<IPluginRegistry>();
        registry.Setup(r => r.GetMetadataProviderEntries())
            .Returns(new List<(string, IMetadataProvider, string?)>());
        _personResolutionService = new PersonResolutionService(
            registry.Object, Mock.Of<IMetadataResolutionService>(), Mock.Of<ILogger<PersonResolutionService>>());

        _db.MediaTypes.Add(new MediaType { Id = 1, Name = "people", DisplayName = "People", IsTrackable = false, CreatedAt = DateTime.UtcNow });
        _db.MediaTypes.Add(new MediaType { Id = 2, Name = "movies", DisplayName = "Movies", CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();

        _svc = new PluginHostService(
            Mock.Of<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            registry.Object,
            Mock.Of<IPluginSettingsProtector>(),
            Mock.Of<IHostEnvironment>());
    }

    private static MediaItem MakeMovie(int id, string name, string metadataJson) => new()
    {
        Id = id, MediaTypeId = 2, Name = name, HierarchyLevel = 0,
        MetadataJson = metadataJson, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task BackfillCreditsFromCachedMetadataAsync_CastAndCrewInBlob_ResolvesToMediaCredits()
    {
        var movie = MakeMovie(100, "Step Brothers", """
            {
                "chronicle.plugin.tmdb": {
                    "cast": [
                        { "name": "Will Ferrell", "role": "Brennan Huff" },
                        { "name": "Adam Scott", "role": "Derek Huff", "externalPersonId": "tmdb:36801" }
                    ],
                    "crew": [
                        { "name": "Adam McKay", "job": "Director" }
                    ]
                }
            }
            """);
        _db.MediaItems.Add(movie);
        await _db.SaveChangesAsync();

        await _svc.BackfillCreditsFromCachedMetadataAsync(_db, _personResolutionService, default);
        await _db.SaveChangesAsync();

        var credits = await _db.MediaCredits.Where(c => c.MediaItemId == movie.Id).ToListAsync();
        credits.Should().HaveCount(3);
        credits.Should().Contain(c => c.PersonName == "Adam Scott" && c.Role == "Actor" && c.CharacterName == "Derek Huff");
        credits.Should().Contain(c => c.PersonName == "Adam McKay" && c.Role == "Director" && c.CharacterName == null);
        credits.Should().OnlyContain(c => c.PersonMediaItemId != null);
    }

    [Fact]
    public async Task BackfillCreditsFromCachedMetadataAsync_ItemAlreadyHasCreditsForSource_SkipsIt()
    {
        var movie = MakeMovie(101, "Already Resolved Movie", """
            { "chronicle.plugin.tmdb": { "cast": [{ "name": "Someone New" }] } }
            """);
        _db.MediaItems.Add(movie);
        await _db.SaveChangesAsync();

        // Simulate this (item, source) pair already having gone through the normal fresh-
        // enrichment credit path -- must not be touched or duplicated by the backfill.
        _db.MediaCredits.Add(new MediaCredit
        {
            MediaItemId = movie.Id, PersonName = "Existing Person", Role = "Actor", Source = "tmdb",
        });
        await _db.SaveChangesAsync();

        await _svc.BackfillCreditsFromCachedMetadataAsync(_db, _personResolutionService, default);
        await _db.SaveChangesAsync();

        var credits = await _db.MediaCredits.Where(c => c.MediaItemId == movie.Id).ToListAsync();
        credits.Should().ContainSingle().Which.PersonName.Should().Be("Existing Person");
    }

    [Fact]
    public async Task BackfillCreditsFromCachedMetadataAsync_NoCastOrCrewInBlob_NoCreditsCreated()
    {
        var movie = MakeMovie(102, "No Cast Data", """
            { "chronicle.plugin.tmdb": { "title": "No Cast Data", "overview": "..." } }
            """);
        _db.MediaItems.Add(movie);
        await _db.SaveChangesAsync();

        await _svc.BackfillCreditsFromCachedMetadataAsync(_db, _personResolutionService, default);
        await _db.SaveChangesAsync();

        (await _db.MediaCredits.CountAsync(c => c.MediaItemId == movie.Id)).Should().Be(0);
    }

    [Fact]
    public async Task BackfillCreditsFromCachedMetadataAsync_SamePersonAcrossTwoTitles_ResolvesOntoSamePersonItem()
    {
        var movie1 = MakeMovie(103, "Step Brothers", """
            { "chronicle.plugin.tmdb": { "cast": [{ "name": "Adam Scott", "role": "Derek Huff", "externalPersonId": "tmdb:36801" }] } }
            """);
        var movie2 = MakeMovie(104, "The Whisper Man", """
            { "chronicle.plugin.tmdb": { "cast": [{ "name": "Adam Scott", "role": "Tom Kennedy", "externalPersonId": "tmdb:36801" }] } }
            """);
        _db.MediaItems.AddRange(movie1, movie2);
        await _db.SaveChangesAsync();

        await _svc.BackfillCreditsFromCachedMetadataAsync(_db, _personResolutionService, default);
        await _db.SaveChangesAsync();

        var credits = await _db.MediaCredits.Where(c => c.PersonName == "Adam Scott").ToListAsync();
        credits.Should().HaveCount(2);
        credits.Select(c => c.PersonMediaItemId).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task BackfillCreditsFromCachedMetadataAsync_PeopleMediaTypeNotRegistered_NoOp()
    {
        var opts = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var db = new ChronicleDbContext(opts);
        db.MediaTypes.Add(new MediaType { Id = 1, Name = "movies", DisplayName = "Movies", CreatedAt = DateTime.UtcNow });
        db.MediaItems.Add(MakeMovie(105, "Some Movie", """
            { "chronicle.plugin.tmdb": { "cast": [{ "name": "Someone" }] } }
            """));
        await db.SaveChangesAsync();

        await _svc.BackfillCreditsFromCachedMetadataAsync(db, _personResolutionService, default);

        (await db.MediaCredits.CountAsync()).Should().Be(0);
    }

    public void Dispose() => _db.Dispose();
}
