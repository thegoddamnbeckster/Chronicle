using System.Text.Json;
using Chronicle.Core.Helpers;
using FluentAssertions;
using Xunit;

namespace Chronicle.Tests.Unit.Core.Helpers;

public class PluginIdHelperTests
{
    [Theory]
    [InlineData("chronicle.plugin.tmdb", "tmdb")]
    [InlineData("chronicle.plugin.trakt", "trakt")]
    [InlineData("hardcover", "hardcover")]
    public void ToSource_ReturnsExpectedShortForm(string pluginId, string expected)
    {
        PluginIdHelper.ToSource(pluginId).Should().Be(expected);
    }

    private static Dictionary<string, JsonElement> ParseBlobs(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void FindProviderBlobKeys_ShortSourceCallerBlobKeyedByFullPluginId_MatchesViaInternalSourceProperty()
    {
        // Regression test for a real production data-corruption bug (2026-09-03):
        // MediaController.ClearExternalId used to match ONLY by exact dictionary-key equality
        // against the caller's `source` argument -- when a caller passed the short form
        // ("wikipedia") for a blob stored under the full plugin ID key
        // ("chronicle.plugin.wikipedia"), neither ever matched, so the stale (in the confirmed
        // live case, actively WRONG -- a different real person's) blob was never removed even
        // though the caller's request looked successful.
        var blobs = ParseBlobs("""
            {"chronicle.plugin.tmdb": {"source": "tmdb", "title": "Real Name"},
             "chronicle.plugin.wikipedia": {"source": "wikipedia", "title": "Wrong Match"}}
            """);

        var keys = PluginIdHelper.FindProviderBlobKeys(blobs, "wikipedia");

        keys.Should().BeEquivalentTo(["chronicle.plugin.wikipedia"]);
    }

    [Fact]
    public void FindProviderBlobKeys_FullPluginIdCaller_MatchesDirectlyByKey()
    {
        var blobs = ParseBlobs("""
            {"chronicle.plugin.tmdb": {"source": "tmdb"}}
            """);

        var keys = PluginIdHelper.FindProviderBlobKeys(blobs, "chronicle.plugin.tmdb");

        keys.Should().BeEquivalentTo(["chronicle.plugin.tmdb"]);
    }

    [Fact]
    public void FindProviderBlobKeys_BlobWithNoSourceProperty_FallsBackToKeyMatch()
    {
        // A blob stored under the old flat format (bare short key, no internal "source" field).
        var blobs = ParseBlobs("""{"fanedit": {"title": "Some Fan Edit"}}""");

        var keys = PluginIdHelper.FindProviderBlobKeys(blobs, "fanedit");

        keys.Should().BeEquivalentTo(["fanedit"]);
    }

    [Fact]
    public void FindProviderBlobKeys_NoMatchingProvider_ReturnsEmpty()
    {
        var blobs = ParseBlobs("""{"chronicle.plugin.tmdb": {"source": "tmdb"}}""");

        PluginIdHelper.FindProviderBlobKeys(blobs, "wikipedia").Should().BeEmpty();
    }

    [Fact]
    public void FindProviderBlobKeys_NeverMatchesReservedResolvedOrOverridesKeys()
    {
        // "_resolved"/"_overrides" are resolver-owned reserved keys (MetadataResolutionService),
        // never plugin data -- must never be treated as a provider blob to strip, even if their
        // shape happens to look like one.
        var blobs = ParseBlobs("""
            {"_resolved": {"source": "wikipedia"}, "_overrides": {"source": "wikipedia"}}
            """);

        PluginIdHelper.FindProviderBlobKeys(blobs, "wikipedia").Should().BeEmpty();
    }
}
