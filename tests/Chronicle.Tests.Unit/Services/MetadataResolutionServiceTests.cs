using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Tests.Unit.Services;

/// <summary>Hands every requested scope the same pre-built context — enough for the batched
/// bulk-clear paths, which only need a ChronicleDbContext per batch.</summary>
file sealed class SingleContextScopeFactory(ChronicleDbContext ctx) : IServiceScopeFactory, IServiceScope, IServiceProvider
{
    public IServiceScope CreateScope() => this;
    public IServiceProvider ServiceProvider => this;
    public object? GetService(Type serviceType) => serviceType == typeof(ChronicleDbContext) ? ctx : null;
    public void Dispose() { }
}

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

    // ── Manual image overrides (pin / unpin) ─────────────────────────────────
    // The whole point of a pin is that it beats the plugin-priority walk and keeps beating it
    // after later refreshes — these lock that contract down at the resolution choke point that
    // every caller (refresh, merge, sync, bulk recompute) funnels through.

    [Fact]
    public async Task SetOverrideAsync_PinnedFieldBeatsPriorityWalk()
    {
        var item = BuildItem("movies", 0,
            """{"chronicle.plugin.tmdb":{"posterUrl":"https://tmdb/auto.jpg"}}""");

        await SetOverride(item, "poster_url", "https://fanart/manual.jpg", "movies", 0, new()
        {
            ["poster_url"] = ["chronicle.plugin.tmdb"],
        });

        item.PosterUrl.Should().Be("https://fanart/manual.jpg");
        GetResolved(item)["posterUrl"].GetString().Should().Be("https://fanart/manual.jpg");
    }

    [Fact]
    public async Task ResolveAsync_PinSurvivesLaterReResolve()
    {
        // Simulates a metadata refresh landing a new provider value after a pin was made:
        // re-resolving must not quietly hand the slot back to the plugin.
        var item = BuildItem("movies", 0,
            """{"chronicle.plugin.tmdb":{"posterUrl":"https://tmdb/old.jpg"}}""");

        await SetOverride(item, "poster_url", "https://fanart/manual.jpg", "movies", 0, new()
        {
            ["poster_url"] = ["chronicle.plugin.tmdb"],
        });

        // Provider returns something different on the next refresh.
        var root = System.Text.Json.Nodes.JsonNode.Parse(item.MetadataJson!)!.AsObject();
        root["chronicle.plugin.tmdb"] = new System.Text.Json.Nodes.JsonObject
        {
            ["posterUrl"] = "https://tmdb/brand-new.jpg",
        };
        item.MetadataJson = root.ToJsonString();

        await ResolveWithConfig(item, "movies", 0, new()
        {
            ["poster_url"] = ["chronicle.plugin.tmdb"],
        });

        item.PosterUrl.Should().Be("https://fanart/manual.jpg",
            "a manual pin must outlast provider refreshes until it is explicitly cleared");
    }

    [Fact]
    public async Task SetOverrideAsync_SameImageCanHoldSeveralSlots()
    {
        var item = BuildItem("movies", 0,
            """{"chronicle.plugin.tmdb":{"posterUrl":"https://tmdb/auto.jpg","thumbUrl":"https://tmdb/auto-thumb.jpg"}}""");
        var config = new Dictionary<string, List<string>>
        {
            ["poster_url"] = ["chronicle.plugin.tmdb"],
            ["thumb_url"]  = ["chronicle.plugin.tmdb"],
        };

        var svc = BuildService("movies", 0, config);
        await svc.SetOverrideAsync(item, null!, "poster_url", "https://shared/art.jpg", "p", "poster", 1);
        await svc.SetOverrideAsync(item, null!, "thumb_url",  "https://shared/art.jpg", "p", "poster", 1);

        var resolved = GetResolved(item);
        resolved["posterUrl"].GetString().Should().Be("https://shared/art.jpg");
        resolved["thumbUrl"].GetString().Should().Be("https://shared/art.jpg");
    }

    [Fact]
    public async Task ClearOverrideAsync_RevertsOnlyThatSlotToTheDefault()
    {
        var item = BuildItem("movies", 0,
            """{"chronicle.plugin.tmdb":{"posterUrl":"https://tmdb/auto.jpg","thumbUrl":"https://tmdb/auto-thumb.jpg"}}""");
        var config = new Dictionary<string, List<string>>
        {
            ["poster_url"] = ["chronicle.plugin.tmdb"],
            ["thumb_url"]  = ["chronicle.plugin.tmdb"],
        };

        var svc = BuildService("movies", 0, config);
        await svc.SetOverrideAsync(item, null!, "poster_url", "https://shared/art.jpg", "p", "poster", 1);
        await svc.SetOverrideAsync(item, null!, "thumb_url",  "https://shared/art.jpg", "p", "poster", 1);

        await svc.ClearOverrideAsync(item, null!, "thumb_url");

        var resolved = GetResolved(item);
        resolved["thumbUrl"].GetString().Should().Be("https://tmdb/auto-thumb.jpg", "cleared slot falls back to the provider");
        resolved["posterUrl"].GetString().Should().Be("https://shared/art.jpg", "other slots keep their pins");
    }

    [Fact]
    public async Task ClearItemOverridesAsync_RevertsEverySlot()
    {
        var item = BuildItem("movies", 0,
            """{"chronicle.plugin.tmdb":{"posterUrl":"https://tmdb/auto.jpg","thumbUrl":"https://tmdb/auto-thumb.jpg"}}""");
        var config = new Dictionary<string, List<string>>
        {
            ["poster_url"] = ["chronicle.plugin.tmdb"],
            ["thumb_url"]  = ["chronicle.plugin.tmdb"],
        };

        var svc = BuildService("movies", 0, config);
        await svc.SetOverrideAsync(item, null!, "poster_url", "https://shared/art.jpg", "p", "poster", 1);
        await svc.SetOverrideAsync(item, null!, "thumb_url",  "https://shared/art.jpg", "p", "poster", 1);

        await svc.ClearItemOverridesAsync(item, null!);

        item.MetadataJson.Should().NotContain("_overrides");
        var resolved = GetResolved(item);
        resolved["posterUrl"].GetString().Should().Be("https://tmdb/auto.jpg");
        resolved["thumbUrl"].GetString().Should().Be("https://tmdb/auto-thumb.jpg");
    }

    [Fact]
    public async Task SetOverrideAsync_UnknownField_Throws()
    {
        var item = BuildItem("movies", 0, """{"chronicle.plugin.tmdb":{"posterUrl":"https://tmdb/auto.jpg"}}""");
        var svc = BuildService("movies", 0, new() { ["poster_url"] = ["chronicle.plugin.tmdb"] });

        await svc.Invoking(s => s.SetOverrideAsync(item, null!, "not_a_real_field", "https://x/y.jpg", null, null, null))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SetOverrideAsync_PreservesPluginBlobsAndRecordsProvenance()
    {
        // A pin must not clobber the lossless per-plugin blobs it sits beside (CLAUDE.md rule 6).
        var item = BuildItem("movies", 0,
            """{"chronicle.plugin.tmdb":{"posterUrl":"https://tmdb/auto.jpg","overview":"keep me"}}""");

        await SetOverride(item, "poster_url", "https://fanart/manual.jpg", "movies", 0, new()
        {
            ["poster_url"] = ["chronicle.plugin.tmdb"],
        }, sourcePluginId: "chronicle.plugin.fanarttv", sourceType: "poster", userId: 7);

        var blobs = MetadataResolutionService.ParsePluginBlobs(item.MetadataJson);
        blobs.Should().ContainKey("chronicle.plugin.tmdb");
        blobs["chronicle.plugin.tmdb"].GetProperty("overview").GetString().Should().Be("keep me");

        var pin = blobs["_overrides"].GetProperty("poster_url");
        pin.GetProperty("url").GetString().Should().Be("https://fanart/manual.jpg");
        pin.GetProperty("sourcePluginId").GetString().Should().Be("chronicle.plugin.fanarttv");
        pin.GetProperty("pinnedByUserId").GetInt32().Should().Be(7);
        pin.GetProperty("pinnedAt").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ClearOverridesForSubtreeAsync_ClearsRootAndDescendants_LeavesOutsidersAlone()
    {
        // Collection-level reset: the container itself, its members, and (for deeper trees) their
        // children all revert — while an unrelated item at the same level keeps its pin.
        var options = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        await using var db = new ChronicleDbContext(options);

        var mt = new MediaType { Id = 1, Name = "movies", DisplayName = "Movies" };
        db.MediaTypes.Add(mt);

        const string pinned =
            """{"chronicle.plugin.tmdb":{"posterUrl":"https://tmdb/auto.jpg"},"_overrides":{"poster_url":{"url":"https://pinned/art.jpg"}}}""";

        var collection = new MediaItem { Id = 1, Name = "Coll",    MediaTypeId = 1, HierarchyLevel = 0, MetadataJson = pinned };
        var member     = new MediaItem { Id = 2, Name = "Member",  MediaTypeId = 1, HierarchyLevel = 1, ParentId = 1, MetadataJson = pinned };
        var grandchild = new MediaItem { Id = 3, Name = "Deeper",  MediaTypeId = 1, HierarchyLevel = 2, ParentId = 2, MetadataJson = pinned };
        var outsider   = new MediaItem { Id = 4, Name = "Unrelated", MediaTypeId = 1, HierarchyLevel = 0, MetadataJson = pinned };
        db.MediaItems.AddRange(collection, member, grandchild, outsider);
        await db.SaveChangesAsync();

        var cache = new AssignmentConfigCache(null!);
        cache.InjectForTest(new Dictionary<string, Dictionary<string, List<string>>>
        {
            ["movies"] = new() { ["poster_url"] = ["chronicle.plugin.tmdb"] },
        });
        var aliasCache = new FieldAliasCache(null!);
        aliasCache.InjectForTest(new(StringComparer.OrdinalIgnoreCase));
        var svc = new MetadataResolutionService(
            cache, aliasCache, new SingleContextScopeFactory(db),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MetadataResolutionService>.Instance);

        var cleared = await svc.ClearOverridesForSubtreeAsync(1);

        cleared.Should().Be(3, "the container plus both descendants were pinned");
        db.MediaItems.Single(m => m.Id == 1).MetadataJson.Should().NotContain("_overrides");
        db.MediaItems.Single(m => m.Id == 2).MetadataJson.Should().NotContain("_overrides");
        db.MediaItems.Single(m => m.Id == 3).MetadataJson.Should().NotContain("_overrides");
        db.MediaItems.Single(m => m.Id == 4).MetadataJson.Should().Contain("_overrides",
            "an item outside the subtree must keep its pin");
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
        var svc = BuildService(mediaTypeName, hierarchyLevel, fieldPriorityMap, fieldAliases);
        await svc.ResolveAsync(item, null!, CancellationToken.None);
    }

    /// Same wiring as ResolveWithConfig, but hands back the service so a test can drive the
    /// override methods (which each re-run ResolveAsync internally) rather than just resolving.
    private static MetadataResolutionService BuildService(
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
        return new MetadataResolutionService(cache, aliasCache, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<MetadataResolutionService>.Instance);
    }

    private static async Task SetOverride(
        MediaItem item,
        string field,
        string url,
        string mediaTypeName,
        int hierarchyLevel,
        Dictionary<string, List<string>> fieldPriorityMap,
        string? sourcePluginId = "chronicle.plugin.fanarttv",
        string? sourceType = "poster",
        int? userId = 1)
    {
        var svc = BuildService(mediaTypeName, hierarchyLevel, fieldPriorityMap);
        await svc.SetOverrideAsync(item, null!, field, url, sourcePluginId, sourceType, userId, CancellationToken.None);
    }
}
