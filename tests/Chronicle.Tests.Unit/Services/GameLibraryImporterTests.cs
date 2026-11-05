using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// A platform library is mostly games the user never started. These pin the rules that keep an import honest: no
/// invented plays, the user's status is theirs, a deleted game stays deleted, two games with one title stay two.
/// </summary>
public class GameLibraryImporterTests : IDisposable
{
    private const string Steam = "chronicle.plugin.steam";
    private const string Psn   = "chronicle.plugin.playstation";
    private readonly ChronicleDbContext _db;
    private readonly GameLibraryImporter _imp;

    public GameLibraryImporterTests()
    {
        RegistryFixtures.LoadBuiltIns();
        _db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _db.MediaTypes.Add(new MediaType { Id = 50, Name = "game", DisplayName = "Video Games", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow });
        _db.Users.Add(new User { Id = 1, Username = "u", Email = "u@x", PasswordHash = "x" });
        _db.SaveChanges();

        var registry = new Mock<IPluginRegistry>();
        registry.Setup(r => r.GetMetadataProviderEntries()).Returns([]);
        var resolution = new Mock<IMetadataResolutionService>();
        _imp = new GameLibraryImporter(registry.Object, resolution.Object, NullLogger.Instance);
    }

    public void Dispose() => _db.Dispose();

    private static ImportedLibraryEntry E(string id, string title, int? minutes = 0, long lastPlayedUnix = 0,
        bool wishlist = false, int? year = null, string source = "steam", string platform = "PC") => new(
        ExternalId: $"{source}:{id}", AdditionalIds: new Dictionary<string, string>(), MediaType: "game", Title: title, Year: year,
        Platform: platform, StoreSource: source, PlaytimeMinutes: minutes,
        LastPlayedAt: lastPlayedUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(lastPlayedUnix) : null,
        AcquiredAt: null, OwnedViaSubscription: false, IsWishlist: wishlist, RawJson: "{\"appid\":" + id + "}");

    private Task<GameLibraryResult> Run(string plugin, params ImportedLibraryEntry[] e) =>
        _imp.ImportAsync(_db, plugin, "Steam", 1, e, default);

    [Fact]
    public async Task An_unplayed_game_is_owned_not_started_with_no_invented_play()
    {
        var r = await Run(Steam, E("10", "Counter-Strike", minutes: 0, lastPlayedUnix: 0));

        r.Created.Should().Be(1);
        r.Errors.Should().BeEmpty();
        _db.InteractionEvents.Should().BeEmpty();
        _db.UserLibraries.Single().Status.Should().Be(LibraryStatus.Unwatched);
        var own = _db.UserGameOwnerships.Single();
        own.Source.Should().Be("steam"); own.SourceId.Should().Be("10"); own.PlaytimeMinutes.Should().Be(0);
        own.RawJson.Should().Contain("appid");
    }

    [Fact]
    public async Task A_played_game_is_Watching_with_an_unmarked_play_and_a_second_run_changes_nothing()
    {
        await Run(Steam, E("1245620", "ELDEN RING", minutes: 600, lastPlayedUnix: 1700000000));
        var again = await Run(Steam, E("1245620", "ELDEN RING", minutes: 600, lastPlayedUnix: 1700000000));

        again.Created.Should().Be(0); again.Matched.Should().Be(1); again.Events.Should().Be(0);
        _db.MediaItems.Count().Should().Be(1);
        _db.UserLibraries.Single().Status.Should().Be(LibraryStatus.Watching);
        var ev = _db.InteractionEvents.Single();
        ev.MarkedAsWatched.Should().BeFalse();
        ev.ProgressPercent.Should().BeNull();
        _db.UserGameOwnerships.Count().Should().Be(1);
    }

    [Fact]
    public async Task A_newer_last_played_adds_exactly_one_more_event()
    {
        await Run(Steam, E("5", "Game", minutes: 60, lastPlayedUnix: 1700000000));
        var r = await Run(Steam, E("5", "Game", minutes: 90, lastPlayedUnix: 1700100000));
        r.Events.Should().Be(1);
        _db.InteractionEvents.Count().Should().Be(2);
        _db.UserGameOwnerships.Single().PlaytimeMinutes.Should().Be(90);
    }

    [Fact]
    public async Task A_status_the_user_changed_is_never_touched_again()
    {
        await Run(Steam, E("7", "Hades", minutes: 0));
        var lib = _db.UserLibraries.Single();
        lib.Status = LibraryStatus.Dropped;   // the user's own choice
        _db.SaveChanges();

        await Run(Steam, E("7", "Hades", minutes: 500, lastPlayedUnix: 1700000000));

        _db.UserLibraries.Single().Status.Should().Be(LibraryStatus.Dropped);
    }

    [Fact]
    public async Task A_status_the_importer_wrote_advances_when_the_game_is_later_played()
    {
        await Run(Steam, E("8", "Celeste", minutes: 0));
        _db.UserLibraries.Single().Status.Should().Be(LibraryStatus.Unwatched);

        await Run(Steam, E("8", "Celeste", minutes: 30, lastPlayedUnix: 1700000000));

        _db.UserLibraries.Single().Status.Should().Be(LibraryStatus.Watching);
    }

    [Fact]
    public async Task A_game_the_user_deleted_is_not_recreated()
    {
        await Run(Steam, E("9", "Doom"));
        var item = _db.MediaItems.Single();
        // What the database does when the user deletes the item: the ownership row survives with no item (SET NULL).
        _db.UserGameOwnerships.Single().MediaItemId = null;
        _db.UserLibraries.RemoveRange(_db.UserLibraries);
        _db.MediaItems.Remove(item);
        _db.SaveChanges();

        var r = await Run(Steam, E("9", "Doom"));

        r.Skipped.Should().Be(1);
        _db.MediaItems.Should().BeEmpty();
        _db.UserLibraries.Should().BeEmpty();
    }

    [Fact]
    public async Task Two_different_games_with_one_title_stay_two_items()
    {
        await Run(Steam, E("221", "Prey"), E("480490", "Prey"));

        _db.MediaItems.Count().Should().Be(2);
        _db.UserGameOwnerships.Count().Should().Be(2);
    }

    [Fact]
    public async Task A_game_already_in_the_library_by_hand_is_joined_not_duplicated()
    {
        _db.MediaItems.Add(new MediaItem { Name = "Stardew Valley", NormalizedName = Chronicle.Core.Helpers.MediaItemNormalizer.NormalizeName("Stardew Valley"),
            MediaTypeId = 50, HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        _db.SaveChanges();

        var r = await Run(Steam, E("413150", "Stardew Valley", minutes: 10, lastPlayedUnix: 1700000000));

        r.Created.Should().Be(0);
        _db.MediaItems.Count().Should().Be(1);
        _db.MediaExternalIds.Should().Contain(e => e.Source == "steam" && e.ExternalId == "steam:413150");
    }

    [Fact]
    public async Task One_game_on_two_platforms_is_one_item_with_two_ownership_rows()
    {
        await Run(Steam, E("1", "Hollow Knight"));
        await _imp.ImportAsync(_db, Psn, "PlayStation", 1,
            [E("CUSA1", "Hollow Knight", source: "psn", platform: "PS5")], default);

        _db.MediaItems.Count().Should().Be(1);
        _db.UserGameOwnerships.Select(o => o.Source).OrderBy(s => s).Should().Equal("psn", "steam");
    }

    [Fact]
    public async Task A_wishlist_entry_is_PlanToWatch_with_no_play_and_does_not_downgrade_an_owned_game()
    {
        await Run(Steam, E("2", "Silksong", minutes: null, wishlist: true));
        _db.UserLibraries.Single().Status.Should().Be(LibraryStatus.PlanToWatch);
        _db.InteractionEvents.Should().BeEmpty();

        await Run(Steam, E("3", "Owned", minutes: 5, lastPlayedUnix: 1700000000));
        await Run(Steam, E("3", "Owned", minutes: null, wishlist: true));
        _db.UserGameOwnerships.Single(o => o.SourceId == "3").IsWishlist.Should().BeFalse();
    }

    [Fact]
    public async Task A_wishlisted_game_that_is_bought_moves_forward()
    {
        await Run(Steam, E("4", "Pizza Tower", minutes: null, wishlist: true));
        await Run(Steam, E("4", "Pizza Tower", minutes: 0));

        _db.UserLibraries.Single().Status.Should().Be(LibraryStatus.Unwatched);
        _db.UserGameOwnerships.Single().IsWishlist.Should().BeFalse();
    }

    [Fact]
    public async Task A_play_at_or_before_the_users_reset_is_not_resurrected()
    {
        await Run(Steam, E("6", "Portal", minutes: 0));
        var lib = _db.UserLibraries.Single();
        lib.WatchResetAt = DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime.AddDays(1);
        _db.SaveChanges();

        var r = await Run(Steam, E("6", "Portal", minutes: 40, lastPlayedUnix: 1700000000));

        r.Events.Should().Be(0);
        _db.UserLibraries.Single().Status.Should().Be(LibraryStatus.Unwatched);
    }

    [Fact]
    public async Task Everything_the_platform_sent_is_kept_whole_on_the_item_and_the_ownership_row()
    {
        var e = E("11", "Half-Life", minutes: 3) with { Tags = ["dlc"], RawJson = "{\"appid\":11,\"playtime_deck_forever\":7,\"has_workshop\":true}" };
        await Run(Steam, e);

        _db.UserGameOwnerships.Single().RawJson.Should().Contain("playtime_deck_forever");
        _db.UserGameOwnerships.Single().TagsJson.Should().Contain("dlc");
        _db.MediaItems.Single().MetadataJson.Should().Contain("has_workshop");
    }

    [Fact]
    public async Task A_bad_entry_is_reported_and_the_rest_still_import()
    {
        var bad = E("12", "x") with { MediaType = "no-such-type" };
        var r = await Run(Steam, bad, E("13", "Fine"));

        r.Errors.Should().ContainSingle();
        r.Created.Should().Be(1);
    }

    [Fact]
    public void The_four_argument_ImportCapabilities_constructor_survives_for_plugins_already_built()
    {
        // Plugins compiled before SupportsLibrary existed call this exact constructor. Changing it breaks Trakt, Simkl, Goodreads at load time.
        typeof(ImportCapabilities).GetConstructor([typeof(bool), typeof(bool), typeof(bool), typeof(bool)]).Should().NotBeNull();
        new ImportCapabilities(true, true, true).SupportsLibrary.Should().BeFalse();
        (new ImportCapabilities(false, false, false) { SupportsLibrary = true }).SupportsLibrary.Should().BeTrue();
    }
}
