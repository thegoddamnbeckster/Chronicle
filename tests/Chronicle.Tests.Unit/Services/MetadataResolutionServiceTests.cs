using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Services;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Services;

public class MetadataResolutionServiceTests
{
    // ── ParsePluginBlobs ──────────────────────────────────────────────────────

    [Fact]
    public void ParsePluginBlobs_NullInput_ReturnsEmpty() =>
        MetadataResolutionService.ParsePluginBlobs(null).Should().BeEmpty();

    [Fact]
    public void ParsePluginBlobs_ValidJson_ReturnsBlobsKeyedByPluginId()
    {
        var result = MetadataResolutionService.ParsePluginBlobs(
            """{"hardcover":{"title":"Dune"},"chronicle.plugin.musicbrainz":{"title":"Dune 2"}}""");
        result.Should().ContainKey("hardcover").And.ContainKey("chronicle.plugin.musicbrainz");
    }

    // ── HasValue ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("null",   false)]
    [InlineData("\"\"",   false)]
    [InlineData("\"  \"", false)]
    [InlineData("\"ok\"", true)]
    [InlineData("42",     true)]
    [InlineData("[]",     false)]
    [InlineData("[1]",    true)]
    public void HasValue_VariousInputs_CorrectResult(string rawJson, bool expected)
    {
        var el = JsonDocument.Parse(rawJson).RootElement;
        MetadataResolutionService.HasValue(el).Should().Be(expected);
    }

    // ── ResolveAsync — priority waterfall ────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_HigherPriorityPluginWins()
    {
        var item = BuildItem("audiobooks", 0,
            """{"hardcover":{"posterUrl":"https://hardcover.app/cover.jpg","title":"Dune"},"chronicle.plugin.musicbrainz":{"posterUrl":"https://mb.org/cover.jpg","title":"Dune"}}""");

        await ResolveWithConfig(item, "audiobooks", 0, new()
        {
            ["poster_url"] = ["hardcover", "chronicle.plugin.musicbrainz"],
            ["title"]      = ["hardcover", "chronicle.plugin.musicbrainz"],
        });

        item.PosterUrl.Should().Be("https://hardcover.app/cover.jpg");
        item.Name.Should().Be("Dune");
        GetResolved(item)["posterUrl"].GetString().Should().Be("https://hardcover.app/cover.jpg");
    }

    [Fact]
    public async Task ResolveAsync_PrimaryMissingField_FallsBackToSecondary()
    {
        var item = BuildItem("audiobooks", 0,
            """{"hardcover":{"overview":""},"chronicle.plugin.musicbrainz":{"overview":"A sci-fi epic."}}""");

        await ResolveWithConfig(item, "audiobooks", 0, new()
        {
            ["overview"] = ["hardcover", "chronicle.plugin.musicbrainz"],
        });

        item.Overview.Should().Be("A sci-fi epic.");
        GetResolved(item)["overview"].GetString().Should().Be("A sci-fi epic.");
    }

    [Fact]
    public async Task ResolveAsync_NoPluginHasValue_FieldAbsentFromResolved()
    {
        var item = BuildItem("audiobooks", 0,
            """{"hardcover":{"rating":null},"chronicle.plugin.musicbrainz":{"rating":null}}""");

        await ResolveWithConfig(item, "audiobooks", 0, new()
        {
            ["rating"] = ["hardcover", "chronicle.plugin.musicbrainz"],
        });

        GetResolved(item).Should().NotContainKey("rating");
    }

    [Fact]
    public async Task ResolveAsync_NoConfigForType_DoesNotCrashAndLeavesNameUnchanged()
    {
        var item = BuildItem("music", 0,
            """{"chronicle.plugin.musicbrainz":{"title":"Abbey Road"}}""");
        item.Name = "Abbey Road";

        await ResolveWithConfig(item, "music", 0, new());

        item.Name.Should().Be("Abbey Road");
    }

    [Fact]
    public async Task ResolveAsync_TitleAndYearNotPromotedAboveLevelZero()
    {
        var item = BuildItem("audiobooks", 1,
            """{"hardcover":{"title":"Stormlight Archive","year":2010}}""");
        item.Name = "original";
        item.Year = null;

        await ResolveWithConfig(item, "audiobooks", 1, new()
        {
            ["title"] = ["hardcover"],
            ["year"]  = ["hardcover"],
        });

        // _resolved populated
        GetResolved(item)["title"].GetString().Should().Be("Stormlight Archive");
        // BUT first-class Name/Year NOT changed at level > 0
        item.Name.Should().Be("original");
        item.Year.Should().BeNull();
    }

    // ── ResolveAsync — field-name aliasing (music sources naming the same concept differently) ──

    [Fact]
    public async Task ResolveAsync_DifferentAliasKeyNames_ResolveToOneCanonicalValue()
    {
        // MusicBrainz-style blob uses "label"; Discogs-style blob uses "recordLabel" — both are
        // aliases of the same canonical "label" field once an admin has configured "recordLabel"
        // as an extra alias via FieldAliasCache (metadata_field_aliases.config).
        var item = BuildItem("music", 0,
            """{"chronicle.plugin.musicbrainz":{"label":"Silva Screen"},"chronicle.plugin.discogs":{"recordLabel":"Varese Sarabande"}}""");

        await ResolveWithConfig(item, "music", 0, new(), new()
        {
            ["label"] = ["recordLabel"],
        });

        // No priority configured — first blob in dictionary order wins (musicbrainz).
        GetResolved(item)["label"].GetString().Should().Be("Silva Screen");
    }

    [Fact]
    public async Task ResolveAsync_PriorityConfiguredPluginUsesDifferentAlias_StillWins()
    {
        // The higher-priority plugin (discogs) uses the configured "recordLabel" alias, not the
        // canonical "label" key — TryGetBlobPropertyAny must still find it.
        var item = BuildItem("music", 0,
            """{"chronicle.plugin.musicbrainz":{"label":"Silva Screen"},"chronicle.plugin.discogs":{"recordLabel":"Varese Sarabande"}}""");

        await ResolveWithConfig(item, "music", 0, new()
        {
            ["label"] = ["chronicle.plugin.discogs", "chronicle.plugin.musicbrainz"],
        }, new()
        {
            ["label"] = ["recordLabel"],
        });

        GetResolved(item)["label"].GetString().Should().Be("Varese Sarabande");
    }

    [Fact]
    public async Task ResolveAsync_NoAliasConfigured_DifferentKeyNameDoesNotMatch()
    {
        // Without an admin-configured alias, "recordLabel" is just an unrelated key — confirms
        // the aliasing behaviour above is genuinely coming from FieldAliasCache, not FieldMap.
        var item = BuildItem("music", 0,
            """{"chronicle.plugin.discogs":{"recordLabel":"Varese Sarabande"}}""");

        await ResolveWithConfig(item, "music", 0, new());

        GetResolved(item).Should().NotContainKey("label");
    }

    [Fact]
    public async Task ResolveAsync_WritesResolvedKeyToMetadataJson()
    {
        var item = BuildItem("audiobooks", 0,
            """{"hardcover":{"posterUrl":"https://hardcover.app/cover.jpg"}}""");

        await ResolveWithConfig(item, "audiobooks", 0, new()
        {
            ["poster_url"] = ["hardcover"],
        });

        item.MetadataJson.Should().Contain("\"_resolved\"");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static MediaItem BuildItem(string mediaTypeName, int hierarchyLevel, string metadataJson) =>
        new()
        {
            Id             = 1,
            Name           = "Test",
            HierarchyLevel = hierarchyLevel,
            MetadataJson   = metadataJson,
            MediaType      = new MediaType { Id = 1, Name = mediaTypeName, DisplayName = mediaTypeName },
            MediaTypeId    = 1,
        };

    private static Dictionary<string, JsonElement> GetResolved(MediaItem item)
    {
        var blobs = MetadataResolutionService.ParsePluginBlobs(item.MetadataJson);
        if (!blobs.TryGetValue("_resolved", out var el) || el.ValueKind != JsonValueKind.Object)
            return [];
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(el.GetRawText()) ?? [];
    }

    private static async Task ResolveWithConfig(
        MediaItem item,
        string mediaTypeName,
        int hierarchyLevel,
        Dictionary<string, List<string>> fieldPriorityMap,
        Dictionary<string, List<string>>? fieldAliases = null)
    {
        var cache = new AssignmentConfigCache(null!);
        cache.InjectForTest(new Dictionary<string, Dictionary<string, List<string>>>
        {
            [$"{mediaTypeName}{(hierarchyLevel > 0 ? $".{hierarchyLevel}" : "")}"] = fieldPriorityMap
        });
        var aliasCache = new FieldAliasCache(null!);
        // Empty, not FieldAliasCache.Defaults — tests should be deterministic and not
        // accidentally depend on the shipped-defaults seed unless a test explicitly injects one.
        aliasCache.InjectForTest(fieldAliases ?? new(StringComparer.OrdinalIgnoreCase));
        var svc = new MetadataResolutionService(cache, aliasCache, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<MetadataResolutionService>.Instance);
        await svc.ResolveAsync(item, null!, CancellationToken.None);
    }
}
