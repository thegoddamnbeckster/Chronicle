using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// Live (2026-11-19): Michael J. Fox and Thomas F. Wilson each appeared twice on Back to the Future Part II. The IMDb record of each
/// had also picked up a DIFFERENT person's TMDB id (a sound editor, a film editor) through a name match, so the pair's birth dates
/// conflicted and the duplicate review had dismissed it for good. The service splits the stranger off, then merges what remains.
/// </summary>
public class PersonCreditConsolidationServiceTests
{
    private readonly ChronicleDbContext _db = new(new DbContextOptionsBuilder<ChronicleDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Mock<IMergeService> _merge = new();
    private readonly Mock<IMetadataResolutionService> _resolution = new();
    private readonly PersonCreditConsolidationService _svc;
    private int _nextId = 1000;

    public PersonCreditConsolidationServiceTests()
    {
        _db.MediaTypes.Add(new MediaType { Id = 1, Name = "people", IsPersonType = true, DisplayName = "People", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow });
        _db.MediaTypes.Add(new MediaType { Id = 2, Name = "movies", DisplayName = "Movies", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();
        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton(_merge.Object);
        services.AddSingleton(_resolution.Object);
        _svc = new PersonCreditConsolidationService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<PersonCreditConsolidationService>.Instance);
    }

    private MediaItem Person(string name, params string[] ids)
    {
        var p = new MediaItem { Id = _nextId++, MediaTypeId = 1, Name = name, NormalizedName = name.ToLowerInvariant(), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.MediaItems.Add(p);
        foreach (var id in ids) _db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = p.Id, Source = id.Split(':')[0], ExternalId = id });
        _db.SaveChanges();
        return p;
    }

    private MediaItem Title(string name)
    {
        var t = new MediaItem { Id = _nextId++, MediaTypeId = 2, Name = name, NormalizedName = name.ToLowerInvariant(), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.MediaItems.Add(t);
        _db.SaveChanges();
        return t;
    }

    private void Credit(MediaItem title, MediaItem person, string source, string? extId, string? character, string role = "Actor")
    {
        _db.MediaCredits.Add(new MediaCredit
        {
            MediaItemId = title.Id, PersonMediaItemId = person.Id, PersonName = person.Name, Source = source,
            ExternalPersonId = extId, Role = role, CharacterName = character,
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task AStrangerWithTheSameName_IsSplitOff_AndTheRealPersonsRecordsAreMerged()
    {
        var bttf = Title("Back to the Future"); var bttf2 = Title("Back to the Future Part II"); var sound = Title("Feast");
        var fromTmdb = Person("Michael J. Fox", "tmdb:521");
        var fromImdb = Person("Michael J. Fox", "imdb:nm0000150", "tmdb:1399286");
        Credit(bttf, fromTmdb, "tmdb", "tmdb:521", "Marty McFly");
        Credit(bttf2, fromTmdb, "tmdb", "tmdb:521", "Marty McFly / Marty McFly Junior");
        Credit(bttf, fromImdb, "imdb", "imdb:nm0000150", "Marty McFly");
        Credit(bttf2, fromImdb, "imdb", "imdb:nm0000150", "Marty McFly");
        Credit(sound, fromImdb, "tmdb", "tmdb:1399286", null, "ADR & Dubbing");

        var report = await _svc.RunAsync(apply: true, default);

        report.Split.Should().Be(1);
        var stranger = await _db.MediaExternalIds.SingleAsync(e => e.ExternalId == "tmdb:1399286");
        stranger.MediaItemId.Should().NotBe(fromImdb.Id).And.NotBe(fromTmdb.Id);
        (await _db.MediaCredits.SingleAsync(c => c.Role == "ADR & Dubbing")).PersonMediaItemId.Should().Be(stranger.MediaItemId);
        (await _db.MediaItems.FindAsync(stranger.MediaItemId))!.Name.Should().Be("Michael J. Fox");
        // the real person's two records are merged, the richest (IMDb: two ids at first, one after the split = tie) surviving
        _merge.Verify(m => m.MergeAsync(It.IsAny<int>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()), Times.Once);
        report.Merged.Should().Be(1);
    }

    [Fact]
    public async Task ADryRun_ReportsEverything_AndChangesNothing()
    {
        var t1 = Title("A"); var t2 = Title("B");
        var a = Person("Michael J. Fox", "tmdb:521"); var b = Person("Michael J. Fox", "imdb:nm0000150", "tmdb:1399286");
        Credit(t1, a, "tmdb", "tmdb:521", "Marty"); Credit(t1, b, "imdb", "imdb:nm0000150", "Marty");
        Credit(t2, b, "tmdb", "tmdb:1399286", null, "ADR");
        Credit(t2, a, "tmdb", "tmdb:521", "Marty");   // the confirmed id has the most credits

        var report = await _svc.RunAsync(apply: false, default);

        report.Findings.Select(f => f.Action).Should().Contain(["split", "merge"]);
        (await _db.MediaExternalIds.SingleAsync(e => e.ExternalId == "tmdb:1399286")).MediaItemId.Should().Be(b.Id);
        (await _db.MediaItems.CountAsync(m => m.MediaTypeId == 1)).Should().Be(2);
        _merge.Verify(m => m.MergeAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TwoIdsOfOneProviderThatATitleConfirmsBoth_AreOnePerson_NothingIsSplit()
    {
        // TMDB sometimes holds one actor under two ids; both credit the same character on the same title.
        var t = Title("Show");
        var a = Person("Rob Price", "tmdb:2121490"); var b = Person("Rob Price", "tmdb:5799954");
        Credit(t, a, "tmdb", "tmdb:2121490", "Dave"); Credit(t, b, "tmdb", "tmdb:5799954", "Dave");

        var report = await _svc.RunAsync(apply: true, default);

        report.Split.Should().Be(0);
        _merge.Verify(m => m.MergeAsync(It.IsAny<int>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConflictingIdsThatNoTitleConfirms_AreLeftForAPerson()
    {
        var t1 = Title("A"); var t2 = Title("B"); var t3 = Title("C");
        var a = Person("Sam Jones", "tmdb:1", "imdb:nm1"); var b = Person("Sam Jones", "tmdb:2", "wikidata:Q9");
        Credit(t1, a, "imdb", "imdb:nm1", "Cop"); Credit(t1, b, "wikidata", "wikidata:Q9", "Cop2");   // linked by two providers
        Credit(t2, a, "tmdb", "tmdb:1", "Cop"); Credit(t3, b, "tmdb", "tmdb:2", "Clerk");

        var report = await _svc.RunAsync(apply: true, default);

        report.Left.Should().Be(1);
        report.Split.Should().Be(0);
        _merge.Verify(m => m.MergeAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TwoPeopleWithOneNameFromOneProvider_AndDifferentCharacters_AreNeverTouched()
    {
        var t = Title("Film");
        var a = Person("Chris Evans", "tmdb:1"); var b = Person("Chris Evans", "tmdb:2");
        Credit(t, a, "tmdb", "tmdb:1", "Steve"); Credit(t, b, "tmdb", "tmdb:2", "Bartender");

        var report = await _svc.RunAsync(apply: true, default);

        report.Components.Should().Be(0);
        _merge.Verify(m => m.MergeAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AnUnconfirmedIdWithAsManyCreditsAsTheConfirmedOne_IsNotSplit_AndTheGroupIsLeft()
    {
        // An actor with one credit beside a post-production executive with several is not "the stranger": which is the person
        // cannot be told from the data (live dry run: Ed Jones).
        var wedding = Title("The Wedding Ringer"); var ghost = Title("Ghost"); var diehard = Title("Die Hard 2");
        var exec = Person("Ed Jones", "tmdb:59771", "wikidata:Q453770"); var actor = Person("Ed Jones", "tmdb:1231306");
        Credit(wedding, exec, "wikidata", "wikidata:Q453770", "Guest"); Credit(wedding, actor, "tmdb", "tmdb:1231306", "Guest");
        Credit(ghost, exec, "tmdb", "tmdb:59771", null, "Executive In Charge Of Post Production");
        Credit(diehard, exec, "tmdb", "tmdb:59771", null, "Executive In Charge Of Post Production");

        var report = await _svc.RunAsync(apply: true, default);

        report.Split.Should().Be(0);
        report.Left.Should().Be(1);
        _merge.Verify(m => m.MergeAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ARecordsDateStillDisagreeingAfterTheSplit_IsNotMerged()
    {
        var t = Title("Film");
        var a = Person("Pat Lee", "tmdb:1"); var b = Person("Pat Lee", "imdb:nm5");
        a.BirthDate = new DateTime(1960, 1, 1); b.BirthDate = new DateTime(1990, 1, 1); _db.SaveChanges();
        Credit(t, a, "tmdb", "tmdb:1", "Judge"); Credit(t, b, "imdb", "imdb:nm5", "Judge");

        var report = await _svc.RunAsync(apply: true, default);

        report.Left.Should().Be(1);
        _merge.Verify(m => m.MergeAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AShortenedGivenName_FromAnotherProvider_IsMerged_AndTheEditorIsSplitOff()
    {
        var t = Title("BTTF2"); var editing = Title("Mr. Right");
        var tmdb = Person("Thomas F. Wilson", "tmdb:1065"); var imdb = Person("Tom Wilson", "imdb:nm0001855", "tmdb:1619912");
        Credit(t, tmdb, "tmdb", "tmdb:1065", "Biff Tannen / Griff");
        Credit(t, imdb, "imdb", "imdb:nm0001855", "Biff Tannen"); Credit(t, imdb, "imdb", "imdb:nm0001855", "Griff");
        Credit(editing, imdb, "tmdb", "tmdb:1619912", null, "Editor");
        Credit(editing, tmdb, "tmdb", "tmdb:1065", "Cameo");

        var report = await _svc.RunAsync(apply: true, default);

        report.Split.Should().Be(1);
        report.Merged.Should().Be(1);
        (await _db.MediaCredits.SingleAsync(c => c.Role == "Editor")).PersonMediaItemId.Should().NotBe(imdb.Id).And.NotBe(tmdb.Id);
    }
}
