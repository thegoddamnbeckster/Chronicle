using Chronicle.Plugins;
using Chronicle.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>Add Media merges same-title results into the first provider listed, so the listing order is the owner's ranking, not registry order.</summary>
public class SearchProviderOrderTests
{
    private static IMetadataProvider P(string id) { var m = new Mock<IMetadataProvider>(); m.SetupGet(x => x.PluginId).Returns(id); return m.Object; }

    private static readonly string Config =
        "{\"game\":{\"title\":[\"chronicle.plugin.igdb\",\"chronicle.plugin.imdb\",\"chronicle.plugin.wikipedia\"]},\"movies\":{\"title\":[\"chronicle.plugin.tmdb\"]}}";

    [Fact]
    public void The_owners_ranking_for_the_type_decides_the_order()
    {
        var registry = new[] { P("chronicle.plugin.wikipedia"), P("chronicle.plugin.imdb"), P("chronicle.plugin.igdb") };
        FileScanService.OrderByAssignment(registry, Config, "game").Select(p => p.PluginId)
            .Should().Equal("chronicle.plugin.igdb", "chronicle.plugin.imdb", "chronicle.plugin.wikipedia");
    }

    [Fact]
    public void An_unranked_provider_follows_in_its_given_order()
    {
        var registry = new[] { P("z.other"), P("chronicle.plugin.imdb"), P("a.other"), P("chronicle.plugin.igdb") };
        FileScanService.OrderByAssignment(registry, Config, "game").Select(p => p.PluginId)
            .Should().Equal("chronicle.plugin.igdb", "chronicle.plugin.imdb", "z.other", "a.other");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"movies\":{\"title\":[\"chronicle.plugin.tmdb\"]}}")]
    [InlineData("[]")]
    public void No_usable_ranking_leaves_the_order_alone(string? config)
    {
        var registry = new[] { P("b"), P("a") };
        FileScanService.OrderByAssignment(registry, config, "game").Select(p => p.PluginId).Should().Equal("b", "a");
    }

    private static MetadataCandidate C(string title, int score, int? year = null, string src = "x") => new("id-" + title, title, year, null, null, null, score, src);

    [Fact]
    public void The_closest_title_is_first_whatever_provider_it_came_from_and_whatever_it_scored()
    {
        var merged = new List<MetadataCandidate>
        {
            C("Fallout: New Vegas downloadable content", 90, null, "wikipedia"),
            C("Fallout: New California", 95, null, "wikipedia"),
            C("The Making of Fallout: New Vegas", 80, 2011, "tmdb"),
            C("Fallout: New Vegas", 40, 2010, "igdb"),
            C("Fallout: New Vegas Ultimate Edition", 70, 2012, "igdb"),
        };

        FileScanService.Rank(merged, "Fallout New Vegas").Select(c => c.Title).Should().Equal(
            "Fallout: New Vegas",                              // exact, though its own score is lowest
            "Fallout: New Vegas downloadable content",         // starts with it; the provider scored it higher than the edition
            "Fallout: New Vegas Ultimate Edition",             // starts with it
            "The Making of Fallout: New Vegas",                // only contains it
            "Fallout: New California");                        // does not contain it at all
    }

    [Fact]
    public void Equal_closeness_falls_back_to_the_providers_score_then_to_the_owners_provider_order()
    {
        var merged = new List<MetadataCandidate> { C("Hades", 60, 2018, "a"), C("Hades", 90, 2020, "b"), C("Hades", 90, 1999, "c") };
        FileScanService.Rank(merged, "hades").Select(c => c.Source).Should().Equal("b", "c", "a");
    }

    [Fact]
    public void A_blank_query_or_odd_titles_never_throw_and_keep_the_given_order()
    {
        var merged = new List<MetadataCandidate> { C("b", 0), C("", 0), C("a", 0) };
        FileScanService.Rank(merged, "").Select(c => c.Title).Should().Equal("b", "", "a");
        FileScanService.Rank([], "x").Should().BeEmpty();
    }
}
