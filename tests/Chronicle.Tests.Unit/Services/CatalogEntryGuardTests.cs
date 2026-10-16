using Chronicle.Core.Models;
using Chronicle.Services.Plugins;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Services;

public class CatalogEntryGuardTests
{
    private static PluginCatalogEntry Entry(string id = "chronicle.plugin.tmdb", string repo = "owner/Chronicle.Plugin.TMDB",
        string asset = "Chronicle.Plugin.TMDB.zip", string dll = "Chronicle.Plugin.TMDB.dll") =>
        new(id, "Name", "d", "a", null, repo, asset, dll, []);

    [Fact]
    public void AnOrdinaryEntry_Passes() => CatalogEntryGuard.Check(Entry()).Should().BeNull();

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a\b")]
    [InlineData("")]
    [InlineData("UPPER")]
    [InlineData("..")]
    public void AnUnsafePluginId_IsRejected(string id) => CatalogEntryGuard.Check(Entry(id: id)).Should().NotBeNull();

    [Theory]
    [InlineData("owner")]
    [InlineData("owner/../x")]
    [InlineData("owner/name/extra")]
    [InlineData("../owner/name")]
    [InlineData("owner/na me")]
    public void AnUnsafeRepo_IsRejected(string repo) => CatalogEntryGuard.Check(Entry(repo: repo)).Should().NotBeNull();

    [Theory]
    [InlineData("..\\x.zip")]
    [InlineData("a/b.zip")]
    [InlineData("a..zip")]
    [InlineData("")]
    public void AnUnsafeAssetOrDllName_IsRejected(string name)
    {
        CatalogEntryGuard.Check(Entry(asset: name)).Should().NotBeNull();
        CatalogEntryGuard.Check(Entry(dll: name)).Should().NotBeNull();
    }

    [Theory]
    [InlineData("https://api.github.com/repos/o/r/releases/assets/1", true)]
    [InlineData("http://api.github.com/repos/o/r/releases/assets/1", false)]
    [InlineData("https://evil.example.com/x", false)]
    [InlineData("https://api.github.com.evil.example.com/x", false)]
    [InlineData("https://user:pw@api.github.com/x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyGitHubsApiHostIsADownloadSource(string? url, bool expected) =>
        CatalogEntryGuard.IsGitHubApiUrl(url).Should().Be(expected);

    [Fact]
    public void EveryBuiltInCatalogEntry_Passes()
    {
        PluginCatalogSeeds.Entries.Should().NotBeEmpty("plugins.json is embedded in the build");
        foreach (var entry in PluginCatalogSeeds.Entries)
            CatalogEntryGuard.Check(Entry(id: entry.PluginId, repo: entry.GithubRepo)).Should().BeNull($"{entry.PluginId} is shipped with Chronicle");
    }
}
