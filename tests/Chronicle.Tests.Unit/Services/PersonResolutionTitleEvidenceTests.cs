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
/// A credit that carries an id is matched to an existing person by NAME only when the title already proves it. Live (2026-11-19):
/// a sound editor's TMDB id was attached to the IMDb record of Michael J. Fox, and a film editor's to Tom Wilson's, purely because the
/// names matched; the real TMDB credit then refused those records (a different TMDB id) and made a second person, so Back to the
/// Future Part II listed both actors twice.
/// </summary>
public sealed class PersonResolutionTitleEvidenceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly ChronicleDbContext _db;
    private readonly PersonResolutionService _svc;
    private readonly int _titleId;
    private readonly int _otherTitleId;

    public PersonResolutionTitleEvidenceTests()
    {
        RegistryFixtures.LoadBuiltIns();
        _conn.Open();
        _db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        if (!_db.MediaTypes.Any(t => t.Name == "people"))
            _db.MediaTypes.Add(new MediaType { Name = "people", IsPersonType = true, DisplayName = "People", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();
        var movies = _db.MediaTypes.Single(t => t.Name == "movies");
        var t1 = new MediaItem { Name = "Back to the Future Part II", MediaTypeId = movies.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var t2 = new MediaItem { Name = "Feast", MediaTypeId = movies.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.MediaItems.AddRange(t1, t2);
        _db.SaveChanges();
        _titleId = t1.Id; _otherTitleId = t2.Id;

        var provider = new Mock<IMetadataProvider>();
        provider.Setup(p => p.GetSupportedMediaTypes()).Returns([new MediaTypeSupport { MediaTypeName = "people" }]);
        var registry = new Mock<IPluginRegistry>();
        registry.Setup(r => r.GetMetadataProviderEntries()).Returns([("chronicle.plugin.tmdb", provider.Object, null)]);
        _svc = new PersonResolutionService(registry.Object, Mock.Of<IMetadataResolutionService>(), NullLogger<PersonResolutionService>.Instance);
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private MediaItem Person(string name, params (string Source, string Id)[] ids)
    {
        var peopleType = _db.MediaTypes.Single(t => t.Name == "people").Id;
        var p = new MediaItem
        {
            Name = name, NormalizedName = Chronicle.Core.Helpers.MediaItemNormalizer.NormalizeName(name), MediaTypeId = peopleType,
            IsStub = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.MediaItems.Add(p);
        _db.SaveChanges();
        foreach (var (s, x) in ids) _db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = p.Id, Source = s, ExternalId = x });
        _db.SaveChanges();
        return p;
    }

    private void CreditedOnTitle(MediaItem person, int titleId, string source, string? id, string role, string? character)
    {
        _db.MediaCredits.Add(new MediaCredit
        {
            MediaItemId = titleId, PersonMediaItemId = person.Id, PersonName = person.Name, Source = source,
            ExternalPersonId = id, Role = role, CharacterName = character,
        });
        _db.SaveChanges();
    }

    private async Task Tmdb(string name, string id, string role, string? character, int? title = null)
    {
        await _svc.ResolveAndRecordCreditsAsync(_db, title ?? _titleId, [new CreditToRecord(name, id, null, role, character, 0)], "tmdb");
        await _db.SaveChangesAsync();
    }

    private int People => _db.MediaItems.Count(m => m.MediaType!.IsPersonType);

    [Fact]
    public async Task ACreditWithAnId_AndNoTitleEvidence_DoesNotAttachToASameNamedPerson()
    {
        var actor = Person("Michael J. Fox", ("imdb", "imdb:nm0000150"));
        CreditedOnTitle(actor, _titleId, "imdb", "imdb:nm0000150", "Actor", "Marty McFly");   // credited on a DIFFERENT title than Feast

        await Tmdb("Michael J. Fox", "tmdb:1399286", "ADR & Dubbing", null, title: _otherTitleId);

        People.Should().Be(2, "a sound editor with the same name is not the actor");
        (await _db.MediaExternalIds.AnyAsync(e => e.MediaItemId == actor.Id && e.Source == "tmdb")).Should().BeFalse();
    }

    [Fact]
    public async Task ACreditAnotherProviderAlreadyMadeOnThisTitle_JoinsThatPerson()
    {
        var actor = Person("Michael J. Fox", ("imdb", "imdb:nm0000150"));
        CreditedOnTitle(actor, _titleId, "imdb", "imdb:nm0000150", "Actor", "Marty McFly");

        await Tmdb("Michael J. Fox", "tmdb:521", "Actor", "Marty McFly / Marty McFly Junior / Marlene McFly");

        People.Should().Be(1);
        (await _db.MediaExternalIds.SingleAsync(e => e.MediaItemId == actor.Id && e.Source == "tmdb")).ExternalId.Should().Be("tmdb:521");
    }

    [Fact]
    public async Task AShortenedFirstName_WithTheSameSurnameAndCharacter_JoinsThatPerson()
    {
        var tom = Person("Tom Wilson", ("imdb", "imdb:nm0001855"));
        CreditedOnTitle(tom, _titleId, "imdb", "imdb:nm0001855", "Actor", "Biff Tannen");

        await Tmdb("Thomas F. Wilson", "tmdb:1065", "Actor", "Biff Tannen / Griff");

        People.Should().Be(1);
        (await _db.MediaExternalIds.AnyAsync(e => e.MediaItemId == tom.Id && e.ExternalId == "tmdb:1065")).Should().BeTrue();
    }

    [Fact]
    public async Task ADifferentRoleOnTheTitle_IsNotEvidence()
    {
        var producer = Person("Pat Doe", ("imdb", "imdb:nm0000009"));
        CreditedOnTitle(producer, _titleId, "imdb", "imdb:nm0000009", "Producer", null);

        await Tmdb("Pat Doe", "tmdb:77", "Actor", "Sam");

        People.Should().Be(2);
    }

    [Fact]
    public async Task ASecondPersonWithTheSameNameAndAnotherIdFromTheSameProvider_StaysSeparate()
    {
        var actor = Person("Chris Evans", ("imdb", "imdb:nm0262635"));
        CreditedOnTitle(actor, _titleId, "imdb", "imdb:nm0262635", "Actor", "Steve Rogers");
        await Tmdb("Chris Evans", "tmdb:16828", "Actor", "Steve Rogers");   // joins the actor

        await Tmdb("Chris Evans", "tmdb:55555", "Actor", "Bartender");      // another real Chris Evans: the actor already holds a TMDB id

        People.Should().Be(2);
    }

    [Fact]
    public async Task ACreditWithNoId_StillMatchesByName()
    {
        var jane = Person("Jane Doe");
        await _svc.ResolveAndRecordCreditsAsync(_db, _titleId, [new CreditToRecord("Jane Doe", null, null, "Writer", null, 0)], "omdb");
        await _db.SaveChangesAsync();

        People.Should().Be(1);
        (await _db.MediaCredits.SingleAsync()).PersonMediaItemId.Should().Be(jane.Id);
    }
}
