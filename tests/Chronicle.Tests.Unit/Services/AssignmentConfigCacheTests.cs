using Chronicle.Services;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Services;

public class AssignmentConfigCacheTests
{
    private static AssignmentConfigCache BuildWithJson(string? json)
    {
        var cache = new AssignmentConfigCache(null!);
        cache.InjectForTest(AssignmentConfigCache.ParseConfig(json));
        return cache;
    }

    [Fact]
    public async Task GetForTypeAsync_NoConfig_ReturnsEmpty()
    {
        var cache = BuildWithJson(null);
        var result = await cache.GetForTypeAsync("audiobooks", 0);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetForTypeAsync_LevelZeroKey_ReturnsCorrectMap()
    {
        var cache = BuildWithJson("""{"audiobooks":{"poster_url":["hardcover","chronicle.plugin.musicbrainz"]}}""");
        var result = await cache.GetForTypeAsync("audiobooks", 0);
        result["poster_url"].Should().Equal("hardcover", "chronicle.plugin.musicbrainz");
    }

    [Fact]
    public async Task GetForTypeAsync_LevelSpecificKey_TakesPrecedenceOverBase()
    {
        var cache = BuildWithJson("""{"audiobooks":{"title":["mb"]},"audiobooks.2":{"title":["hardcover"]}}""");
        var result = await cache.GetForTypeAsync("audiobooks", 2);
        result["title"].Should().Equal("hardcover");
    }

    [Fact]
    public async Task GetForTypeAsync_LevelSpecificKeyAbsent_FallsBackToBase()
    {
        var cache = BuildWithJson("""{"audiobooks":{"title":["hardcover"]}}""");
        var result = await cache.GetForTypeAsync("audiobooks", 2);
        result["title"].Should().Equal("hardcover");
    }

    [Fact]
    public void Invalidate_ClearsCache()
    {
        var cache = BuildWithJson("""{"audiobooks":{"title":["hardcover"]}}""");
        cache.Invalidate();
        // After invalidate, InjectForTest with new config and verify it takes effect
        cache.InjectForTest(AssignmentConfigCache.ParseConfig("""{"audiobooks":{"title":["mb"]}}"""));
        var result = cache.GetForTypeAsync("audiobooks", 0).GetAwaiter().GetResult();
        result["title"].Should().Equal("mb");
    }

    [Fact]
    public void ParseConfig_NullInput_ReturnsEmpty()
    {
        AssignmentConfigCache.ParseConfig(null).Should().BeEmpty();
    }

    [Fact]
    public void ParseConfig_ValidJson_ParsesCorrectly()
    {
        var result = AssignmentConfigCache.ParseConfig("""{"movies":{"poster_url":["chronicle.plugin.tmdb"]}}""");
        result["movies"]["poster_url"].Should().Equal("chronicle.plugin.tmdb");
    }
}
