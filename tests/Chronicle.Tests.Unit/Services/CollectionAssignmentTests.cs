using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins.Models;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// A movie collection and a video game series are different things from different sources (TMDB, IGDB, whatever turns up next), and no
/// source's order may decide another's. Each flat, collection-supporting type has a collection card of its own in Metadata Assignment,
/// stored as "{type}.collection"; a collection follows the type's order for any field the card has not set.
/// </summary>
public class CollectionAssignmentTests
{
    private const string Tmdb = "chronicle.plugin.tmdb", Igdb = "chronicle.plugin.igdb", Imdb = "chronicle.plugin.imdb", Wiki = "chronicle.plugin.wikipedia";

    private static MediaType Type(string name, int levels = 1, bool collections = true) =>
        new() { Id = 1, Name = name, DisplayName = name, HierarchyLevels = levels, SupportsCollections = collections };

    private static MediaTypeSupport Support(string type, bool supportsCollections, params string[] fields) =>
        new() { MediaTypeName = type, SupportsCollections = supportsCollections, SupportedFields = [.. fields] };

    // ── the key and which types get a card ───────────────────────────────────

    [Fact]
    public void The_key_is_the_type_name_and_collection()
    {
        CollectionAssignment.KeyFor("game").Should().Be("game.collection");
        CollectionAssignment.IsKey("game.collection").Should().BeTrue();
        CollectionAssignment.IsKey("tv.1").Should().BeFalse("a level key is not a collection key");
        CollectionAssignment.TypeOf("movies.collection").Should().Be("movies");
        CollectionAssignment.TypeOf("movies").Should().Be("movies");
    }

    [Theory]
    [InlineData("movies", 1, true, true)]
    [InlineData("game", 1, true, true)]
    [InlineData("anime_movies", 1, true, true)]
    [InlineData("audiobooks", 3, true, false)]   // series grouping already has its own level card (audiobooks.1)
    [InlineData("tv", 3, false, false)]
    [InlineData("music", 3, false, false)]
    [InlineData("people", 1, false, false)]
    public void Only_a_flat_type_that_supports_collections_gets_a_collection_card(string name, int levels, bool supports, bool expected) =>
        CollectionAssignment.AppliesTo(Type(name, levels, supports)).Should().Be(expected);

    // ── which sources can be ordered on it ───────────────────────────────────

    [Fact]
    public void A_source_that_names_collections_or_declares_the_collection_field_can_be_ordered_and_others_cannot()
    {
        CollectionAssignment.CanDescribeCollections(Support("game", true, "title", "overview")).Should().BeTrue("IGDB says it supports collections");
        CollectionAssignment.CanDescribeCollections(Support("movies", false, "title", "collection")).Should().BeTrue("TMDB declares the collection field");
        CollectionAssignment.CanDescribeCollections(Support("movies", false, "title", "overview", "cast")).Should().BeFalse("IMDb has no collections");
    }

    [Fact]
    public void The_card_offers_the_collection_fields_a_collection_source_declares_in_the_cards_order()
    {
        var fields = CollectionAssignment.FieldsFor([
            Support("movies", false, "title", "overview", "poster_url", "backdrop_url", "cast", "crew", "year", "collection"),   // TMDB
            Support("movies", false, "title", "cast", "year"),                                                                    // IMDb: no collections
        ]);

        fields.Should().Equal("backdrop_url", "overview", "poster_url", "tags", "title");   // not cast, crew or year: a collection has none
    }

    [Fact]
    public void A_field_only_a_source_without_collections_declares_is_not_offered()
    {
        var fields = CollectionAssignment.FieldsFor([
            Support("game", true, "title", "overview"),                       // IGDB
            Support("game", false, "title", "poster_url", "logo_url"),        // a source with no collections declares poster and logo
        ]);

        fields.Should().NotContain("logo_url").And.NotContain("poster_url");
        fields.Should().Contain("title").And.Contain("overview");
    }

    [Fact]
    public void With_no_collection_source_at_all_the_card_offers_a_sensible_default()
    {
        CollectionAssignment.FieldsFor([Support("game", false, "title", "cast")]).Should().Equal(CollectionAssignment.DefaultFields);
        CollectionAssignment.FieldsFor([]).Should().Equal(CollectionAssignment.DefaultFields);
    }

    [Fact]
    public void The_card_is_titled_with_the_types_display_name() =>
        CollectionAssignment.DisplayName("Video Games").Should().Be("Video Games Collections");

    // ── a card nobody has saved starts from the type's order ─────────────────

    [Fact]
    public void An_unsaved_collection_card_starts_from_the_types_order_for_the_fields_it_has()
    {
        var assignments = new Dictionary<string, Dictionary<string, string[]>>
        {
            ["game"] = new() { ["title"] = [Igdb, Imdb], ["cast"] = [Igdb], ["overview"] = [Igdb, Wiki] },
        };
        var assignable = new Dictionary<string, string[]> { ["game"] = ["cast", "overview", "title"], ["game.collection"] = ["overview", "title"] };

        var seeded = CollectionAssignment.SeedFromTypeOrder(assignments, assignable);

        seeded["game.collection"].Keys.Should().BeEquivalentTo(["title", "overview"], "cast is not a collection field");
        seeded["game.collection"]["title"].Should().Equal(Igdb, Imdb);
        seeded["game"]["cast"].Should().Equal(new[] { Igdb }, "the type's own card is untouched");
    }

    [Fact]
    public void A_saved_collection_card_is_never_overwritten_by_the_types_order()
    {
        var assignments = new Dictionary<string, Dictionary<string, string[]>>
        {
            ["game"] = new() { ["title"] = [Igdb, Imdb] },
            ["game.collection"] = new() { ["title"] = [Wiki, Igdb] },
        };
        var assignable = new Dictionary<string, string[]> { ["game"] = ["title"], ["game.collection"] = ["title"] };

        CollectionAssignment.SeedFromTypeOrder(assignments, assignable)["game.collection"]["title"].Should().Equal(Wiki, Igdb);
    }

    [Fact]
    public void A_collection_card_for_a_type_with_no_saved_order_stays_absent()
    {
        var seeded = CollectionAssignment.SeedFromTypeOrder(new(), new Dictionary<string, string[]> { ["game.collection"] = ["title"] });
        seeded.Should().NotContainKey("game.collection");
    }

    // ── the cache: the collection card wins per field, the type fills the rest ─

    private static AssignmentConfigCache CacheWith(string json)
    {
        var cache = new AssignmentConfigCache(null!);
        cache.InjectForTest(AssignmentConfigCache.ParseConfig(json));
        return cache;
    }

    [Fact]
    public async Task A_collection_field_set_on_its_card_wins_and_the_rest_follow_the_type()
    {
        var cache = CacheWith("""{"movies":{"title":["tmdb","imdb"],"overview":["tmdb","wiki"]},"movies.collection":{"title":["wiki","tmdb"]}}""");

        var order = await cache.GetForCollectionAsync("movies");

        order["title"].Should().Equal("wiki", "tmdb");
        order["overview"].Should().Equal("tmdb", "wiki");
    }

    [Fact]
    public async Task With_no_collection_card_a_collection_follows_the_types_order()
    {
        var order = await CacheWith("""{"game":{"title":["igdb","imdb"]}}""").GetForCollectionAsync("game");
        order["title"].Should().Equal("igdb", "imdb");
    }

    [Fact]
    public async Task An_emptied_collection_field_falls_back_to_the_types_order()
    {
        var order = await CacheWith("""{"game":{"title":["igdb"]},"game.collection":{"title":[]}}""").GetForCollectionAsync("game");
        order["title"].Should().Equal("igdb");
    }

    [Fact]
    public async Task The_collection_order_of_one_type_never_touches_another()
    {
        var cache = CacheWith("""{"movies.collection":{"title":["tmdb"]},"game.collection":{"title":["igdb"]}}""");

        (await cache.GetForCollectionAsync("movies"))["title"].Should().Equal("tmdb");
        (await cache.GetForCollectionAsync("game"))["title"].Should().Equal("igdb");
    }

    [Fact]
    public async Task The_types_own_lookup_ignores_the_collection_card()
    {
        var order = await CacheWith("""{"game":{"title":["igdb"]},"game.collection":{"title":["wiki"]}}""").GetForTypeAsync("game", 0);
        order["title"].Should().Equal("igdb");
    }

    // ── the resolver uses the right order for a collection and for an item in it ─

    private static ChronicleDbContext Db() =>
        new(new DbContextOptionsBuilder<ChronicleDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static MetadataResolutionService Resolver(string config)
    {
        var aliases = new FieldAliasCache(null!);
        aliases.InjectForTest(new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase));
        return new MetadataResolutionService(CacheWith(config), aliases, null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MetadataResolutionService>.Instance);
    }

    private const string Blobs = """{"igdb":{"title":"From IGDB"},"wiki":{"title":"From Wikipedia"}}""";
    private const string Config = """{"game":{"title":["igdb","wiki"]},"game.collection":{"title":["wiki","igdb"]}}""";

    private static async Task<MediaItem> SavedGameAsync(ChronicleDbContext db, int level, int? parentId = null, string? externalId = null)
    {
        var type = new MediaType { Name = "game", DisplayName = "Video Games", HierarchyLevels = 1, SupportsCollections = true };
        db.MediaTypes.Add(type);
        await db.SaveChangesAsync();
        var item = new MediaItem
        {
            Name = "Test", MediaTypeId = type.Id, MediaType = type, HierarchyLevel = level, ParentId = parentId, MetadataJson = Blobs,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync();
        if (externalId is not null)
        {
            db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = item.Id, Source = "igdb", ExternalId = externalId });
            await db.SaveChangesAsync();
        }
        return item;
    }

    private static string ResolvedTitle(MediaItem item)
    {
        var blobs = MetadataResolutionService.ParsePluginBlobs(item.MetadataJson);
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(blobs["_resolved"].GetRawText())!["title"].GetString()!;
    }

    [Fact]
    public async Task A_game_series_container_is_resolved_with_the_collection_order()
    {
        var db = Db();
        var container = await SavedGameAsync(db, level: 0);
        db.MediaItems.Add(new MediaItem { Name = "Member", MediaTypeId = container.MediaTypeId, HierarchyLevel = 1, ParentId = container.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        await Resolver(Config).ResolveAsync(container, db);

        ResolvedTitle(container).Should().Be("From Wikipedia", "the collection card puts Wikipedia first, while the type's card puts IGDB first");
    }

    [Fact]
    public async Task A_collection_holding_a_collection_id_is_resolved_with_the_collection_order_even_with_no_children_yet()
    {
        var db = Db();
        var container = await SavedGameAsync(db, level: 0, externalId: "collection:353");

        await Resolver(Config).ResolveAsync(container, db);

        ResolvedTitle(container).Should().Be("From Wikipedia");
    }

    [Fact]
    public async Task A_game_that_is_not_a_collection_is_resolved_with_the_types_order()
    {
        var db = Db();
        var game = await SavedGameAsync(db, level: 0);   // no children, no collection id

        await Resolver(Config).ResolveAsync(game, db);

        ResolvedTitle(game).Should().Be("From IGDB");
    }

    [Fact]
    public async Task A_game_inside_a_collection_is_resolved_with_the_types_order()
    {
        var db = Db();
        var container = await SavedGameAsync(db, level: 0);
        var member = new MediaItem
        {
            Name = "Member", MediaTypeId = container.MediaTypeId, MediaType = container.MediaType, HierarchyLevel = 1, ParentId = container.Id,
            MetadataJson = Blobs, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.MediaItems.Add(member);
        await db.SaveChangesAsync();

        await Resolver(Config).ResolveAsync(member, db);

        ResolvedTitle(member).Should().Be("From IGDB");
    }
}
