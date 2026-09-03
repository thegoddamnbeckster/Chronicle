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
    public async Task ResolvePersonOnlyAsync_WhitespaceOnlyNameDifference_FallsBackToLooseNormalizedNameMatch()
    {
        // Regression test for a real production duplicate (2026-09-03): "Cee Lo Green" (from
        // one plugin) and "CeeLo Green" (from another) are the same real person, but Step 2's
        // exact NormalizedName match treats "cee lo green" and "ceelo green" as different
        // strings -- NormalizeName collapses whitespace runs to a single space, it never
        // removes it. NormalizeNameLoose already existed for exactly this class of gap (added
        // for "James S. A. Corey" vs "James S.A. Corey") but was never wired into this general
        // resolution path, so every plugin that spaces a name differently than an earlier one
        // created a fresh duplicate stub instead of matching the existing person.
        var first = await _svc.ResolvePersonOnlyAsync(_db, "CeeLo Green", null, "wikipedia", default);
        await _db.SaveChangesAsync();

        var second = await _svc.ResolvePersonOnlyAsync(_db, "Cee Lo Green", null, "tmdb", default);
        await _db.SaveChangesAsync();

        second.Id.Should().Be(first.Id);
        (await _db.MediaItems.CountAsync(m => m.MediaTypeId == 1)).Should().Be(1);
    }

    [Fact]
    public async Task ResolvePersonOnlyAsync_LooseMatchSameSourceConflictingId_CreatesSeparatePersonInsteadOfMerging()
    {
        // The Step 2b loose-name fallback must carry the exact same same-source-conflict guard
        // as Step 2's exact match -- otherwise it would just be a second, easier way to
        // reproduce the Brian Johnson conflation (two different real people who happen to share
        // a name, differing only in whitespace, must not be silently merged).
        var first = await _svc.ResolvePersonOnlyAsync(_db, "CeeLo Green", "tmdb:111", "tmdb", default);
        await _db.SaveChangesAsync();

        var second = await _svc.ResolvePersonOnlyAsync(_db, "Cee Lo Green", "tmdb:222", "tmdb", default);
        await _db.SaveChangesAsync();

        second.Id.Should().NotBe(first.Id);
        (await _db.MediaItems.CountAsync(m => m.MediaTypeId == 1)).Should().Be(2);
    }

    [Fact]
    public async Task ResolvePersonOnlyAsync_SameSourceConflictingId_CreatesSeparatePersonInsteadOfMerging()
    {
        // Regression test for a confirmed live incident: "Brian Johnson" the VFX artist
        // (tmdb:9402) and "Brian Johnson" the AC/DC singer (tmdb:84008) are two different real
        // people who share an exact name. The singer's credit has no id on file yet for this
        // name, so it would fall through to a name-only match -- but it DOES carry its own tmdb
        // id, which conflicts with the id already recorded against the name match. That's the
        // signal: don't merge, create a separate person.
        var artist = await _svc.ResolvePersonOnlyAsync(_db, "Brian Johnson", "tmdb:9402", "tmdb", default);
        await _db.SaveChangesAsync();

        var singer = await _svc.ResolvePersonOnlyAsync(_db, "Brian Johnson", "tmdb:84008", "tmdb", default);
        await _db.SaveChangesAsync();

        singer.Id.Should().NotBe(artist.Id);
        (await _db.MediaItems.CountAsync(m => m.MediaTypeId == 1)).Should().Be(2);

        var singerExtId = await _db.MediaExternalIds.SingleAsync(x => x.MediaItemId == singer.Id);
        singerExtId.ExternalId.Should().Be("tmdb:84008");
    }

    [Fact]
    public async Task ResolvePersonOnlyAsync_NewIdFromDifferentSource_StillMergesIntoNameMatch()
    {
        // The conflict check is scoped to the SAME source as the incoming credit -- a person
        // enriched via TMDB first, then later credited via a source that's never supplied an id
        // for them before (e.g. Wikipedia), is routine multi-source enrichment, not a collision.
        // Must keep resolving onto the existing person, not fragment it.
        var person = await _svc.ResolvePersonOnlyAsync(_db, "Anson Mount", "tmdb:12345", "tmdb", default);
        await _db.SaveChangesAsync();

        var again = await _svc.ResolvePersonOnlyAsync(_db, "Anson Mount", "wikipedia:en:Anson_Mount", "wikipedia", default);
        await _db.SaveChangesAsync();

        again.Id.Should().Be(person.Id);
        (await _db.MediaItems.CountAsync(m => m.MediaTypeId == 1)).Should().Be(1);
        (await _db.MediaExternalIds.CountAsync(x => x.MediaItemId == person.Id)).Should().Be(2);
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
    public async Task ResolvePersonOnlyAsync_NoPeopleMediaTypeRegistered_SelfHealsInsteadOfThrowing()
    {
        // Nothing in the normal plugin-media-type-sync path guarantees "people" exists yet --
        // unlike every other type, no single installed plugin owns registering it (see
        // PersonResolutionService.GetPeopleMediaTypeIdAsync). Resolving the very first credit,
        // ever, must still work rather than requiring a specific plugin to be installed first.
        var db2 = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var person = await _svc.ResolvePersonOnlyAsync(db2, "Anson Mount", null, "wikipedia", default);

        var mediaType = await db2.MediaTypes.SingleAsync(t => t.Name == "people");
        person.MediaTypeId.Should().Be(mediaType.Id);
        mediaType.IsTrackable.Should().BeFalse();
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
    public async Task RecordOwnPortraitAsync_NewPosterUrl_InsertsHeadshot()
    {
        // Feed path 1 of Section 1.5: a person's OWN enrichment result (not a credit on
        // someone else's title) supplying a photo -- e.g. TMDB's own /person/{id} profile
        // picture, or Wikipedia's bio photo.
        var person = await _svc.ResolvePersonOnlyAsync(_db, "Keanu Reeves", "tmdb:6384", "tmdb", default);
        await _db.SaveChangesAsync();

        await _svc.RecordOwnPortraitAsync(_db, person,
            [("https://image.tmdb.org/t/p/original/keanu.jpg", "https://image.tmdb.org/t/p/w500/keanu.jpg")],
            "tmdb", default);
        await _db.SaveChangesAsync();

        var headshot = await _db.PersonHeadshots.SingleAsync(h => h.PersonMediaItemId == person.Id);
        headshot.Url.Should().Be("https://image.tmdb.org/t/p/original/keanu.jpg");
        headshot.ThumbnailUrl.Should().Be("https://image.tmdb.org/t/p/w500/keanu.jpg");
        headshot.Source.Should().Be("tmdb");
    }

    [Fact]
    public async Task RecordOwnPortraitAsync_MultipleUrls_InsertsEveryOne()
    {
        // TMDB's own /person/{id}/images gallery can return dozens of alternate photos in a
        // single enrichment result -- all of them, not just the one "current" pick, must reach
        // person_headshots for the picker to have anything to show beyond a single photo.
        var person = await _svc.ResolvePersonOnlyAsync(_db, "Keanu Reeves", "tmdb:6384", "tmdb", default);
        await _db.SaveChangesAsync();

        await _svc.RecordOwnPortraitAsync(_db, person,
            [("https://image.tmdb.org/a.jpg", (string?)null),
             ("https://image.tmdb.org/b.jpg", null),
             ("https://image.tmdb.org/c.jpg", null)],
            "tmdb", default);
        await _db.SaveChangesAsync();

        (await _db.PersonHeadshots.CountAsync(h => h.PersonMediaItemId == person.Id)).Should().Be(3);
    }

    [Fact]
    public async Task RecordOwnPortraitAsync_SameUrlTwice_DoesNotDuplicate()
    {
        var person = await _svc.ResolvePersonOnlyAsync(_db, "Keanu Reeves", "tmdb:6384", "tmdb", default);
        await _db.SaveChangesAsync();

        await _svc.RecordOwnPortraitAsync(_db, person, [("https://image.tmdb.org/t/p/h632/keanu.jpg", (string?)null)], "tmdb", default);
        await _db.SaveChangesAsync();
        await _svc.RecordOwnPortraitAsync(_db, person, [("https://image.tmdb.org/t/p/h632/keanu.jpg", (string?)null)], "tmdb", default);
        await _db.SaveChangesAsync();

        (await _db.PersonHeadshots.CountAsync(h => h.PersonMediaItemId == person.Id)).Should().Be(1);
    }

    [Fact]
    public async Task RecordOwnPortraitAsync_EmptyOrBlankUrls_NoOp()
    {
        var person = await _svc.ResolvePersonOnlyAsync(_db, "Keanu Reeves", "tmdb:6384", "tmdb", default);
        await _db.SaveChangesAsync();

        await _svc.RecordOwnPortraitAsync(_db, person, [], "tmdb", default);
        await _svc.RecordOwnPortraitAsync(_db, person, [(null!, (string?)null), ("", null)], "tmdb", default);
        await _db.SaveChangesAsync();

        (await _db.PersonHeadshots.CountAsync(h => h.PersonMediaItemId == person.Id)).Should().Be(0);
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
