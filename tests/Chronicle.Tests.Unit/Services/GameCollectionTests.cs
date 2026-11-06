using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// A game is grouped under its series when a provider (IGDB) names one, and stays at the top level when none does. Nothing here depends on
/// IGDB being installed: with no collection data from anywhere, no game moves.
/// </summary>
public class GameCollectionTests
{
    private static MovieCollectionService Svc() =>
        new(Mock.Of<IServiceScopeFactory>(), Mock.Of<IHttpClientFactory>(), NullLogger<MovieCollectionService>.Instance);

    private static ChronicleDbContext Db() => new(new DbContextOptionsBuilder<ChronicleDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)).Options);

    private static MediaType Game(bool supportsCollections = true, int levels = 1) => new()
    {
        Id = 50, Name = "game", DisplayName = "Video Games", HierarchyLevels = levels, SupportsCollections = supportsCollections, CreatedAt = DateTime.UtcNow,
    };

    private static void Register(MediaType t) => MediaTypeRegistry.LoadForCurrentContext(
        [.. RegistryFixtures.BuiltIns(), new MediaTypeInfo(t.Id, t.Name, null, null, false, false, false, false, [], null, t.SupportsCollections, t.HierarchyLevels)]);

    private const string InSeries =
        "{\"chronicle.plugin.igdb\":{\"title\":\"Delta Force 2\",\"extendedData\":{\"belongsToCollection\":{\"id\":164,\"name\":\"Delta Force\",\"url\":\"https://www.igdb.com/collections/delta-force\"}}}}";

    private static MediaItem NewGame(ChronicleDbContext db, MediaType t, int id, string name, string? json)
    {
        var m = new MediaItem
        {
            Id = id, Name = name, MediaTypeId = t.Id, MediaType = t, HierarchyLevel = 0, MetadataJson = json ?? "{}",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.MediaItems.Add(m);
        return m;
    }

    [Fact]
    public async Task A_game_in_a_series_is_grouped_under_a_collection_scoped_to_the_provider_that_named_it()
    {
        await using var db = Db(); var t = Game(); db.MediaTypes.Add(t); Register(t);
        var g = NewGame(db, t, 1, "Delta Force 2", InSeries); await db.SaveChangesAsync();

        await Svc().EnsureCollectionParentAsync(db, g);

        var coll = await db.MediaItems.SingleAsync(m => m.Name == "Delta Force");
        g.ParentId.Should().Be(coll.Id);
        g.HierarchyLevel.Should().Be(1);
        var ext = await db.MediaExternalIds.SingleAsync(e => e.MediaItemId == coll.Id);
        ext.Source.Should().Be("igdb");              // not "tmdb": an IGDB collection id 164 never collides with a TMDB one
        ext.ExternalId.Should().Be("collection:164");
    }

    [Fact]
    public async Task Two_games_of_one_series_share_one_collection()
    {
        await using var db = Db(); var t = Game(); db.MediaTypes.Add(t); Register(t);
        var a = NewGame(db, t, 1, "Delta Force 2", InSeries);
        var b = NewGame(db, t, 2, "Delta Force", InSeries.Replace("Delta Force 2", "Delta Force"));
        await db.SaveChangesAsync();

        await Svc().EnsureCollectionParentAsync(db, a);
        await Svc().EnsureCollectionParentAsync(db, b);

        (await db.MediaItems.CountAsync(m => m.HierarchyLevel == 0 && m.Name == "Delta Force" && m.Id != 2)).Should().Be(1);
        a.ParentId.Should().NotBeNull();
        b.ParentId.Should().Be(a.ParentId);
    }

    [Fact]
    public async Task A_game_no_provider_puts_in_a_series_stays_at_the_top_level_with_or_without_igdb()
    {
        await using var db = Db(); var t = Game(); db.MediaTypes.Add(t); Register(t);
        var g = NewGame(db, t, 1, "Solitaire", "{\"chronicle.plugin.steam\":{\"title\":\"Solitaire\"}}"); await db.SaveChangesAsync();

        await Svc().EnsureCollectionParentAsync(db, g);

        g.ParentId.Should().BeNull();
        g.HierarchyLevel.Should().Be(0);
        (await db.MediaItems.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_game_that_was_grouped_and_loses_its_series_data_returns_to_the_top_level()
    {
        await using var db = Db(); var t = Game(); db.MediaTypes.Add(t); Register(t);
        var g = NewGame(db, t, 1, "Delta Force 2", InSeries); await db.SaveChangesAsync();
        await Svc().EnsureCollectionParentAsync(db, g);
        g.ParentId.Should().NotBeNull();

        g.MetadataJson = "{}";                      // e.g. IGDB was removed and its data cleared
        await Svc().EnsureCollectionParentAsync(db, g);

        g.ParentId.Should().BeNull();
        g.HierarchyLevel.Should().Be(0);
    }

    [Fact]
    public async Task A_type_that_does_not_support_collections_is_never_grouped_even_when_a_provider_names_a_series()
    {
        await using var db = Db(); var t = Game(supportsCollections: false); db.MediaTypes.Add(t); Register(t);
        var g = NewGame(db, t, 1, "Delta Force 2", InSeries); await db.SaveChangesAsync();

        await Svc().EnsureCollectionParentAsync(db, g);

        g.ParentId.Should().BeNull();
        (await db.MediaItems.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_type_with_levels_is_never_grouped_this_way_even_if_it_supports_collections()
    {
        await using var db = Db(); var t = Game(levels: 3); db.MediaTypes.Add(t); Register(t);
        var g = NewGame(db, t, 1, "Delta Force 2", InSeries); await db.SaveChangesAsync();

        await Svc().EnsureCollectionParentAsync(db, g);

        g.ParentId.Should().BeNull();
    }

    [Fact]
    public async Task A_game_collection_never_asks_a_provider_to_fill_in_missing_members()
    {
        // Movies get stubs for the films of a collection the user lacks. A game collection holds the games the user has: nothing is invented, and
        // IGDB (which has no call for a collection id) is never asked.
        await using var db = Db(); var t = Game(); db.MediaTypes.Add(t); Register(t);
        var g = NewGame(db, t, 1, "Delta Force 2", InSeries); await db.SaveChangesAsync();
        await Svc().EnsureCollectionParentAsync(db, g);
        var coll = await db.MediaItems.Include(m => m.ExternalIds).Include(m => m.MediaType).SingleAsync(m => m.Name == "Delta Force");
        var provider = new Mock<IMetadataProvider>(MockBehavior.Strict);

        var ok = await Svc().EnsureCollectionStubsAsync(db, coll, provider.Object);

        ok.Should().BeTrue();
        provider.VerifyNoOtherCalls();
        (await db.MediaExternalIds.AnyAsync(e => e.ExternalId == "collection:164")).Should().BeTrue();   // and the container keeps its id
        (await db.MediaItems.CountAsync()).Should().Be(2);
    }

    [Theory]
    [InlineData(true, 1, true)]
    [InlineData(true, 3, false)]
    [InlineData(false, 1, false)]
    public void Only_a_flat_type_that_supports_collections_can_be_grouped(bool supports, int levels, bool expected)
    {
        var t = Game(supports, levels); Register(t);
        MediaTypeRegistry.CanGroupIntoCollections("game").Should().Be(expected);
        MediaTypeRegistry.CanGroupIntoCollections("nothing-known").Should().BeFalse();
    }
}
