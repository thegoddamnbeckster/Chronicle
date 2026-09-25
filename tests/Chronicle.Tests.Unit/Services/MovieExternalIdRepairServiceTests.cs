using Chronicle.Core.Models;
using Chronicle.Services;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// Root-caused live (2026-09-24): the 1974 "The Longest Yard" item carried the 2005 remake's ids,
/// so both Add Media results linked to it. Foreign ids are those no provider attested for the item.
/// </summary>
public class MovieExternalIdRepairServiceTests
{
    private static MediaExternalId Row(string source, string id) => new() { MediaItemId = 1, Source = source, ExternalId = id };

    private const string Corpus = """{"chronicle.plugin.tmdb":{"externalId":"movie:4985","extendedData":{"ids":{"imdb":"tt0071771"}}}}""" + "\nsimkl:movie:58746";

    [Fact]
    public void ForeignIds_AreDetached_AttestedOnesKept()
    {
        var rows = new[]
        {
            Row("tmdb", "movie:4985"), Row("tmdb", "movie:9291"),
            Row("imdb", "tt0071771"), Row("imdb", "tt0398165"),
            Row("simkl", "simkl:58746"), Row("simkl", "simkl:62022"),
        };

        var foreign = MovieExternalIdRepairService.FindForeignIds(rows, Corpus);

        Assert.Equal(new[] { "movie:9291", "tt0398165", "simkl:62022" }, foreign.Select(f => f.ExternalId).OrderBy(x => x == "movie:9291" ? 0 : x == "tt0398165" ? 1 : 2));
    }

    [Fact]
    public void SourceWithNoAttestedId_IsLeftAlone()
    {
        var rows = new[] { Row("tmdb", "movie:4985"), Row("tmdb", "movie:9291"), Row("tvdb", "12254"), Row("tvdb", "1609") };

        var foreign = MovieExternalIdRepairService.FindForeignIds(rows, Corpus);

        Assert.Single(foreign);
        Assert.Equal("movie:9291", foreign[0].ExternalId);
    }

    [Fact]
    public void ItemWithOnlyOneTmdbMovieId_IsNeverTouched()
    {
        var rows = new[] { Row("tmdb", "movie:4985"), Row("imdb", "tt0071771"), Row("imdb", "tt0398165") };
        Assert.Empty(MovieExternalIdRepairService.FindForeignIds(rows, Corpus));
    }

    [Fact]
    public void Attestation_DoesNotMatchInsideALongerNumber()
    {
        Assert.False(MovieExternalIdRepairService.IsAttested("movie:985", Corpus));
        Assert.True(MovieExternalIdRepairService.IsAttested("movie:4985", Corpus));
    }

    [Fact]
    public void ProvidersOwnEnrichmentId_OutranksACorpusThatAlsoMentionsTheForeignId()
    {
        // Live: Alien Apocalypse (2023) had a Fanart.tv partition carrying another film's id, which
        // made that film's TMDB id look attested by text alone. The TMDB enrichment row is decisive.
        var corpus = """{"chronicle.plugin.fanarttv":{"externalId":"movie:14907"}}""" + "\nmovie:1181709";
        var rows = new[] { Row("tmdb", "movie:1181709"), Row("tmdb", "movie:14907") };

        var foreign = MovieExternalIdRepairService.FindForeignIds(
            rows, corpus, new Dictionary<string, string> { ["tmdb"] = "movie:1181709" });

        Assert.Single(foreign);
        Assert.Equal("movie:14907", foreign[0].ExternalId);
    }

    [Theory]
    [InlineData("chronicle.plugin.thetvdb", "tvdb")]
    [InlineData("chronicle.plugin.tmdb", "tmdb")]
    public void SourceOfPlugin_MapsPluginIdsToExternalIdSources(string pluginId, string source) =>
        Assert.Equal(source, MovieExternalIdRepairService.SourceOfPlugin(pluginId));
}
