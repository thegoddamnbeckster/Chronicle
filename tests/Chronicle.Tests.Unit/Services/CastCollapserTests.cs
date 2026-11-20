using Chronicle.Core.Models;
using Chronicle.Services;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// A title's cast list shows one entry per real person. Live (2026-11-19): Back to the Future Part II listed Michael J. Fox twice,
/// one person record from TMDB's credits and one from IMDb's, because Chronicle had no proof yet that they were one person.
/// </summary>
public class CastCollapserTests
{
    private static MediaItem Person(int id, string name, string? poster = null) => new() { Id = id, Name = name, PosterUrl = poster };

    private static MediaCredit Credit(int id, MediaItem person, string source, string character, int billing = 0, string role = "Actor") => new()
    {
        Id = id, PersonMediaItemId = person.Id, PersonMediaItem = person, PersonName = person.Name,
        Source = source, Role = role, CharacterName = character, BillingOrder = billing,
    };

    [Fact]
    public void TheSamePersonFromTwoProviders_IsOneEntry_WithEveryCharacterOnce()
    {
        var tmdb = Person(741036, "Michael J. Fox", "photo");
        var imdb = Person(738205, "Michael J. Fox", "other");
        var credits = new[]
        {
            Credit(1, tmdb, "tmdb", "Marty McFly / Marty McFly Junior / Marlene McFly"),
            Credit(2, imdb, "imdb", "Marty McFly", 0),
            Credit(3, imdb, "imdb", "Marty McFly Junior", 1),
            Credit(4, imdb, "imdb", "Marlene McFly", 2),
        };

        var cast = CastCollapser.Collapse(credits);

        cast.Should().ContainSingle();
        cast[0].CharacterName.Should().Be("Marty McFly / Marty McFly Junior / Marlene McFly");
    }

    [Fact]
    public void TwoPeopleWithOneNameFromTheSameProvider_AndDifferentCharacters_StayTwo()
    {
        var a = Person(1, "Chris Evans");
        var b = Person(2, "Chris Evans");
        var credits = new[] { Credit(1, a, "tmdb", "Steve Rogers"), Credit(2, b, "tmdb", "Bartender", 5) };

        CastCollapser.Collapse(credits).Should().HaveCount(2);
    }

    [Fact]
    public void TheSameCharacterUnderTwoRecordsOfOneProvider_IsOnePerson()
    {
        // TMDB itself sometimes holds one actor under two person ids.
        var a = Person(1, "Rob Price");
        var b = Person(2, "Rob Price");
        var credits = new[] { Credit(1, a, "tmdb", "Dave"), Credit(2, b, "tmdb", "Dave") };

        CastCollapser.Collapse(credits).Should().ContainSingle();
    }

    [Fact]
    public void DifferentNames_AreNeverFolded_EvenWithAnIdenticalCharacter()
    {
        var a = Person(1, "Ann Lee");
        var b = Person(2, "Ann Law");
        var credits = new[] { Credit(1, a, "tmdb", "Mum"), Credit(2, b, "imdb", "Mum") };

        CastCollapser.Collapse(credits).Should().HaveCount(2);
    }

    [Fact]
    public void AShortenedGivenName_FromAnotherProvider_WithTheSameCharacters_IsOnePerson()
    {
        var tmdb = Person(701504, "Thomas F. Wilson", "photo");
        var imdb = Person(882134, "Tom Wilson");
        var credits = new[]
        {
            Credit(1, tmdb, "tmdb", "Biff Tannen / Griff", 3),
            Credit(2, imdb, "imdb", "Biff Tannen", 5),
            Credit(3, imdb, "imdb", "Griff", 6),
        };

        var cast = CastCollapser.Collapse(credits);

        cast.Should().ContainSingle();
        cast[0].Person.Id.Should().Be(701504);
        cast[0].CharacterName.Should().Be("Biff Tannen / Griff");
    }

    [Fact]
    public void ADifferentGivenName_WithoutAShared_Character_OrFromTheSameProvider_StaysTwo()
    {
        var a = Person(1, "Tom Wilson");
        var b = Person(2, "Thomas Wilson");
        // same provider on both: two people, whatever they are called
        CastCollapser.Collapse([Credit(1, a, "tmdb", "Biff"), Credit(2, b, "tmdb", "Biff")]).Should().HaveCount(2);
        // different providers but different characters: not enough
        CastCollapser.Collapse([Credit(1, a, "tmdb", "Biff"), Credit(2, b, "imdb", "Marty")]).Should().HaveCount(2);
        // a different surname never folds
        var c = Person(3, "Tom Wills");
        CastCollapser.Collapse([Credit(1, a, "tmdb", "Biff"), Credit(2, c, "imdb", "Biff")]).Should().HaveCount(2);
    }

    [Fact]
    public void ARecordWithAPhoto_IsTheOneShown()
    {
        var bare = Person(1, "Pat Doe");
        var photo = Person(2, "Pat Doe", "https://x/p.jpg");
        var credits = new[] { Credit(1, bare, "imdb", "Sam"), Credit(2, photo, "tmdb", "Sam") };

        CastCollapser.Collapse(credits).Single().Person.Id.Should().Be(2);
    }

    [Fact]
    public void ASingleRecord_KeepsItsOwnWordingAndRoles()
    {
        var p = Person(1, "Kim Roe");
        var credits = new[] { Credit(1, p, "tmdb", "Boss / Boss Jr.", 3), Credit(2, p, "tmdb", "", 3, "Producer") };

        var entry = CastCollapser.Collapse(credits).Single();

        entry.CharacterName.Should().Be("Boss / Boss Jr.");
        entry.Roles.Should().Equal("Actor", "Producer");
        entry.BillingOrder.Should().Be(3);
    }

    [Fact]
    public void FoldedEntries_KeepBillingOrder()
    {
        var star = Person(1, "Star Name");
        var starAlt = Person(2, "Star Name");
        var extra = Person(3, "Extra Name");
        var credits = new[]
        {
            Credit(1, extra, "tmdb", "Man", 9),
            Credit(2, star, "tmdb", "Hero", 0),
            Credit(3, starAlt, "imdb", "Hero", 1),
        };

        CastCollapser.Collapse(credits).Select(e => e.Person.Name).Should().Equal("Star Name", "Extra Name");
    }
}
