using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Tests.Unit.Services;

/// <summary>Loads the registry the way a freshly migrated database would hold it: the built-in types plus the people type.</summary>
public static class RegistryFixtures
{
    public static MediaTypeInfo[] BuiltIns()
    {
        var types = BuiltInMediaTypes.Seed().Select(MediaTypeRegistry.ToInfo).ToList();
        types.Add(new MediaTypeInfo(10, "people", null, null, true, false, false, false, []));
        types.Add(new MediaTypeInfo(11, "anime", ProviderFamilies.Tv, null, false, false, false, true, ["anime_episode"], ImportShapes.Episodic));
        types.Add(new MediaTypeInfo(12, "fanedits", ProviderFamilies.Movie, null, false, false, false, true, []));
        types.Add(new MediaTypeInfo(13, "anime_movies", ProviderFamilies.Movie, null, false, false, false, true, []));
        types.Add(new MediaTypeInfo(14, "books", null, null, false, false, false, false, ["book"], ImportShapes.AuthorSeriesWork));
        types.Add(new MediaTypeInfo(15, "audiobooks", null, ScanStrategies.Audiobook, false, false, false, false, [], ImportShapes.AuthorSeriesWork));
        return [.. types];
    }

    public static void LoadBuiltIns() => MediaTypeRegistry.LoadForCurrentContext(BuiltIns());

    /// <summary>Makes the registry describe the types in this test's own database (what the application does at start).</summary>
    public static void LoadFrom(ChronicleDbContext db) =>
        MediaTypeRegistry.LoadForCurrentContext(db.MediaTypes.AsNoTracking().ToList().Select(MediaTypeRegistry.ToInfo));
}

public class MediaTypeRegistryTests
{
    public MediaTypeRegistryTests() => RegistryFixtures.LoadBuiltIns();

    [Theory]
    [InlineData("tv", "tv")]
    [InlineData("anime", "tv")]
    [InlineData("anime_movies", "movie")]
    [InlineData("fanedits", "movie")]
    [InlineData("music", "music")]
    [InlineData("audiobooks", null)]
    [InlineData("movies", "movie")]
    public void TheFamilyIsWhatTheRowSays(string name, string? expected) =>
        MediaTypeRegistry.Family(name).Should().Be(expected);

    [Fact]
    public void ANameTheSnapshotHasNotSeen_HasNoFamily_NeverAGuessFromItsSpelling()
    {
        MediaTypeRegistry.LoadForCurrentContext([]);

        MediaTypeRegistry.Family("anime").Should().BeNull();
        MediaTypeRegistry.Family("tv").Should().BeNull();
        MediaTypeRegistry.IsMovieLike("movies").Should().BeFalse();
    }

    [Fact]
    public void AStoredNone_MeansNoFamily()
    {
        MediaTypeRegistry.LoadForCurrentContext([new MediaTypeInfo(1, "anime", null, null, false, false, false, false, [])]);

        MediaTypeRegistry.Family("ANIME").Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NoName_NoFamily(string? name) => MediaTypeRegistry.Family(name).Should().BeNull();

    [Fact]
    public void Load_ToleratesDuplicateNames()
    {
        var act = () => MediaTypeRegistry.LoadForCurrentContext([
            new MediaTypeInfo(1, "tv", "tv", null, false, false, false, false, []),
            new MediaTypeInfo(2, "TV", "movie", null, false, false, false, false, [])]);

        act.Should().NotThrow();
        MediaTypeRegistry.Family("tv").Should().Be("tv");
    }

    [Theory]
    [InlineData("movies", true, false)]
    [InlineData("fanedits", true, false)]
    [InlineData("anime_movies", true, false)]
    [InlineData("tv", false, true)]
    [InlineData("anime", false, true)]
    [InlineData("music", false, false)]
    [InlineData("books", false, false)]
    public void MovieLikeAndShowLike_FollowTheFamily(string name, bool movie, bool show)
    {
        MediaTypeRegistry.IsMovieLike(name).Should().Be(movie);
        MediaTypeRegistry.IsShowLike(name).Should().Be(show);
        MediaTypeRegistry.IsVideoLibraryType(name).Should().Be(movie || show);
    }

    [Fact]
    public void AUserMadeType_IsMovieLike_WhenItsFamilyIsMovie_WhateverItIsCalled()
    {
        MediaTypeRegistry.LoadForCurrentContext([
            new MediaTypeInfo(1, "home_videos", "movie", null, false, false, false, false, []),
            new MediaTypeInfo(2, "sitcoms", "tv", null, false, false, false, false, []),
            new MediaTypeInfo(3, "movies", null, null, false, false, false, false, [])]);

        MediaTypeRegistry.IsMovieLike("home_videos").Should().BeTrue();
        MediaTypeRegistry.IsShowLike("sitcoms").Should().BeTrue();
        MediaTypeRegistry.IsMovieLike("movies").Should().BeFalse("an administrator cleared its family");
        MediaTypeRegistry.IsMovieLike(null).Should().BeFalse();
    }

    [Theory]
    [InlineData("film", "movies")]
    [InlineData("movie", "movies")]
    [InlineData("tv_episode", "tv")]
    [InlineData("episode", "tv")]
    [InlineData("anime_episode", "anime")]
    [InlineData("track", "music")]
    [InlineData("book", "books")]
    [InlineData("movies", "movies")]
    [InlineData("Comics", "comics")]
    public void AnAlias_StandsForTheTypeThatListsIt_AndAnUnknownNameIsLowercasedAsItIs(string name, string expected) =>
        MediaTypeRegistry.Canonical(name).Should().Be(expected);

    [Fact]
    public void ATypesOwnName_BeatsAnAliasOfAnotherType()
    {
        MediaTypeRegistry.LoadForCurrentContext([
            new MediaTypeInfo(1, "tv", null, null, false, false, false, false, ["episode"]),
            new MediaTypeInfo(2, "episode", null, null, false, false, false, false, [])]);

        MediaTypeRegistry.Canonical("episode").Should().Be("episode");
    }

    [Fact]
    public void TheRolesAreFoundByFlag_NotByName()
    {
        MediaTypeRegistry.IsPersonType("people").Should().BeTrue();
        MediaTypeRegistry.IsPersonTypeId(10).Should().BeTrue();
        MediaTypeRegistry.PersonType!.Name.Should().Be("people");
        MediaTypeRegistry.DefaultType!.Name.Should().Be("movies");
        MediaTypeRegistry.FamilyDefault(ProviderFamilies.Tv)!.Name.Should().Be("tv");

        MediaTypeRegistry.LoadForCurrentContext([new MediaTypeInfo(1, "everyone", null, null, true, true, false, false, [])]);
        MediaTypeRegistry.IsPersonType("everyone").Should().BeTrue("a renamed people type is still the people type");
        MediaTypeRegistry.IsPersonType("people").Should().BeFalse();
        MediaTypeRegistry.DefaultType!.Name.Should().Be("everyone");
    }

    [Theory]
    [InlineData("movies", "movie")]      // a family's default type goes by the family name to a provider
    [InlineData("tv", "tv")]
    [InlineData("music", "music")]
    [InlineData("fanedits", "fanedits")]
    [InlineData("anime", "anime")]
    [InlineData("Unknown", "unknown")]
    public void ProviderName_IsTheFamilyForTheFamilyDefault_AndTheOwnNameOtherwise(string name, string expected) =>
        MediaTypeRegistry.ProviderName(name).Should().Be(expected);

    [Fact]
    public void DatabaseNamesFor_ListsEveryTypeAProviderDeclarationReaches()
    {
        MediaTypeRegistry.DatabaseNamesFor("movie").Should().BeEquivalentTo("movie", "movies");
        MediaTypeRegistry.DatabaseNamesFor("anime").Should().BeEquivalentTo("anime");
        MediaTypeRegistry.ProviderDeclaresType("movie", "movies").Should().BeTrue();
        MediaTypeRegistry.ProviderDeclaresType("movies", "movies").Should().BeTrue();
        MediaTypeRegistry.ProviderDeclaresType("movie", "fanedits").Should().BeFalse();
        MediaTypeRegistry.ProviderDeclaresType("tv", "anime").Should().BeFalse("only a provider naming the type, or its family default, serves it directly");
    }

    [Fact]
    public void ScanStrategyImportShapeAndCastFlags_AreReadFromTheRow()
    {
        MediaTypeRegistry.UsesScanStrategy("audiobooks", ScanStrategies.Audiobook).Should().BeTrue();
        MediaTypeRegistry.UsesScanStrategy("books", ScanStrategies.Audiobook).Should().BeFalse();
        MediaTypeRegistry.HasImportShape("anime", ImportShapes.Episodic).Should().BeTrue();
        MediaTypeRegistry.HasImportShape("music", ImportShapes.ArtistAlbumTrack).Should().BeTrue();
        MediaTypeRegistry.HasImportShape("movies", ImportShapes.Episodic).Should().BeFalse();
        MediaTypeRegistry.CastAreActors("movies").Should().BeTrue();
        MediaTypeRegistry.CastAreActors("fanedits").Should().BeTrue();
        MediaTypeRegistry.CastAreActors("music").Should().BeFalse();
        MediaTypeRegistry.CastAreActors("audiobooks").Should().BeFalse();
        MediaTypeRegistry.CastAreActors(null).Should().BeFalse();
    }

    [Fact]
    public void SplitAliases_TrimsLowercasesAndDropsBlanks()
    {
        MediaTypeRegistry.SplitAliases(" Film , MOVIE,,film ").Should().Equal("film", "movie");
        MediaTypeRegistry.SplitAliases(null).Should().BeEmpty();
    }

    [Theory]
    [InlineData("tv", true)]
    [InlineData("movie", true)]
    [InlineData("my_family2", true)]
    [InlineData("TV", false)]
    [InlineData("t", false)]
    [InlineData("has space", false)]
    [InlineData("1abc", false)]
    public void TheRuleForAFamilyName(string family, bool valid)
    {
        var input = new MediaTypeInput("x1", "X", null, 1, ["Item"], "watched", "minutes", false, true, null, true, family, null);

        (MediaTypeRules.Validate(input, creating: true) is null).Should().Be(valid);
    }

    [Fact]
    public void ACastHeadingOverFortyCharacters_IsRefused()
    {
        var input = new MediaTypeInput("x1", "X", null, 1, ["Item"], "watched", "minutes", false, true, null, true, null, new string('a', 41));

        MediaTypeRules.Validate(input, creating: true).Should().Contain("40 characters");
    }
}
