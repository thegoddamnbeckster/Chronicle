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
    public MetadataResolutionServiceTests() => RegistryFixtures.LoadBuiltIns();

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
    public async Task ResolveAsync_AnImpossibleYearFromTheTopPlugin_FallsThroughToTheNextPluginByPrecedence()
    {
        // live (2026-10-19): Hardcover carried year 20222 for a book released in 2022
        var item = BuildItem("audiobooks", 0,
            """{"hardcover":{"title":"Biomancer 2","year":20222},"openlibrary":{"title":"Biomancer 2","year":2022}}""");
        item.Year = null;

        await ResolveWithConfig(item, "audiobooks", 0, new()
        {
            ["year"] = ["hardcover", "openlibrary"],
        });

        GetResolved(item)["year"].GetInt32().Should().Be(2022);
        item.Year.Should().Be(2022);
    }

    [Fact]
    public async Task ResolveAsync_AnImpossibleYearWithNoOtherSource_IsNotPromoted()
    {
        var item = BuildItem("audiobooks", 0, """{"hardcover":{"title":"Biomancer 2","year":20222}}""");
        item.Year = 2022;

        await ResolveWithConfig(item, "audiobooks", 0, new() { ["year"] = ["hardcover"] });

        GetResolved(item).ContainsKey("year").Should().BeFalse();
        item.Year.Should().Be(2022);   // an existing year is never overwritten by garbage
    }

    [Fact]
    public async Task ResolveAsync_AFutureBirthDateFromTheTopPlugin_FallsThroughToTheNextPlugin()
    {
        var item = BuildItem("people", 0, """{"wikipedia":{"birthDate":"2999-01-01"},"tmdb":{"birthDate":"1964-09-02"}}""");

        await ResolveWithConfig(item, "people", 0, new() { ["birth_date"] = ["wikipedia", "tmdb"] });

        item.BirthDate.Should().Be(new DateTime(1964, 9, 2));
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

    [Fact]
    public async Task ResolveAsync_Episode_PromotesTitleDespiteBeingAboveLevelZero()
    {
        // Regression test for a real, live bug (2026-09-11): a TV episode showing a stale/wrong
        // title from its original import ("Valles Marineris") while _resolved already had the
        // corrected title ("Orders of Magnitude") -- because episodes sit at HierarchyLevel 2,
        // the old "level 0 only" promotion rule never corrected MediaItem.Name, the field that
        // actually displays as the title everywhere.
        var item = BuildItem("tv", 2, """{"tmdb":{"title":"Orders of Magnitude","year":2026}}""");
        item.Name = "Valles Marineris";

        await ResolveWithConfig(item, "tv", 2, new()
        {
            ["title"] = ["tmdb"],
            ["year"]  = ["tmdb"],
        });

        item.Name.Should().Be("Orders of Magnitude");
        item.Year.Should().Be(2026);
    }

    [Fact]
    public async Task ResolveAsync_MovieInACollection_PromotesTitleDespiteBeingAboveLevelZero()
    {
        // Regression test for the same class of bug on the movie side: a collection member
        // sits at HierarchyLevel 1 (the collection container itself is level 0 -- see
        // NfoPushService's own "No HierarchyLevel gate for movies" doc), but is still a real,
        // individually-titled movie, not a container -- unlike a TV season container, which
        // also sits at level 1 but genuinely has no title of its own.
        var item = BuildItem("movies", 1, """{"tmdb":{"title":"Die Hard with a Vengeance","year":1995}}""");
        item.Name = "Die Hard: With a Vengeance";

        await ResolveWithConfig(item, "movies", 1, new()
        {
            ["title"] = ["tmdb"],
            ["year"]  = ["tmdb"],
        });

        item.Name.Should().Be("Die Hard with a Vengeance");
        item.Year.Should().Be(1995);
    }

    [Fact]
    public async Task ResolveAsync_TvSeasonContainer_StillDoesNotPromoteTitle()
    {
        // A TV season sits at the SAME HierarchyLevel (1) as a movie collection member, but
        // must NOT get this treatment -- a season has no individually-provided title the way a
        // movie does. Guards against a too-broad fix that promotes at level 1 for every type.
        var item = BuildItem("tv", 1, """{"tmdb":{"title":"Season 4","year":2026}}""");
        item.Name = "Season 04";

        await ResolveWithConfig(item, "tv", 1, new()
        {
            ["title"] = ["tmdb"],
            ["year"]  = ["tmdb"],
        });

        item.Name.Should().Be("Season 04");
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

    [Fact]
    public async Task ResolveAsync_PluginNotOnTheSavedList_IsUsedWhenNothingOnTheListHasAValue()
    {
        // A saved list holds only the plugins installed when it was saved. Open Library had the only cover of an
        // Andy Weir book, was on no list, and the item showed no poster.
        var item = BuildItem("audiobooks", 1,
            """{"hardcover":{"title":"Lacero"},"chronicle.plugin.openlibrary":{"posterUrl":"https://covers.openlibrary.org/b/id/1-L.jpg"}}""");

        await ResolveWithConfig(item, "audiobooks", 1, new() { ["poster_url"] = ["hardcover"] });

        GetResolved(item)["posterUrl"].GetString().Should().Be("https://covers.openlibrary.org/b/id/1-L.jpg");
    }

    [Fact]
    public async Task ResolveAsync_PluginNotOnTheSavedList_NeverBeatsOneThatIsOnIt()
    {
        var item = BuildItem("audiobooks", 1,
            """{"chronicle.plugin.openlibrary":{"posterUrl":"https://ol/cover.jpg"},"hardcover":{"posterUrl":"https://hardcover/cover.jpg"}}""");

        await ResolveWithConfig(item, "audiobooks", 1, new() { ["poster_url"] = ["hardcover"] });

        GetResolved(item)["posterUrl"].GetString().Should().Be("https://hardcover/cover.jpg");
    }

    // ── ResolveAsync — people poster_url (accumulated headshots, not the normal priority walk) ──

    [Fact]
    public async Task ResolveAsync_PersonWithNoRemainingHeadshot_ClearsTheStalePosterUrl()
    {
        // Regression test for a real, live bug (2026-09-11): removing a person's only headshot
        // (e.g. a wrongly name-matched photo shared with a different real person of the same
        // name) left MediaItem.PosterUrl stuck on that same stale value forever, no matter how
        // many times resolution re-ran -- the promotion step only ever overwrote when a new
        // value was found, it never cleared one when the resolved value disappeared.
        var options = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ChronicleDbContext(options);
        var item = new MediaItem
        {
            Id = 1, Name = "Some Person", MediaTypeId = 9, HierarchyLevel = 0,
            PosterUrl = "https://stale.example/wrong-shared-photo.jpg",
            MediaType = new MediaType { Id = 9, Name = "people", IsPersonType = true, DisplayName = "People" },
        };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync();
        // Deliberately no PersonHeadshots row at all -- simulates the wrongly-attributed one
        // having just been removed, with no fallback (e.g. no TMDB photo) left.

        var svc = BuildService("people", 0, new() { ["title"] = ["tmdb"] });
        await svc.ResolveAsync(item, db, CancellationToken.None);

        item.PosterUrl.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_PersonWithAHeadshot_PromotesItAsPosterUrl()
    {
        var options = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ChronicleDbContext(options);
        var item = new MediaItem
        {
            Id = 1, Name = "Some Person", MediaTypeId = 9, HierarchyLevel = 0,
            MediaType = new MediaType { Id = 9, Name = "people", IsPersonType = true, DisplayName = "People" },
        };
        db.MediaItems.Add(item);
        db.PersonHeadshots.Add(new PersonHeadshot
        {
            PersonMediaItemId = 1, Url = "https://example/correct-own-photo.jpg",
            Source = "tmdb", FirstSeenAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var svc = BuildService("people", 0, new() { ["title"] = ["tmdb"] });
        await svc.ResolveAsync(item, db, CancellationToken.None);

        item.PosterUrl.Should().Be("https://example/correct-own-photo.jpg");
    }

    [Fact]
    public async Task ResolveAsync_PersonWhoseNewestHeadshotIsHidden_FallsBackToTheNewestVisibleOne()
    {
        // TMDB's gallery for a person can carry a stranger's photo. Once hidden, it must never be the automatic pick,
        // even when it is the most recently discovered row.
        var options = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ChronicleDbContext(options);
        var item = new MediaItem
        {
            Id = 1, Name = "Some Person", MediaTypeId = 9, HierarchyLevel = 0,
            MediaType = new MediaType { Id = 9, Name = "people", IsPersonType = true, DisplayName = "People" },
        };
        db.MediaItems.Add(item);
        db.PersonHeadshots.AddRange(
            new PersonHeadshot { PersonMediaItemId = 1, Url = "https://example/right.jpg", Source = "tmdb", FirstSeenAt = DateTime.UtcNow.AddDays(-2) },
            new PersonHeadshot { PersonMediaItemId = 1, Url = "https://example/stranger.jpg", Source = "tmdb", FirstSeenAt = DateTime.UtcNow, IsHidden = true });
        await db.SaveChangesAsync();

        var svc = BuildService("people", 0, new() { ["title"] = ["tmdb"] });
        await svc.ResolveAsync(item, db, CancellationToken.None);

        item.PosterUrl.Should().Be("https://example/right.jpg");
    }

    // ── Death-date-before-birth-date consistency guard ─────────────────────────

    [Fact]
    public async Task ResolveAsync_DeathDatePredatesBirthDate_DropsDeathDate()
    {
        // Regression test for a real, live bug (2026-09-14): "Arturo Castro" resolved birthDate
        // from TMDB (1985, correct) and deathDate from Wikipedia (1975), because Wikipedia's own
        // search had matched a completely different, same-named historical actor -- each field
        // resolves independently by its own per-provider priority, with nothing checking the two
        // facts describe the same individual. The impossible combination (died 10 years before
        // born) must never reach _resolved or the item's own columns.
        var options = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ChronicleDbContext(options);
        var item = BuildItem("people", 0,
            """{"chronicle.plugin.tmdb":{"birthDate":"1985-11-26"},"chronicle.plugin.wikipedia":{"deathDate":"1975-03-06"}}""");
        item.MediaTypeId = 9;
        item.MediaType!.Id = 9;

        var svc = BuildService("people", 0, new()
        {
            ["birth_date"] = ["chronicle.plugin.tmdb"],
            ["death_date"] = ["chronicle.plugin.wikipedia"],
        });
        await svc.ResolveAsync(item, db, CancellationToken.None);

        item.BirthDate.Should().Be(new DateTime(1985, 11, 26));
        item.DeathDate.Should().BeNull();
        GetResolved(item).Should().NotContainKey("deathDate");
    }

    [Fact]
    public async Task ResolveAsync_DeathDatePredatesBirthDate_ClearsStaleDeathDateColumn()
    {
        // The promotion step only ever overwrites item.DeathDate when a value is found -- same
        // "must actually clear, not just skip" gap the posterUrl fix above closed. A DeathDate
        // promoted by an earlier resolve pass (before this consistency check existed) must not
        // survive a later re-resolve that now correctly drops the inconsistent value.
        var options = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ChronicleDbContext(options);
        var item = BuildItem("people", 0,
            """{"chronicle.plugin.tmdb":{"birthDate":"1985-11-26"},"chronicle.plugin.wikipedia":{"deathDate":"1975-03-06"}}""");
        item.MediaTypeId = 9;
        item.MediaType!.Id = 9;
        item.DeathDate = new DateTime(1975, 3, 6); // stale value from a previous, buggy resolve pass

        var svc = BuildService("people", 0, new()
        {
            ["birth_date"] = ["chronicle.plugin.tmdb"],
            ["death_date"] = ["chronicle.plugin.wikipedia"],
        });
        await svc.ResolveAsync(item, db, CancellationToken.None);

        item.DeathDate.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_ConsistentBirthAndDeathDates_BothResolve()
    {
        var options = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ChronicleDbContext(options);
        var item = BuildItem("people", 0,
            """{"chronicle.plugin.tmdb":{"birthDate":"1918-03-21"},"chronicle.plugin.wikipedia":{"deathDate":"1975-03-06"}}""");
        item.MediaTypeId = 9;
        item.MediaType!.Id = 9;

        var svc = BuildService("people", 0, new()
        {
            ["birth_date"] = ["chronicle.plugin.tmdb"],
            ["death_date"] = ["chronicle.plugin.wikipedia"],
        });
        await svc.ResolveAsync(item, db, CancellationToken.None);

        item.BirthDate.Should().Be(new DateTime(1918, 3, 21));
        item.DeathDate.Should().Be(new DateTime(1975, 3, 6));
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
        await ResolveWithDb(item, mediaTypeName, hierarchyLevel, fieldPriorityMap, fieldAliases);
    }

    /// <summary>Resolves against a real (in-memory) database and hands it back, for tests that look at what resolving recorded.</summary>
    private static async Task<ChronicleDbContext> ResolveWithDb(
        MediaItem item, string mediaTypeName, int hierarchyLevel,
        Dictionary<string, List<string>> fieldPriorityMap, Dictionary<string, List<string>>? fieldAliases = null)
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var svc = BuildService(mediaTypeName, hierarchyLevel, fieldPriorityMap, fieldAliases);
        await svc.ResolveAsync(item, db, CancellationToken.None);
        await db.SaveChangesAsync();
        return db;
    }

    // ── A renamed container keeps its old name as an alias (the scanner finds folders by it) ──

    private static readonly Dictionary<string, List<string>> TitleFromHardcover = new() { ["title"] = ["hardcover"] };

    [Fact]
    public async Task ResolveAsync_ARenamedAuthor_KeepsItsFolderName_AsAnAlias()
    {
        // Live: the folder "Iain M. Banks" was matched by Hardcover to the author "Iain Banks"; the item took that name and the
        // next scan, finding nothing called "Iain M. Banks", created the author again -- every night, however often merged.
        var item = BuildItem("audiobooks", 0, """{"hardcover":{"title":"Iain Banks"}}""");
        item.Name = "Iain M. Banks";
        item.MediaType = new MediaType { Id = 1, Name = "audiobooks", DisplayName = "Audiobooks", HierarchyLevels = 3 };

        var db = await ResolveWithDb(item, "audiobooks", 0, TitleFromHardcover);

        item.Name.Should().Be("Iain Banks");
        var alias = await db.MediaItemAliases.SingleAsync();
        alias.Alias.Should().Be("Iain M. Banks");
        alias.MediaItemId.Should().Be(item.Id);
    }

    [Fact]
    public async Task ResolveAsync_ResolvingTwice_DoesNotRepeatTheAlias()
    {
        var item = BuildItem("audiobooks", 0, """{"hardcover":{"title":"Iain Banks"}}""");
        item.Name = "Iain M. Banks";
        item.MediaType = new MediaType { Id = 1, Name = "audiobooks", DisplayName = "Audiobooks", HierarchyLevels = 3 };
        var db = await ResolveWithDb(item, "audiobooks", 0, TitleFromHardcover);

        item.Name = "iain m. banks";   // renamed back and forth
        await BuildService("audiobooks", 0, TitleFromHardcover).ResolveAsync(item, db, CancellationToken.None);
        await db.SaveChangesAsync();

        (await db.MediaItemAliases.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("audiobooks", 1)]   // a book inside a series is not found by a folder name at the top
    [InlineData("movies", 0)]       // a flat item is not a container
    public async Task ResolveAsync_OnlyATopLevelContainer_KeepsItsFormerName(string type, int level)
    {
        var item = BuildItem(type, level, """{"hardcover":{"title":"New Title"}}""");
        item.Name = "Old Title";
        item.MediaType = new MediaType { Id = 1, Name = type, DisplayName = type, HierarchyLevels = type == "movies" ? 1 : 3 };

        var db = await ResolveWithDb(item, type, level, TitleFromHardcover);

        (await db.MediaItemAliases.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("The Wire (2002)", "The Wire")]    // only a year suffix differs
    [InlineData("the wire", "The Wire")]           // only the casing differs
    public async Task ResolveAsync_ATrivialRename_RecordsNoAlias(string folderName, string providerTitle)
    {
        var item = BuildItem("audiobooks", 0, "{\"hardcover\":{\"title\":\"" + providerTitle + "\"}}");
        item.Name = folderName;
        item.MediaType = new MediaType { Id = 1, Name = "audiobooks", DisplayName = "Audiobooks", HierarchyLevels = 3 };

        var db = await ResolveWithDb(item, "audiobooks", 0, TitleFromHardcover);

        (await db.MediaItemAliases.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ResolveAsync_ANameThatDidNotChange_RecordsNothing()
    {
        var item = BuildItem("audiobooks", 0, """{"hardcover":{"title":"iain banks"}}""");
        item.Name = "Iain Banks";

        var db = await ResolveWithDb(item, "audiobooks", 0, TitleFromHardcover);

        (await db.MediaItemAliases.CountAsync()).Should().Be(0);
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
