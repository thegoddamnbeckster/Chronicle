using Chronicle.Core.Models;
using Chronicle.Services.Plugins;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Services;

public class PluginCatalogFilterTests
{
    private static PluginCatalogEntry Entry(params string[]? types) =>
        new("p", "P", "", "a", null, "o/r", "r.zip", "r.dll", [], SupportedMediaTypes: types);

    [Theory]
    [InlineData("movies", "movies", true)]
    [InlineData("movies", "movie", true)]       // catalogs say movies, providers say movie
    [InlineData("MOVIES", "movies", true)]
    [InlineData("tv", "movies", false)]
    [InlineData("music", "movies", false)]
    public void ANamedTypeMatchesItself_WithoutCaseOrPlural(string pluginSays, string asked, bool expected) =>
        PluginCatalogFilter.Handles(Entry(pluginSays), asked, null).Should().Be(expected);

    [Fact]
    public void ATypeIsAlsoHandledByItsProviderFamily()
    {
        PluginCatalogFilter.Handles(Entry("tv", "metadata"), "anime", "tv").Should().BeTrue();
        PluginCatalogFilter.Handles(Entry("tv", "metadata"), "anime", null).Should().BeFalse();
        PluginCatalogFilter.Handles(Entry("movies"), "anime", "tv").Should().BeFalse();
    }

    [Fact]
    public void APluginThatSaysNothingAboutTypes_IsNeverHidden()
    {
        PluginCatalogFilter.Handles(Entry(), "anime", "tv").Should().BeTrue();
        PluginCatalogFilter.Handles(Entry(null!), "anime", null).Should().BeTrue();
    }

    [Fact]
    public void AnyOneMatchIsEnough() =>
        PluginCatalogFilter.Handles(Entry("music", "audiobooks", "books"), "audiobook", null).Should().BeTrue();

    [Fact]
    public void ShortNamesAreNotMangled() =>
        PluginCatalogFilter.Handles(Entry("tv"), "tvs", null).Should().BeFalse();
}
