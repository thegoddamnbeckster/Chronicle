using Chronicle.Core.Models;
using Chronicle.Services;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Services;

[Collection("MediaTypeFamilies")]   // the snapshot is process-wide
public class MediaTypeFamiliesTests : IDisposable
{
    public MediaTypeFamiliesTests() => MediaTypeFamilies.Load([]);
    public void Dispose() => MediaTypeFamilies.Load([]);

    [Theory]
    [InlineData("tv", "tv")]
    [InlineData("anime", "tv")]
    [InlineData("anime_movies", "movie")]
    [InlineData("fanedits", "movie")]
    [InlineData("music", "music")]
    [InlineData("audiobooks", null)]
    [InlineData("books", null)]
    [InlineData("movies", "movie")]
    public void WithNothingStored_ANewTypeStartsWithTheFamilyItsNameImplied(string name, string? expected) =>
        ProviderFamilies.DefaultFor(name).Should().Be(expected);

    [Fact]
    public void AStoredFamily_WinsOverTheName()
    {
        MediaTypeFamilies.Load([("anime", "movie"), ("documentaries", "tv")]);

        MediaTypeFamilies.Resolve("anime").Should().Be("movie");
        MediaTypeFamilies.Resolve("ANIME").Should().Be("movie");
        MediaTypeFamilies.Resolve("documentaries").Should().Be("tv");
    }

    [Fact]
    public void AStoredNone_MeansNoFamily_EvenWhenTheNameWouldHaveGuessedOne()
    {
        MediaTypeFamilies.Load([("anime", null)]);

        MediaTypeFamilies.Resolve("anime").Should().BeNull();
    }

    [Fact]
    public void ATypeTheSnapshotHasNotSeen_FallsBackToTheDefault()
    {
        MediaTypeFamilies.Load([("books", null)]);

        MediaTypeFamilies.Resolve("anime").Should().Be("tv");
        MediaTypeFamilies.Resolve("books").Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NoName_NoFamily(string? name) => MediaTypeFamilies.Resolve(name).Should().BeNull();

    [Fact]
    public void Load_ToleratesDuplicateNames()
    {
        var act = () => MediaTypeFamilies.Load([("tv", "tv"), ("TV", "movie")]);

        act.Should().NotThrow();
        MediaTypeFamilies.Resolve("tv").Should().Be("tv");
    }

    [Theory]
    [InlineData("movies", true, false)]
    [InlineData("fanedits", true, false)]
    [InlineData("anime_movies", true, false)]
    [InlineData("tv", false, true)]
    [InlineData("anime", false, true)]
    [InlineData("music", false, false)]
    [InlineData("books", false, false)]
    public void MovieLikeAndShowLike_FollowTheFamily_ForTheBuiltInTypes(string name, bool movie, bool show)
    {
        MediaTypeFamilies.IsMovieLike(name).Should().Be(movie);
        MediaTypeFamilies.IsShowLike(name).Should().Be(show);
        MediaTypeFamilies.IsVideoLibraryType(name).Should().Be(movie || show);
    }

    [Fact]
    public void AUserMadeType_IsMovieLike_WhenItsFamilyIsMovie_WhateverItIsCalled()
    {
        MediaTypeFamilies.Load([("home_videos", "movie"), ("sitcoms", "tv"), ("movies", null)]);

        MediaTypeFamilies.IsMovieLike("home_videos").Should().BeTrue();
        MediaTypeFamilies.IsShowLike("sitcoms").Should().BeTrue();
        MediaTypeFamilies.IsMovieLike("movies").Should().BeFalse("an administrator cleared its family");
        MediaTypeFamilies.IsMovieLike(null).Should().BeFalse();
    }

    [Theory]
    [InlineData("music", "Band Members")]
    [InlineData("Audiobooks", "Narrators")]
    [InlineData("audiobook", "Narrators")]
    [InlineData("movies", null)]
    public void CastHeadingDefaults(string name, string? expected) =>
        ProviderFamilies.DefaultCastHeadingFor(name).Should().Be(expected);

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

/// <summary>The family snapshot is process-wide, so tests that replace it must not run beside any other test.</summary>
[CollectionDefinition("MediaTypeFamilies", DisableParallelization = true)]
public class MediaTypeFamiliesCollection;
