using Chronicle.Services;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Services;

public class FieldAliasCacheTests
{
    private static FieldAliasCache BuildWithJson(string? json)
    {
        var cache = new FieldAliasCache(null!);
        cache.InjectForTest(FieldAliasCache.ParseConfig(json));
        return cache;
    }

    [Fact]
    public async Task GetAllAsync_NoConfig_FallsBackToShippedDefaults()
    {
        var cache = BuildWithJson(null);
        var result = await cache.GetAllAsync();
        result["label"].Should().Equal("recordLabel", "publisher");
        result["composer"].Should().Equal("composers");
        result["bpm"].Should().Equal("tempo");
        result["language"].Should().Equal("lang");
    }

    [Fact]
    public async Task GetAllAsync_ExplicitConfig_OverridesDefaults()
    {
        var cache = BuildWithJson("""{"label":["onlyThisAlias"]}""");
        var result = await cache.GetAllAsync();
        result["label"].Should().Equal("onlyThisAlias");
        result.Should().NotContainKey("composer"); // not part of this explicit config — no silent merge with defaults
    }

    [Fact]
    public async Task GetAllAsync_ExplicitlySavedEmptyObject_IsRespectedNotOverriddenByDefaults()
    {
        // An admin who saves "{}" is deliberately clearing every extra alias — that choice
        // must not be silently reverted to the shipped defaults.
        var cache = BuildWithJson("{}");
        var result = await cache.GetAllAsync();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAllAsync_MalformedJson_FallsBackToDefaults()
    {
        var cache = BuildWithJson("{not valid json");
        var result = await cache.GetAllAsync();
        result["label"].Should().Equal("recordLabel", "publisher");
    }

    [Fact]
    public async Task Invalidate_ClearsCache()
    {
        var cache = BuildWithJson("""{"label":["onlyThisAlias"]}""");
        cache.Invalidate();
        cache.InjectForTest(FieldAliasCache.ParseConfig("""{"label":["differentAlias"]}"""));
        var result = await cache.GetAllAsync();
        result["label"].Should().Equal("differentAlias");
    }

    [Fact]
    public void ParseConfig_NullInput_ReturnsDefaults()
    {
        FieldAliasCache.ParseConfig(null)["composer"].Should().Equal("composers");
    }

    [Fact]
    public void ParseConfig_ValidJson_ParsesCorrectly()
    {
        var result = FieldAliasCache.ParseConfig("""{"mood":["vibe","atmosphere"]}""");
        result["mood"].Should().Equal("vibe", "atmosphere");
    }
}
