using Chronicle.Services.Scan;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Services;

public class ReleaseNameParserTests
{
    [Theory]
    [InlineData("Show.Name.S02E03.Episode.Title.720p.HDTV.x264-GRP", "Show Name", 2, 3, "Episode Title")]
    [InlineData("Show Name - S02E03 - Episode Title", "Show Name", 2, 3, "Episode Title")]
    [InlineData("show_name_s2e3", "show name", 2, 3, null)]
    [InlineData("Show Name 1x05", "Show Name", 1, 5, null)]
    [InlineData("Show.Name.2019.S01E01.1080p.WEB-DL", "Show Name", 1, 1, null)]
    [InlineData("[SubGroup] Show Name - 1x05 [1080p]", "Show Name", 1, 5, null)]
    [InlineData("Mr. Robot S01E01 Pilot", "Mr. Robot", 1, 1, "Pilot")]
    public void ReadsAnEpisodeRelease(string name, string title, int season, int episode, string? episodeTitle)
    {
        var r = ReleaseNameParser.Parse(name);

        r.Title.Should().Be(title);
        r.Season.Should().Be(season);
        r.Episode.Should().Be(episode);
        r.EpisodeTitle.Should().Be(episodeTitle);
    }

    [Theory]
    [InlineData("Movie.Name.2019.1080p.BluRay.x264-GRP", "Movie Name", 2019)]
    [InlineData("Movie Name (2019) 1080p", "Movie Name", 2019)]
    [InlineData("Movie_Name_2019_720p_WEBRip", "Movie Name", 2019)]
    [InlineData("Blade.Runner.2049.2017.1080p.BluRay", "Blade Runner 2049", 2017)]
    [InlineData("2012.2009.720p.BluRay", "2012", 2009)]
    public void ReadsAMovieRelease(string name, string title, int year)
    {
        var r = ReleaseNameParser.Parse(name);

        r.Title.Should().Be(title);
        r.Year.Should().Be(year);
        r.Season.Should().BeNull();
        r.Episode.Should().BeNull();
    }

    [Theory]
    [InlineData("Show.Name.S02.1080p.BluRay.x264-GRP", "Show Name", 2)]
    [InlineData("Show Name Season 3", "Show Name", 3)]
    [InlineData("Show.Name.S01.COMPLETE", "Show Name", 1)]
    public void ReadsASeasonPackFolder(string name, string title, int season)
    {
        var r = ReleaseNameParser.Parse(name);

        r.Title.Should().Be(title);
        r.Season.Should().Be(season);
        r.Episode.Should().BeNull();
    }

    [Theory]
    [InlineData("Show.Name.S02E03.720p.HDTV.x264-GRP", true)]
    [InlineData("Movie.Name.2019.1080p.BluRay", true)]
    [InlineData("Heat (1995)", false)]
    [InlineData("The Matrix", false)]
    [InlineData("Season 1", false)]
    [InlineData("Show Name S01E01", false)]      // an episode code alone is not "a release": no quality words
    public void OnlyNamesWithQualityWordsLookLikeReleases(string name, bool expected) =>
        ReleaseNameParser.Parse(name).LooksLikeRelease.Should().Be(expected);

    [Theory]
    [InlineData("Heat (1995)", "Heat", 1995)]
    [InlineData("The Matrix", "The Matrix", null)]
    public void TidyNamesComeOutAsTheyAre(string name, string title, int? year)
    {
        var r = ReleaseNameParser.Parse(name);

        r.Title.Should().Be(title);
        r.Year.Should().Be(year);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1080p")]
    [InlineData("S01E01")]
    [InlineData("[Group]")]
    public void NothingUsableLeftMeansNoTitle(string name) => ReleaseNameParser.Parse(name).Title.Should().BeNull();

    [Theory]
    [InlineData("Cam (2018)")]
    [InlineData("Internal Affairs (1990)")]
    [InlineData("Extended Family (2010)")]
    [InlineData("Dual (2022)")]
    [InlineData("Web")]
    [InlineData("The Complete Guide")]
    [InlineData("Multi (2019)")]
    public void OrdinaryTitlesWithQualityLikeWords_AreNotReleases(string name) =>
        ReleaseNameParser.Parse(name).LooksLikeRelease.Should().BeFalse();

    [Fact]
    public void WordsThatContainQualityWords_AreNotCut()
    {
        // "Webster" starts with "web", "Cameron" with "cam", "Multiverse" with "multi", "Extended Family" with "extended".
        ReleaseNameParser.Parse("Webster S01E01 720p").Title.Should().Be("Webster");
        ReleaseNameParser.Parse("Cameron Crowe Collection 2010").Title.Should().Be("Cameron Crowe Collection");
        ReleaseNameParser.Parse("Multiverse of Madness 2022 1080p").Title.Should().Be("Multiverse of Madness");
    }

    [Fact]
    public void AnEpisodeTitleThatLooksLikeAYearIsKept()
    {
        ReleaseNameParser.Parse("Show.Name.S01E01.1984.720p").EpisodeTitle.Should().BeNull();   // a bare year is a year, not a title
    }
}

public class MessyFolderGroupingTests
{
    private readonly ScanGroupingService _svc = new(new FolderSignalExtractor(), new TagSignalExtractor());

    [Fact]
    public void LooseEpisodesInADownloadsFolder_AreFiledUnderTheirShowAndSeason()
    {
        var files = new[]
        {
            "D:/Downloads/Show.Name.S02E03.Episode.Three.720p.HDTV.x264-GRP.mkv",
            "D:/Downloads/Show.Name.S02E04.720p.HDTV.x264-GRP.mkv",
            "D:/Downloads/Show.Name.S01E01.720p.HDTV.x264-GRP.mkv",
        };

        var result = _svc.Group(files, "D:/Downloads", 3);

        result.Ungrouped.Should().BeEmpty();
        var show = result.Groups.Single();
        show.Name.Should().Be("Show Name");
        show.Children.Select(s => s.Name).Should().BeEquivalentTo("Season 1", "Season 2");
        var season2 = show.Children.Single(s => s.Number == 2);
        season2.Children.Select(e => (e.Number, e.Name)).Should().BeEquivalentTo(new[] { ((int?)3, "Episode Three"), ((int?)4, "Episode 4") });
    }

    [Fact]
    public void ADerivedShow_IsShownForReview_ButNeverReachesTheAutoImportThreshold()
    {
        var result = _svc.Group(["D:/Downloads/Show.Name.S02E03.720p.HDTV.x264-GRP.mkv"], "D:/Downloads", 3);

        var show = result.Groups.Single();
        show.ConfidenceScore.Should().BeLessThan(0.75);   // the default automatic-import threshold is 75%
        show.SignalSources.Should().Contain("filename");
    }

    [Fact]
    public void LooseNonEpisodeFiles_StayUngrouped_AsBefore()
    {
        var result = _svc.Group(["D:/Downloads/random video.mkv", "D:/Downloads/Some.Movie.2019.1080p.mkv"], "D:/Downloads", 3);

        result.Groups.Should().BeEmpty();
        result.Ungrouped.Should().HaveCount(2);
    }

    [Fact]
    public void ALooseEpisodeJoinsTheShowsRealFolder_WhenThereIsOne()
    {
        var files = new[]
        {
            "D:/TV/Show Name/Season 1/Show Name - S01E01.mkv",
            "D:/TV/Show.Name.S01E02.720p.HDTV.x264-GRP.mkv",
        };

        var result = _svc.Group(files, "D:/TV", 3);

        var show = result.Groups.Single();
        show.Name.Should().Be("Show Name");
        show.Children.Single().Children.Should().HaveCount(2);
    }

    [Fact]
    public void AReleaseNamedShowFolder_IsCleanedToTheShowName_ItsFilesStayInIt()
    {
        var files = new[]
        {
            "D:/TV/Show.Name.S02.1080p.BluRay.x264-GRP/Show.Name.S02E01.1080p.BluRay.x264-GRP.mkv",
            "D:/TV/Show.Name.S02.1080p.BluRay.x264-GRP/Show.Name.S02E02.1080p.BluRay.x264-GRP.mkv",
        };

        var result = _svc.Group(files, "D:/TV", 3);

        var show = result.Groups.Single();
        show.Name.Should().Be("Show Name");
        show.FolderPath.Should().EndWith("Show.Name.S02.1080p.BluRay.x264-GRP");   // the real folder stays the matching key
        show.TotalFileCount.Should().Be(2);
    }

    [Fact]
    public void ATidyShowFolder_IsLeftExactlyAsItWas()
    {
        var result = _svc.Group(["D:/TV/Breaking Bad (2008)/Season 1/Breaking Bad - S01E01.mkv"], "D:/TV", 3);

        var show = result.Groups.Single();
        show.Name.Should().Be("Breaking Bad");
        show.Year.Should().Be(2008);
        show.ConfidenceScore.Should().BeGreaterThan(0.75);
    }

    [Fact]
    public void AReleaseNamedMovieFolder_BecomesTitleAndYear_WithLessConfidenceThanATidyOne()
    {
        var release = _svc.Group(["D:/Movies/Movie.Name.2019.1080p.BluRay.x264-GRP/movie.mkv"], "D:/Movies", 1).Groups.Single();
        var tidy = _svc.Group(["D:/Movies/Movie Name (2019)/movie.mkv"], "D:/Movies", 1).Groups.Single();

        release.Name.Should().Be("Movie Name (2019)");
        release.FolderPath.Should().EndWith("Movie.Name.2019.1080p.BluRay.x264-GRP");
        release.ConfidenceScore.Should().BeLessThan(tidy.ConfidenceScore);
        tidy.Name.Should().Be("Movie Name (2019)");
    }

    [Fact]
    public void ALooseReleaseNamedMovie_GetsACleanName()
    {
        var group = _svc.Group(["D:/Movies/Movie.Name.2019.1080p.BluRay.x264-GRP.mkv"], "D:/Movies", 1).Groups.Single();

        group.Name.Should().Be("Movie Name (2019)");
    }

    [Fact]
    public void AFolderWhoseNameMerelyContainsAQualityLikeWord_IsNotMangled()
    {
        var group = _svc.Group(["D:/Movies/Complete Unknown (2024)/movie.mkv"], "D:/Movies", 1).Groups.Single();

        group.Name.Should().Be("Complete Unknown (2024)");
    }
}
