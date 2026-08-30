using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

public class PersonResolutionServiceTests : IDisposable
{
    private readonly ChronicleDbContext _db;
    private readonly Mock<IPluginRegistry> _registry;
    private readonly PersonResolutionService _svc;

    public PersonResolutionServiceTests()
    {
        var opts = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new ChronicleDbContext(opts);
        _registry = new Mock<IPluginRegistry>();
        _registry.Setup(r => r.GetMetadataProviderEntries())
            .Returns(new List<(string, IMetadataProvider, string?)>());
        _svc = new PersonResolutionService(
            _registry.Object, Mock.Of<IMetadataResolutionService>(), Mock.Of<ILogger<PersonResolutionService>>());

        _db.MediaTypes.Add(new MediaType { Id = 1, Name = "people", DisplayName = "People", CreatedAt = DateTime.UtcNow });
        _db.MediaTypes.Add(new MediaType { Id = 2, Name = "movies", DisplayName = "Movies", CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();
    }

    [Fact]
    public async Task ResolvePersonOnlyAsync_NoExistingMatch_CreatesStub()
    {
        var person = await _svc.ResolvePersonOnlyAsync(_db, "Anson Mount", "tmdb:12345", "tmdb", default);
        await _db.SaveChangesAsync();

        person.Should().NotBeNull();
        person.IsStub.Should().BeTrue();
        person.MediaTypeId.Should().Be(1);
        person.HierarchyLevel.Should().Be(0);
        person.NormalizedName.Should().Be("anson mount");

        var extId = await _db.MediaExternalIds.SingleAsync(x => x.MediaItemId == person.Id);
        extId.Source.Should().Be("tmdb");
        extId.ExternalId.Should().Be("tmdb:12345");
    }

    [Fact]
    public async Task ResolvePersonOnlyAsync_ExistingExternalId_ReturnsSamePerson_NoDuplicate()
    {
        var first = await _svc.ResolvePersonOnlyAsync(_db, "Anson Mount", "tmdb:12345", "tmdb", default);
        var second = await _svc.ResolvePersonOnlyAsync(_db, "Anson Mount", "tmdb:12345", "tmdb", default);

        second.Id.Should().Be(first.Id);
        (await _db.MediaItems.CountAsync(m => m.MediaTypeId == 1)).Should().Be(1);
    }

    [Fact]
    public async Task ResolvePersonOnlyAsync_NoExternalId_FallsBackToNormalizedNameMatch()
    {
        var first = await _svc.ResolvePersonOnlyAsync(_db, "Anson Mount", null, "wikipedia", default);
        // Same person, different punctuation/casing, no external id on either call --
        // resolves via NormalizedName, not a new stub.
        var second = await _svc.ResolvePersonOnlyAsync(_db, "anson  mount", null, "wikipedia", default);

        second.Id.Should().Be(first.Id);
        (await _db.MediaItems.CountAsync(m => m.MediaTypeId == 1)).Should().Be(1);
    }

    [Fact]
    public async Task ResolvePersonOnlyAsync_ExternalIdScopedToPeopleType_IgnoresNonPersonMatch()
    {
        // A movie happens to share the exact same (source, externalId) pair as a person would --
        // must never resolve onto it.
        var movie = new MediaItem
        {
            MediaTypeId = 2, Name = "Some Movie", HierarchyLevel = 0,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.MediaItems.Add(movie);
        await _db.SaveChangesAsync();
        _db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = movie.Id, Source = "tmdb", ExternalId = "tmdb:12345" });
        await _db.SaveChangesAsync();

        var person = await _svc.ResolvePersonOnlyAsync(_db, "Some Person", "tmdb:12345", "tmdb", default);

        person.Id.Should().NotBe(movie.Id);
        person.MediaTypeId.Should().Be(1);
    }

    [Fact]
    public async Task ResolvePersonOnlyAsync_NoPeopleMediaTypeRegistered_Throws()
    {
        var db2 = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        await FluentActions.Invoking(() => _svc.ResolvePersonOnlyAsync(db2, "Anson Mount", null, "wikipedia", default))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ResolveAndRecordCreditAsync_WritesCreditRowLinkedToPerson()
    {
        var movie = new MediaItem
        {
            MediaTypeId = 2, Name = "Star Trek: Strange New Worlds", HierarchyLevel = 0,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.MediaItems.Add(movie);
        await _db.SaveChangesAsync();

        await _svc.ResolveAndRecordCreditAsync(
            _db, movie.Id, "Anson Mount", "tmdb:12345", "tmdb", "https://image.tmdb.org/photo.jpg",
            role: "Actor", characterName: "Christopher Pike", billingOrder: 0, default);
        await _db.SaveChangesAsync();

        var credit = await _db.MediaCredits.SingleAsync(c => c.MediaItemId == movie.Id);
        credit.PersonName.Should().Be("Anson Mount");
        credit.Role.Should().Be("Actor");
        credit.CharacterName.Should().Be("Christopher Pike");
        credit.PersonMediaItemId.Should().NotBeNull();

        var headshot = await _db.PersonHeadshots.SingleAsync(h => h.PersonMediaItemId == credit.PersonMediaItemId);
        headshot.Url.Should().Be("https://image.tmdb.org/photo.jpg");
        headshot.Source.Should().Be("tmdb");
    }

    [Fact]
    public async Task ResolveAndRecordCreditAsync_SameHeadshotUrlTwice_DoesNotDuplicate()
    {
        var movie1 = new MediaItem { MediaTypeId = 2, Name = "Movie 1", HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var movie2 = new MediaItem { MediaTypeId = 2, Name = "Movie 2", HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.MediaItems.AddRange(movie1, movie2);
        await _db.SaveChangesAsync();

        // Same person (same external id) credited on two different titles with the identical
        // headshot URL both times -- a routine case (re-discovering the same photo), not a
        // duplicate to record twice.
        await _svc.ResolveAndRecordCreditAsync(_db, movie1.Id, "Anson Mount", "tmdb:12345", "tmdb", "https://img/photo.jpg", "Actor", null, null, default);
        await _db.SaveChangesAsync();
        await _svc.ResolveAndRecordCreditAsync(_db, movie2.Id, "Anson Mount", "tmdb:12345", "tmdb", "https://img/photo.jpg", "Actor", null, null, default);
        await _db.SaveChangesAsync();

        (await _db.PersonHeadshots.CountAsync()).Should().Be(1);
        (await _db.MediaCredits.CountAsync()).Should().Be(2); // one credit row per title, though
    }

    [Fact]
    public async Task ResolvePersonOnlyAsync_NewStub_SeedsEnrichmentRowsForCompatiblePluginsOnly()
    {
        var peopleProvider = new Mock<IMetadataProvider>();
        peopleProvider.Setup(p => p.GetSupportedMediaTypes())
            .Returns([new MediaTypeSupport { MediaTypeName = "people", DisplayName = "People" }]);
        var movieOnlyProvider = new Mock<IMetadataProvider>();
        movieOnlyProvider.Setup(p => p.GetSupportedMediaTypes())
            .Returns([new MediaTypeSupport { MediaTypeName = "movies", DisplayName = "Movies" }]);

        _registry.Setup(r => r.GetMetadataProviderEntries())
            .Returns(new List<(string, IMetadataProvider, string?)>
            {
                ("chronicle.plugin.wikipedia", peopleProvider.Object, null),
                ("chronicle.plugin.tmdb", movieOnlyProvider.Object, null),
            });

        var person = await _svc.ResolvePersonOnlyAsync(_db, "Anson Mount", null, "wikipedia", default);

        var rows = await _db.MediaEnrichments.Where(e => e.MediaItemId == person.Id).ToListAsync();
        rows.Should().ContainSingle();
        rows[0].PluginId.Should().Be("chronicle.plugin.wikipedia");
    }

    [Fact]
    public async Task ResolveAndRecordCreditAsync_SamePersonCreditedTwiceInOneBatch_DoesNotThrowOnSave()
    {
        // Regression test (2026-08-30): confirmed live against a real TMDB force-refresh --
        // a person credited under BOTH Cast (Actor) and Crew (e.g. Executive Producer) in the
        // same enrichment result, with the identical ProfileImageUrl both times (TMDB returns
        // the same profile_path regardless of which credit list a person appears in), used to
        // throw a unique-constraint violation on person_headshots at SaveChangesAsync -- the
        // duplicate-headshot check only queried the database, which can't see the FIRST
        // occurrence's insert until the batch is actually saved. Real caller pattern
        // (MetadataEnrichmentService.ResolveCreditsAsync): many ResolveAndRecordCreditAsync
        // calls, one SaveChangesAsync at the very end.
        var movie = new MediaItem { MediaTypeId = 2, Name = "Strange New Worlds", HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.MediaItems.Add(movie);
        await _db.SaveChangesAsync();

        await _svc.ResolveAndRecordCreditAsync(
            _db, movie.Id, "Anson Mount", "tmdb:287", "tmdb", "https://image.tmdb.org/photo.jpg",
            role: "Actor", characterName: "Christopher Pike", billingOrder: 0, default);
        await _svc.ResolveAndRecordCreditAsync(
            _db, movie.Id, "Anson Mount", "tmdb:287", "tmdb", "https://image.tmdb.org/photo.jpg",
            role: "Executive Producer", characterName: null, billingOrder: null, default);

        // Must not throw (the actual bug: DbUpdateException from the unique index).
        await _db.SaveChangesAsync();

        (await _db.PersonHeadshots.CountAsync()).Should().Be(1);
        (await _db.MediaCredits.CountAsync(c => c.MediaItemId == movie.Id)).Should().Be(2);
    }

    public void Dispose() => _db.Dispose();
}
