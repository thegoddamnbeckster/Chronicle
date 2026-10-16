using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Plugins.Models;
using Chronicle.Services;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Services;

public class ExternalIdPoliciesTests
{
    public ExternalIdPoliciesTests() => ExternalIdPolicies.LoadForCurrentContext(ExternalIdFixtures.ShippedPlugins());

    [Theory]
    [InlineData("imdb:tt0137523", "tmdb", "imdb", "tt0137523")]        // a prefix a provider declared shared goes to that provider's row, whoever reported it
    [InlineData("imdb:nm0000206", "tmdb", "imdb", "imdb:nm0000206")]   // a person id keeps its prefix
    [InlineData("person:42", "tmdb", "tmdb", "tmdb:42")]               // an entity prefix the provider stores in another form
    [InlineData("movie:550", "tmdb", "tmdb", "movie:550")]
    [InlineData("person:42", "hardcover", "hardcover", "person:42")]   // another provider's "person:" is not rewritten
    [InlineData("b123", "hardcover", "hardcover", "b123")]
    public void ARawId_IsFiledAsItsProviderDeclared(string raw, string caller, string source, string id) =>
        ExternalIdPolicies.Normalize(raw, caller).Should().Be((source, id));

    [Fact]
    public void WithNoCaller_OnlyADeclaredSharedPrefixIsRecognised()
    {
        ExternalIdPolicies.Normalize("imdb:tt1", null).Should().Be(("imdb", "tt1"));
        ExternalIdPolicies.Normalize("movie:550", null).Should().BeNull();
    }

    [Fact]
    public void WithoutThePluginsInstalled_IdsAreStoredAsIssued()
    {
        ExternalIdPolicies.LoadForCurrentContext([]);

        ExternalIdPolicies.Normalize("imdb:tt1", "omdb").Should().Be(("omdb", "imdb:tt1"));
        ExternalIdPolicies.Normalize("person:42", "tmdb").Should().Be(("tmdb", "person:42"));
        ExternalIdPolicies.IsRefused("simkl", "simkl:12").Should().BeFalse();
        ExternalIdPolicies.IgnoredForConflicts("fanarttv").Should().BeFalse();
        ExternalIdPolicies.NameDerivedSource().Should().BeEmpty();
    }

    [Theory]
    [InlineData("simkl", "simkl:123", true)]
    [InlineData("simkl", "simkl:movie:123", false)]
    [InlineData("simkl", "simkl:123:s1e2", false)]
    [InlineData("tmdb", "simkl:123", false)]
    public void AnIdTheProviderDeclaredUnusable_IsRefused(string source, string id, bool refused) =>
        ExternalIdPolicies.IsRefused(source, id).Should().Be(refused);

    [Fact]
    public void TheFlagsAreReadFromWhatThePluginsDeclared()
    {
        ExternalIdPolicies.IgnoredForConflicts("fanarttv").Should().BeTrue();
        ExternalIdPolicies.IgnoredForConflicts("tmdb").Should().BeFalse();
        ExternalIdPolicies.IsNameDerived("WIKIPEDIA").Should().BeTrue();
        ExternalIdPolicies.NameDerivedSource().Should().Be("wikipedia");
        ExternalIdPolicies.NameDerivedPluginId().Should().Be(ExternalIdFixtures.WikipediaPluginId);
    }

    [Theory]
    [InlineData("tmdb", "603", true, true, null, "movie:603")]
    [InlineData("tmdb", "1396", true, false, null, "tv:1396")]
    [InlineData("tmdb", "tt1", false, false, null, null)]               // TMDB ids in a cross-reference are numbers
    [InlineData("imdb", "tt0137523", false, false, null, "imdb:tt0137523")]
    [InlineData("trakt", "7", true, true, null, "trakt:movie:7")]
    [InlineData("trakt", "7", true, false, null, "trakt:show:7")]
    [InlineData("simkl", "9", true, false, "anime", "simkl:anime:9")]   // the type the item was filed as picks the form
    [InlineData("simkl", "9", true, false, "tv", "simkl:tv:9")]
    [InlineData("simkl", "9", true, true, "anime", "simkl:movie:9")]    // a movie is a movie first
    [InlineData("tvdb", "81189", true, false, null, "tvdb:81189")]      // no declared policy: "source:value"
    [InlineData("tvdb", "abc", false, false, null, "tvdb:abc")]
    public void ACrossReference_IsWrittenInTheFormTheProviderDeclared(
        string source, string value, bool isNumber, bool isMovie, string? typeName, string? expected)
    {
        var json = isNumber ? JsonDocument.Parse(value).RootElement : JsonDocument.Parse($"\"{value}\"").RootElement;

        CrossRefHelper.FormatCrossRefId(source, json, isMovie, typeName).Should().Be(expected);
    }

    [Fact]
    public void TheCrossReferenceOfAMovieTypedItem_IsMovieShapedWithoutAnIdHint()
    {
        RegistryFixtures.LoadBuiltIns();
        var meta = new MediaMetadata { ExternalId = "603", ExtendedData = JsonDocument.Parse("{\"ids\":{\"tmdb\":603,\"imdb\":\"tt0133093\"}}").RootElement };

        var ids = CrossRefHelper.ExtractCrossRefIds(meta, "trakt", "movies");

        ids.Should().BeEquivalentTo(new[] { ("tmdb", "movie:603"), ("imdb", "imdb:tt0133093") });
    }

    [Fact]
    public void APluginsOwnPolicy_IsLeftOutOfItsOwnCrossReferences()
    {
        var meta = new MediaMetadata { ExternalId = "x", ExtendedData = JsonDocument.Parse("{\"ids\":{\"trakt\":5}}").RootElement };

        CrossRefHelper.ExtractCrossRefIds(meta, "trakt").Should().BeEmpty();
    }
}
