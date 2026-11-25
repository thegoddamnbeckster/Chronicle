using Chronicle.Plugins.Models;
using Chronicle.Services.Scores;
using FluentAssertions;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

public class ItemScoresTests
{
    private static PluginRatingSource Src(string key, double stored = 10, double scale = 10, string display = "outOf", string? star = null,
        string? variant = null, bool plain = false) =>
        new() { Key = key, DisplayName = key.ToUpperInvariant(), StoredScale = stored, Scale = scale, Display = display, Star = star, Variant = variant, PlainRating = plain };

    private static ScorePlugin Plugin(string id, params PluginRatingSource[] sources) => new(id, id.Split('.').Last(), sources);

    [Fact]
    public void Recorded1917Partitions_ShowImdbAndTmdbAndSimkl_InTheirOwnTerms()
    {
        const string json = """
        {"chronicle.plugin.simkl":{"rating":6.5},"chronicle.plugin.tmdb":{"rating":7.984},
         "chronicle.plugin.imdb":{"rating":8.2,"extendedData":{"votes":787753,"ratings":[{"key":"imdb","value":8.2,"scale":10,"votes":787753,"url":"https://www.imdb.com/title/tt8579674/"}]}},
         "chronicle.plugin.fanarttv":{"rating":null},"_resolved":{"rating":7.984}}
        """;
        var plugins = new[]
        {
            Plugin("chronicle.plugin.simkl", Src("simkl")),
            Plugin("chronicle.plugin.tmdb", Src("tmdb", scale: 100, display: "percent")),
            Plugin("chronicle.plugin.imdb", Src("imdb", star: "single")),
            Plugin("chronicle.plugin.fanarttv"),
        };

        var scores = ItemScores.Collect(json, plugins);

        scores.Select(s => s.Key).Should().BeEquivalentTo(["imdb", "simkl", "tmdb"]);
        var imdb = scores.Single(s => s.Key == "imdb");
        (imdb.Value, imdb.Scale, imdb.Votes, imdb.Star, imdb.Display).Should().Be((8.2, 10, 787753, "single", "outOf"));
        var tmdb = scores.Single(s => s.Key == "tmdb");
        tmdb.Value.Should().BeApproximately(79.84, 0.001);
        (tmdb.Scale, tmdb.Display, tmdb.Star).Should().Be((100, "percent", null));
        scores.Single(s => s.Key == "simkl").Value.Should().Be(6.5);
    }

    [Fact]
    public void PlainRating_IsConvertedFromTheStoredScaleToTheShownScale()
    {
        // Hardcover stores raw /5; MusicBrainz stores x2 (/10) but its site shows /5; AniList stores /10 of a /100 score.
        var plugins = new[]
        {
            Plugin("chronicle.plugin.hardcover", Src("hardcover", stored: 5, scale: 5)),
            Plugin("chronicle.plugin.musicbrainz", Src("musicbrainz", stored: 10, scale: 5)),
            Plugin("chronicle.plugin.anilist", Src("anilist", stored: 10, scale: 100, display: "percent")),
        };
        var scores = ItemScores.Collect("""
            {"chronicle.plugin.hardcover":{"rating":4.2},"chronicle.plugin.musicbrainz":{"rating":8.4},"chronicle.plugin.anilist":{"rating":8.2}}
            """, plugins);

        scores.Single(s => s.Key == "hardcover").Value.Should().BeApproximately(4.2, 1e-9);
        scores.Single(s => s.Key == "musicbrainz").Value.Should().BeApproximately(4.2, 1e-9);
        scores.Single(s => s.Key == "anilist").Value.Should().BeApproximately(82, 1e-9);
    }

    [Fact]
    public void PluginWithNoDeclaredSource_ShowsNoPlainRating_ButAContractEntryAlwaysShows()
    {
        var json = """
            {"chronicle.plugin.fanedit":{"rating":4},"chronicle.plugin.other":{"rating":3,"extendedData":{"ratings":[{"key":"x","value":3,"scale":5}]}}}
            """;
        var scores = ItemScores.Collect(json, [Plugin("chronicle.plugin.fanedit"), Plugin("chronicle.plugin.other")]);

        scores.Should().ContainSingle().Which.Key.Should().Be("x");
        scores[0].Scale.Should().Be(5);
    }

    [Theory]
    [InlineData("""{"key":"a","value":"8.2/10","scale":10}""")]   // OMDb-style strings
    [InlineData("""{"key":"a","value":8.2}""")]                    // no scale
    [InlineData("""{"key":"a","value":0,"scale":10}""")]
    [InlineData("""{"key":"a","value":-1,"scale":10}""")]
    [InlineData("""{"key":"a","value":11,"scale":10}""")]         // above its scale: a wrong scale, not clamped
    [InlineData("""{"value":8,"scale":10}""")]                     // no key
    [InlineData("""{"key":"a","value":8,"scale":0}""")]
    public void MalformedContractEntries_AreSkipped(string entry)
    {
        var json = "{\"chronicle.plugin.p\":{\"extendedData\":{\"ratings\":[" + entry + "]}}}";
        ItemScores.Collect(json, [Plugin("chronicle.plugin.p", Src("a"))]).Should().BeEmpty();
    }

    [Fact]
    public void BadInput_NeverThrows()
    {
        var p = new[] { Plugin("chronicle.plugin.p", Src("a")) };
        ItemScores.Collect(null, p).Should().BeEmpty();
        ItemScores.Collect("", p).Should().BeEmpty();
        ItemScores.Collect("not json", p).Should().BeEmpty();
        ItemScores.Collect("[1,2]", p).Should().BeEmpty();
        ItemScores.Collect("""{"chronicle.plugin.p":{"rating":"high"}}""", p).Should().BeEmpty();
    }

    [Fact]
    public void Votes_AreReadFromTheEntry_ThenExtendedData_ThenThePartition()
    {
        var p = new[] { Plugin("chronicle.plugin.p", Src("a", plain: true), Src("b"), Src("c")) };
        var json = """
            {"chronicle.plugin.p":{"votes":3,"extendedData":{"votes":2,"ratings":[
              {"key":"a","value":5,"scale":10,"votes":1},{"key":"b","value":5,"scale":10},{"key":"c","value":5,"scale":10}]}}}
            """;
        var s = ItemScores.Collect(json, p);
        s.Single(x => x.Key == "a").Votes.Should().Be(1);
        s.Single(x => x.Key == "b").Votes.Should().Be(2);
        ItemScores.Collect("""{"chronicle.plugin.p":{"votes":3,"extendedData":{"ratings":[{"key":"c","value":5,"scale":10}]}}}""", p)
            .Single().Votes.Should().Be(3);
    }

    [Fact]
    public void PlainRating_TakesItsVoteCountFromTheFieldTheManifestNames()
    {
        var src = Src("h", stored: 5, scale: 5); src.VotesField = "ratings_count";
        var json = """{"chronicle.plugin.p":{"rating":4.3,"extendedData":{"ratings_count":7609}}}""";
        ItemScores.Collect(json, [Plugin("chronicle.plugin.p", src)]).Single().Votes.Should().Be(7609);
        ItemScores.Collect(json, [Plugin("chronicle.plugin.p", Src("h", stored: 5, scale: 5))]).Single().Votes.Should().BeNull();
    }

    [Fact]
    public void SameMeasurementFromSeveralPlugins_ShowsOnce_FromTheHighestPrecedencePlugin_AndNamesTheOthers()
    {
        var plugins = new[]
        {
            Plugin("chronicle.plugin.omdb", Src("omdb", plain: true), Src("imdb")),
            Plugin("chronicle.plugin.imdb", Src("imdb")),
            Plugin("chronicle.plugin.simkl", Src("simkl", plain: true), Src("imdb")),
        };
        var entry = """{"key":"imdb","value":8.2,"scale":10}""";
        var json = "{\"chronicle.plugin.omdb\":{\"extendedData\":{\"ratings\":[" + entry + "]}},"
                 + "\"chronicle.plugin.imdb\":{\"extendedData\":{\"ratings\":[" + entry + "]}},"
                 + "\"chronicle.plugin.simkl\":{\"extendedData\":{\"ratings\":[" + entry + "]}}}";

        var s = ItemScores.Collect(json, plugins, ["chronicle.plugin.simkl", "chronicle.plugin.imdb", "chronicle.plugin.omdb"]).Single();
        s.Source.Should().Be("simkl");              // first in the type's plugin order wins
        s.AlsoFrom.Should().BeEquivalentTo(["imdb", "omdb"]);

        // Not in the saved order: alphabetical by plugin id decides, so the result is stable.
        ItemScores.Collect(json, plugins).Single().Source.Should().Be("imdb");
    }

    [Fact]
    public void VariantsOfOneSource_AreSeparateScores()
    {
        var plugins = new[] { Plugin("chronicle.plugin.p", Src("rt", variant: "critics"), Src("rt", variant: "audience")) };
        var json = """
            {"chronicle.plugin.p":{"extendedData":{"ratings":[
            {"key":"rt","variant":"critics","value":88,"scale":100},{"key":"rt","variant":"audience","value":88,"scale":100}]}}}
            """;
        ItemScores.Collect(json, plugins).Select(x => x.Variant).Should().BeEquivalentTo(["critics", "audience"]);
    }

    [Fact]
    public void PlainRating_IsNotAddedWhenTheSameSourceGaveContractEntries()
    {
        var json = """
            {"chronicle.plugin.igdb":{"rating":9.4,"extendedData":{"ratings":[{"key":"igdb","variant":"users","value":9.4,"scale":10},{"key":"igdb","variant":"critics","value":9.2,"scale":10}]}}}
            """;
        ItemScores.Collect(json, [Plugin("chronicle.plugin.igdb", Src("igdb", plain: true))]).Select(x => x.Variant).Should().BeEquivalentTo(["users", "critics"]);
    }

    [Fact]
    public void Order_FollowsTheSavedPluginOrder_ThenAlphabetical()
    {
        var plugins = new[] { Plugin("chronicle.plugin.a", Src("a")), Plugin("chronicle.plugin.b", Src("b")), Plugin("chronicle.plugin.c", Src("c")) };
        var json = """{"chronicle.plugin.a":{"rating":1},"chronicle.plugin.b":{"rating":2},"chronicle.plugin.c":{"rating":3}}""";
        ItemScores.Collect(json, plugins, ["chronicle.plugin.c"]).Select(x => x.Key).Should().Equal("c", "a", "b");
    }

    [Fact]
    public void ADisabledOrRemovedPlugin_IsAbsentFromTheInput_SoItsScoreIsNotShown()
    {
        var json = """{"chronicle.plugin.gone":{"rating":9}}""";
        ItemScores.Collect(json, [Plugin("chronicle.plugin.other", Src("o"))]).Should().BeEmpty();
    }

    [Fact]
    public void OnlyHttpLinksSurvive_AndStarIsOnlyWhatTheManifestDeclared()
    {
        var plugins = new[] { Plugin("chronicle.plugin.p", Src("a", star: "sparkle")) };
        var json = """{"chronicle.plugin.p":{"extendedData":{"ratings":[{"key":"a","value":5,"scale":10,"url":"javascript:alert(1)"}]}}}""";
        var s = ItemScores.Collect(json, plugins).Single();
        s.Url.Should().BeNull();
        s.Star.Should().BeNull();
    }

    private const string OneScore = """{"chronicle.plugin.p":{"extendedData":{"ratings":[{"key":"a","value":80,"scale":100}]}}}""";

    [Fact]
    public void ALookIsOnlyOneTheHostHas_AndAnythingElseIsPlain()
    {
        ItemScores.Collect(OneScore, [Plugin("chronicle.plugin.p", new PluginRatingSource { Key = "a", Look = "ring" })]).Single().Look.Should().Be("ring");
        ItemScores.Collect(OneScore, [Plugin("chronicle.plugin.p", new PluginRatingSource { Key = "a", Look = "BADGE" })]).Single().Look.Should().Be("badge");
        ItemScores.Collect(OneScore, [Plugin("chronicle.plugin.p", new PluginRatingSource { Key = "a", Look = "<script>" })]).Single().Look.Should().Be("plain");
        ItemScores.Collect(OneScore, [Plugin("chronicle.plugin.p", new PluginRatingSource { Key = "a" })]).Single().Look.Should().Be("plain");
    }

    [Fact]
    public void OnlyPlainHexColoursAndKnownIconsAndFontsReachThePage()
    {
        var src = new PluginRatingSource
        {
            Key = "a", Look = "badge", TextColor = "red; background:url(x)", FontFamily = "Comic Sans", Shape = "circle", Label = new string('x', 100),
            Thresholds =
            [
                new() { Min = 60, Color = "#00CE7A", Track = "#204529", Icon = "fresh" },
                new() { Min = 0, Color = "expression(alert(1))", Icon = "rotten" },
                new() { Min = 30, Color = "#FFBD3F", Icon = "../../evil" },
            ],
        };
        var s = ItemScores.Collect(OneScore, [Plugin("chronicle.plugin.p", src)]).Single();
        s.TextColor.Should().BeNull();
        s.FontFamily.Should().BeNull();
        s.Shape.Should().Be("circle");
        s.Label!.Length.Should().Be(40);
        s.Tiers!.Select(t => t.Min).Should().Equal(30, 60);          // the tier with a bad colour is dropped, the rest ordered
        s.Tiers[0].Icon.Should().BeNull();
        s.Tiers[1].Should().Be(new ScoreTier(60, "#00CE7A", "#204529", "fresh"));
    }

    [Fact]
    public void AKnownFontIsKept_AndALogoIsOnlyFlagged_NeverCarriedToThePage()
    {
        var src = new PluginRatingSource { Key = "a", FontFamily = "source sans 3", LogoUrl = "https://example.com/logo.svg" };
        var s = ItemScores.Collect(OneScore, [Plugin("chronicle.plugin.p", src)]).Single();
        s.FontFamily.Should().Be("Source Sans 3");
        s.HasLogo.Should().BeTrue();
    }

    [Fact]
    public void TheCollector_NamesNoPlugin()
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Chronicle.Services", "Scores"));
        Directory.Exists(dir).Should().BeTrue();
        foreach (var file in Directory.GetFiles(dir, "*.cs"))
        {
            var text = File.ReadAllText(file);
            foreach (var name in new[] { "imdb", "tmdb", "simkl", "hardcover", "musicbrainz", "anilist", "igdb", "omdb", "steam", "trakt", "tvmaze" })
                System.Text.RegularExpressions.Regex.IsMatch(System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^\s*///.*$|//.*$", ""),
                    name, System.Text.RegularExpressions.RegexOptions.IgnoreCase).Should().BeFalse($"{Path.GetFileName(file)} must not name '{name}'");
        }
    }
}
