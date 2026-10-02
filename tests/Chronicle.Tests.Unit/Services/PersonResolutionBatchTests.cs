using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// ResolveAndRecordCreditsAsync (the batch used for a title's whole credit list) must make the
/// same decisions as calling ResolveAndRecordCreditAsync once per credit. Each scenario runs
/// both ways on separate real-SQLite databases and compares the outcome.
/// </summary>
public sealed class PersonResolutionBatchTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    private (ChronicleDbContext Db, PersonResolutionService Svc, int TitleId) NewWorld(Action<ChronicleDbContext>? seed = null)
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        _disposables.Add(db);
        _disposables.Add(conn);

        if (!db.MediaTypes.Any(t => t.Name == "people"))
            db.MediaTypes.Add(new MediaType { Name = "people", DisplayName = "People", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
        var movies = db.MediaTypes.Single(t => t.Name == "movies");
        var title = new MediaItem { Name = "Title", MediaTypeId = movies.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MediaItems.Add(title);
        db.SaveChanges();
        seed?.Invoke(db);
        db.SaveChanges();

        var provider = new Mock<IMetadataProvider>();
        provider.Setup(p => p.GetSupportedMediaTypes()).Returns([new MediaTypeSupport { MediaTypeName = "people" }]);
        var registry = new Mock<IPluginRegistry>();
        registry.Setup(r => r.GetMetadataProviderEntries()).Returns(
            [("chronicle.plugin.tmdb", provider.Object, null), ("chronicle.plugin.imdb", provider.Object, null)]);
        var svc = new PersonResolutionService(registry.Object, Mock.Of<IMetadataResolutionService>(),
            NullLogger<PersonResolutionService>.Instance);
        return (db, svc, title.Id);
    }

    private static MediaItem Person(ChronicleDbContext db, string name, params (string Source, string Id)[] ids)
    {
        var peopleTypeId = db.MediaTypes.Single(t => t.Name == "people").Id;
        var p = new MediaItem { Name = name, NormalizedName = Chronicle.Core.Helpers.MediaItemNormalizer.NormalizeName(name),
            MediaTypeId = peopleTypeId, IsStub = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MediaItems.Add(p);
        db.SaveChanges();
        foreach (var (s, x) in ids) db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = p.Id, Source = s, ExternalId = x });
        db.SaveChanges();
        return p;
    }

    /// <summary>Who each credit went to (by name of the resolved person and whether it was
    /// pre-existing), every person's ids, and the counts that matter.</summary>
    private static async Task<object> Outcome(ChronicleDbContext db, int titleId, string source)
    {
        var credits = await db.MediaCredits.Where(c => c.MediaItemId == titleId && c.Source == source)
            .OrderBy(c => c.Id)
            .Select(c => new { c.PersonName, c.Role, c.CharacterName, c.BillingOrder, c.ExternalPersonId,
                               Person = c.PersonMediaItem!.Name, PersonIsStub = c.PersonMediaItem.IsStub, c.PersonMediaItemId })
            .ToListAsync();
        // Person identity normalised to "nth distinct person in credit order" so ids from two
        // separate databases compare.
        var order = credits.Select(c => c.PersonMediaItemId).Distinct().ToList();
        var peopleType = db.MediaTypes.Single(t => t.Name == "people").Id;
        var ids = await db.MediaExternalIds.Where(x => x.Source == source)
            .Select(x => new { x.MediaItemId, x.ExternalId }).ToListAsync();
        return new
        {
            Credits = credits.Select(c => new { c.PersonName, c.Role, c.CharacterName, c.BillingOrder, c.ExternalPersonId,
                                               PersonNo = order.IndexOf(c.PersonMediaItemId), c.Person }).ToList(),
            IdsByPerson = order.Select(pid => ids.Where(x => x.MediaItemId == pid).Select(x => x.ExternalId).Order().ToList()).ToList(),
            People = await db.MediaItems.CountAsync(m => m.MediaTypeId == peopleType),
            EnrichmentRows = await db.MediaEnrichments.CountAsync(),
        };
    }

    private async Task AssertSameAsOneAtATime(CreditToRecord[] credits, string source, Action<ChronicleDbContext>? seed = null)
    {
        var (dbA, svcA, titleA) = NewWorld(seed);
        foreach (var c in credits)
            await svcA.ResolveAndRecordCreditAsync(dbA, titleA, c.PersonName, c.ExternalPersonId, source,
                c.ProfileImageUrl, c.Role, c.CharacterName, c.BillingOrder);
        await dbA.SaveChangesAsync();

        var (dbB, svcB, titleB) = NewWorld(seed);
        await svcB.ResolveAndRecordCreditsAsync(dbB, titleB, credits, source);
        await dbB.SaveChangesAsync();

        (await Outcome(dbB, titleB, source)).Should().BeEquivalentTo(await Outcome(dbA, titleA, source),
            o => o.WithStrictOrdering());
    }

    private static CreditToRecord C(string name, string? id, string role = "Actor", string? character = null, int? order = null) =>
        new(name, id, null, role, character, order);

    [Fact]
    public Task NewPeople_OneStubEach_IdsRecorded_EnrichmentRowsSeeded() =>
        AssertSameAsOneAtATime([C("Keanu Reeves", "imdb:nm0000206", order: 0), C("Laurence Fishburne", "imdb:nm0000401", order: 1)], "imdb");

    [Fact]
    public Task SamePersonTwiceInOneTitle_OneStub() =>
        AssertSameAsOneAtATime([C("Clint Eastwood", "imdb:nm0000142"), C("Clint Eastwood", "imdb:nm0000142", "Director")], "imdb");

    [Fact]
    public Task SameNameDifferentIds_TwoPeople() =>
        AssertSameAsOneAtATime([C("Chris Smith", "imdb:nm0000001"), C("Chris Smith", "imdb:nm0000002", "Producer")], "imdb");

    [Fact]
    public Task SameNameWithAndWithoutId_OnePerson() =>
        AssertSameAsOneAtATime([C("Jane Doe", "imdb:nm0000009"), C("Jane Doe", null, "Writer")], "imdb");

    [Fact]
    public Task ExistingPersonById_IsReused() =>
        AssertSameAsOneAtATime([C("K. Reeves", "imdb:nm0000206")], "imdb",
            db => Person(db, "Keanu Reeves", ("imdb", "imdb:nm0000206")));

    [Fact]
    public Task ExistingPersonByName_GetsTheId() =>
        AssertSameAsOneAtATime([C("Keanu Reeves", "imdb:nm0000206")], "imdb",
            db => Person(db, "Keanu Reeves", ("tmdb", "tmdb:6384")));

    [Fact]
    public Task ExistingSameNamedPersonWithADifferentIdFromThisSource_IsRefused() =>
        AssertSameAsOneAtATime([C("Brian Johnson", "imdb:nm0000777")], "imdb",
            db => Person(db, "Brian Johnson", ("imdb", "imdb:nm0000555")));

    [Fact]
    public Task LooseNameMatch_IsUsed() =>
        AssertSameAsOneAtATime([C("Jean Luc Godard", "imdb:nm0000419")], "imdb",
            db => Person(db, "Jean-Luc Godard"));

    [Fact]
    public Task MixedRealisticList() =>
        AssertSameAsOneAtATime(
        [
            C("Keanu Reeves", "imdb:nm0000206", character: "Neo", order: 0),
            C("Carrie-Anne Moss", "imdb:nm0005251", character: "Trinity", order: 1),
            C("Hugo Weaving", "imdb:nm0915989", character: "Agent Smith", order: 2),
            C("Lana Wachowski", "imdb:nm0905154", "Director"),
            C("Lilly Wachowski", "imdb:nm0905152", "Director"),
            C("Lana Wachowski", "imdb:nm0905154", "Writer"),
            C("Joel Silver", "imdb:nm0005428", "Executive Producer"),
            C("Brian Johnson", "imdb:nm0000777", "Editor"),
        ], "imdb",
        db =>
        {
            Person(db, "Keanu Reeves", ("imdb", "imdb:nm0000206"));
            Person(db, "Hugo Weaving", ("tmdb", "tmdb:1331"));
            Person(db, "Brian Johnson", ("imdb", "imdb:nm0000555"));
        });

    [Fact]
    public async Task ManyNewPeople_OneSaveForAllOfThem()
    {
        var (db, svc, title) = NewWorld();
        var saves = 0;
        db.SavingChanges += (_, _) => saves++;

        await svc.ResolveAndRecordCreditsAsync(db, title,
            [.. Enumerable.Range(0, 200).Select(i => C($"Crew Member {i}", $"imdb:nm{i:D7}", "Director"))], "imdb");
        await db.SaveChangesAsync();

        saves.Should().Be(2, "one save for the new people, one for their credits and enrichment rows");
        (await db.MediaCredits.CountAsync()).Should().Be(200);
        (await db.MediaEnrichments.CountAsync()).Should().Be(400);
    }
}
