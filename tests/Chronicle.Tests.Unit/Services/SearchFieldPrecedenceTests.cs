using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// Add Media combines each title's answers from several providers into one card. Every field of the card follows the owner's
/// Metadata Assignment for the type, and a source with a small request quota is held back until the text has settled.
/// </summary>
public class SearchFieldPrecedenceTests
{
    private const string Tmdb = "chronicle.plugin.tmdb", Imdb = "chronicle.plugin.imdb", Wiki = "chronicle.plugin.wikipedia", Simkl = "chronicle.plugin.simkl";

    private static IMetadataProvider P(string id) { var m = new Mock<IMetadataProvider>(); m.SetupGet(x => x.PluginId).Returns(id); return m.Object; }

    private static (IMetadataProvider Provider, ScoredCandidate Candidate) M(string pluginId, string? overview = null, string? poster = null, double? rating = null) =>
        (P(pluginId), new ScoredCandidate(new MediaMetadata { ExternalId = pluginId + ":1", Title = "The Godfather", Year = 1972, Overview = overview, PosterUrl = poster, Rating = rating }, 80));

    private const string Config =
        "{\"movies\":{\"overview\":[\"chronicle.plugin.tmdb\",\"chronicle.plugin.wikipedia\"],\"poster_url\":[\"chronicle.plugin.tmdb\",\"chronicle.plugin.wikipedia\"],\"rating\":[\"chronicle.plugin.imdb\",\"chronicle.plugin.tmdb\"]}}";

    [Fact]
    public void Each_field_comes_from_the_provider_the_owner_ranks_first_for_that_field()
    {
        var members = new[] { M(Wiki, "wiki text", "wiki.jpg", 5.0), M(Tmdb, "tmdb text", "tmdb.jpg", 8.7), M(Imdb, null, null, 9.2) };
        var order = FieldPrecedence.For(Config, "movies");

        order.Pick(members, "overview", x => x.Metadata.Overview, s => !string.IsNullOrWhiteSpace(s)).Should().Be("tmdb text");
        order.Pick(members, "poster_url", x => x.Metadata.PosterUrl, s => !string.IsNullOrEmpty(s)).Should().Be("tmdb.jpg");
        order.Pick(members, "rating", x => x.Metadata.Rating, v => v.HasValue).Should().Be(9.2);   // IMDb is ranked first for rating
    }

    [Fact]
    public void A_field_the_top_provider_lacks_falls_to_the_next_one_ranked()
    {
        var members = new[] { M(Tmdb, overview: null), M(Wiki, "wiki text") };
        FieldPrecedence.For(Config, "movies").Pick(members, "overview", x => x.Metadata.Overview, s => !string.IsNullOrWhiteSpace(s))
            .Should().Be("wiki text");
    }

    [Fact]
    public void A_provider_the_owner_did_not_list_still_fills_a_field_nobody_listed_has()
    {
        var members = new[] { M(Simkl, "simkl text"), M(Tmdb, overview: null) };
        FieldPrecedence.For(Config, "movies").Pick(members, "overview", x => x.Metadata.Overview, s => !string.IsNullOrWhiteSpace(s))
            .Should().Be("simkl text");
    }

    [Fact]
    public void An_unlisted_provider_ranks_after_every_listed_one()
    {
        var members = new[] { M(Simkl, "simkl text"), M(Wiki, "wiki text") };
        FieldPrecedence.For(Config, "movies").Pick(members, "overview", x => x.Metadata.Overview, s => !string.IsNullOrWhiteSpace(s))
            .Should().Be("wiki text");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"tv\":{\"overview\":[\"chronicle.plugin.tmdb\"]}}")]
    public void No_usable_precedence_keeps_the_order_the_providers_were_given_in(string? config)
    {
        var members = new[] { M(Wiki, "wiki text"), M(Tmdb, "tmdb text") };
        FieldPrecedence.For(config, "movies").Pick(members, "overview", x => x.Metadata.Overview, s => !string.IsNullOrWhiteSpace(s))
            .Should().Be("wiki text");
    }

    [Fact]
    public void Nothing_to_pick_gives_the_default()
    {
        var members = new[] { M(Tmdb), M(Wiki) };
        FieldPrecedence.For(Config, "movies").Pick(members, "overview", x => x.Metadata.Overview, s => !string.IsNullOrWhiteSpace(s)).Should().BeNull();
    }

    [Fact]
    public void While_typing_the_quota_limited_source_is_left_out()
    {
        var all = new[] { P(Tmdb), P(Simkl), P(Imdb) };
        FileScanService.ForSearchPass(all, includeQuotaLimited: false).Select(p => p.PluginId).Should().Equal(Tmdb, Imdb);
    }

    [Fact]
    public void Once_settled_every_source_is_searched()
    {
        var all = new[] { P(Tmdb), P(Simkl), P(Imdb) };
        FileScanService.ForSearchPass(all, includeQuotaLimited: true).Select(p => p.PluginId).Should().Equal(Tmdb, Simkl, Imdb);
    }

    [Fact]
    public void When_the_quota_limited_source_is_the_only_one_it_is_searched_anyway()
    {
        var only = new[] { P(Simkl) };
        FileScanService.ForSearchPass(only, includeQuotaLimited: false).Select(p => p.PluginId).Should().Equal(Simkl);
    }
}
