using Chronicle.Plugins;
using Chronicle.Services.Export;
using FluentAssertions;
using Xunit;

namespace Chronicle.Tests.Unit.Services.Export;

public class ExportPlannerTests
{
    private static readonly ExportCapabilities Simkl = new(true, true, true, true, TimeSpan.FromDays(2));
    private static readonly ExportCapabilities NoRewatch = new(true, false, true, true, TimeSpan.FromDays(2));
    private static readonly ExportOptions Defaults = new();

    private static DateTime T(string utc) => DateTime.SpecifyKind(DateTime.Parse(utc), DateTimeKind.Utc);
    private static DateTimeOffset O(string utc) => new(T(utc));

    // Chronicle's id formats on its own side ...
    private static ExportItemRef Chronicle(string title = "Spider-Man: Brand New Day") => new(
        "movie", title, 2026,
        new Dictionary<string, string>
        {
            ["imdb"] = "tt22084616", ["tmdb"] = "movie:969681", ["simkl"] = "simkl:movie:1902343",
        });

    // ... and the service's, which share no format with them.
    private static ExportItemRef Remote(string title = "Spider-Man: Brand New Day") => new(
        "movie", title, 2026,
        new Dictionary<string, string> { ["imdb"] = "tt22084616", ["tmdb"] = "969681", ["simkl"] = "1902343" });

    /// <param name="playbacks">Real playbacks.</param>
    /// <param name="evidence">Source-unspecific first-watch evidence.</param>
    private static LocalItem Local(string[] playbacks, int? rating = null, string? ratedAt = null, bool watched = true,
        string? status = null, ExportItemRef? item = null, string? evidence = null) =>
        new(item ?? Chronicle(), playbacks.Select(T).ToList(), evidence is null ? null : T(evidence),
            rating, ratedAt is null ? null : T(ratedAt), watched, status);

    private static RemoteState RemoteHas(string[]? watches = null, int? rating = null, string? ratedAt = null, string? status = null,
        ExportItemRef? item = null)
    {
        var r = item ?? Remote();
        return new RemoteState(
            (watches ?? []).Select(w => new RemoteWatch(r, O(w))).ToList(),
            rating is null ? [] : [new RemoteRating(r, rating.Value, ratedAt is null ? null : O(ratedAt))],
            status is null ? [] : [new RemoteListStatus(r, status)]);
    }

    private static readonly RemoteState Nothing = new([], [], []);

    // ── watches ───────────────────────────────────────────────────────────────

    [Fact]
    public void Brand_New_Day_the_rewatch_SIMKL_lacks_is_pushed_as_a_rewatch()
    {
        var local = Local(["2026-08-02 03:33:56", "2026-10-15 03:21:42"]);
        var remote = RemoteHas(watches: ["2026-08-02 03:33:56"]);

        var batch = ExportPlanner.Plan([local], remote, Simkl, Defaults);

        var push = batch.Watches.Should().ContainSingle().Subject;
        push.IsRewatch.Should().BeTrue();
        push.WatchedAt.Should().Be(O("2026-10-15 03:21:42"));
    }

    [Fact]
    public void A_watch_the_service_already_has_is_not_pushed_again()
    {
        var batch = ExportPlanner.Plan([Local(["2026-10-15 03:21:42"])],
            RemoteHas(watches: ["2026-10-15 03:21:56"]), Simkl, Defaults);

        batch.Watches.Should().BeEmpty();
    }

    [Fact]
    public void A_watch_inside_the_services_minimum_gap_is_the_same_watch()
    {
        // 6 hours apart: the old imported-timestamp skew. SIMKL would fold it into the existing session anyway.
        var batch = ExportPlanner.Plan([Local(["2026-10-11 04:57:59"])],
            RemoteHas(watches: ["2026-10-11 10:57:59"]), Simkl, Defaults);

        batch.Watches.Should().BeEmpty();
    }

    [Fact]
    public void An_item_the_service_does_not_have_gets_a_first_watch_not_a_rewatch()
    {
        var batch = ExportPlanner.Plan([Local(["2026-10-15 03:21:42"])], Nothing, Simkl, Defaults);

        batch.Watches.Should().ContainSingle().Which.IsRewatch.Should().BeFalse();
    }

    [Fact]
    public void Several_missing_watches_are_a_first_watch_then_rewatches_and_close_ones_fold_together()
    {
        var local = Local(["2026-01-01 20:00:00", "2026-01-01 23:00:00", "2026-03-01 20:00:00"]);

        var batch = ExportPlanner.Plan([local], Nothing, Simkl, Defaults);

        batch.Watches.Should().HaveCount(2);
        batch.Watches[0].IsRewatch.Should().BeFalse();
        batch.Watches[1].IsRewatch.Should().BeTrue();
        batch.Watches[1].WatchedAt.Should().Be(O("2026-03-01 20:00:00"));
    }

    [Fact]
    public void A_service_without_rewatch_support_gets_no_rewatches()
    {
        var local = Local(["2026-08-02 03:33:56", "2026-10-15 03:21:42"]);

        var batch = ExportPlanner.Plan([local], RemoteHas(watches: ["2026-08-02 03:33:56"]), NoRewatch, Defaults);

        batch.Watches.Should().BeEmpty();
    }

    [Fact]
    public void An_item_with_no_usable_id_is_skipped()
    {
        var item = new ExportItemRef("movie", "Mystery", 2020, new Dictionary<string, string> { ["wikipedia"] = "x" });

        var batch = ExportPlanner.Plan([Local(["2026-10-15 03:21:42"], item: item)], Nothing, Simkl, Defaults);

        batch.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void A_different_movie_sharing_a_tmdb_number_with_a_show_is_not_matched()
    {
        // tmdb 969681 as a TV show must not be mistaken for the movie of the same number.
        var show = new ExportItemRef("tv_episode", "Some Show", 2020,
            new Dictionary<string, string> { ["tmdb"] = "969681" }, "Some Show", 1, 1);
        var remote = new RemoteState([new RemoteWatch(show, O("2026-10-15 03:21:42"))], [], []);

        var batch = ExportPlanner.Plan([Local(["2026-10-15 03:21:42"], item: new ExportItemRef(
            "movie", "Movie", 2026, new Dictionary<string, string> { ["tmdb"] = "movie:969681" }))], remote, Simkl, Defaults);

        batch.Watches.Should().ContainSingle().Which.IsRewatch.Should().BeFalse();
    }

    [Fact]
    public void First_watch_evidence_is_sent_only_to_a_service_that_holds_no_watch_of_the_item()
    {
        // Fight Club / 27 Dresses / The Abyss in a real library: only Kodi play-count or another service's times,
        // which differ from SIMKL's own for the very same watch. Against an existing record they must not become a
        // rewatch; where SIMKL has nothing they are the first watch.
        var local = Local([], evidence: "2026-08-03 15:31:29");

        ExportPlanner.Plan([local], RemoteHas(watches: ["2013-05-01 12:00:00"]), Simkl, Defaults).Watches.Should().BeEmpty();
        var first = ExportPlanner.Plan([local], Nothing, Simkl, Defaults).Watches.Should().ContainSingle().Subject;
        first.IsRewatch.Should().BeFalse();
        first.WatchedAt.Should().Be(O("2026-08-03 15:31:29"));
    }

    [Fact]
    public void First_watch_evidence_never_adds_a_rewatch_beside_a_real_playback()
    {
        var local = Local(["2026-10-15 03:21:42"], evidence: "2024-02-20 20:20:53");

        var batch = ExportPlanner.Plan([local], RemoteHas(watches: ["2026-08-02 03:33:56"]), Simkl, Defaults);

        batch.Watches.Should().ContainSingle().Which.WatchedAt.Should().Be(O("2026-10-15 03:21:42"));
    }

    [Fact]
    public void A_remote_watch_with_no_known_date_blocks_evidence_but_not_a_real_playback()
    {
        // SIMKL keeps a 1970 placeholder for "watched, date unknown".
        var unknown = new RemoteState([new RemoteWatch(Remote(), DateTimeOffset.MinValue)], [], []);

        ExportPlanner.Plan([Local([], evidence: "2026-10-15 03:21:42")], unknown, Simkl, Defaults).Watches.Should().BeEmpty();
        ExportPlanner.Plan([Local(["2026-10-15 03:21:42"])], unknown, Simkl, Defaults)
            .Watches.Should().ContainSingle().Which.IsRewatch.Should().BeTrue();
    }

    [Fact]
    public void A_movie_collection_is_not_an_item_at_all()
    {
        // "Dune Collection": a container whose id is tmdb collection:726871 - never a movie id.
        var collection = new ExportItemRef("movie", "Dune Collection", null,
            new Dictionary<string, string> { ["tmdb"] = "collection:726871" });

        var batch = ExportPlanner.Plan([Local([], item: collection, evidence: "2026-09-18 17:59:56")], Nothing, Simkl, Defaults);

        batch.IsEmpty.Should().BeTrue();
    }

    // ── ratings ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_rating_the_service_lacks_is_pushed()
    {
        var batch = ExportPlanner.Plan([Local([], rating: 9, ratedAt: "2026-08-02 03:28:41")],
            RemoteHas(watches: ["2026-08-02 03:33:56"]), Simkl, Defaults);

        batch.Ratings.Should().ContainSingle().Which.Rating.Should().Be(9);
    }

    [Fact]
    public void An_identical_rating_is_not_pushed()
    {
        var batch = ExportPlanner.Plan([Local([], rating: 9, ratedAt: "2026-08-02 03:28:41")],
            RemoteHas(rating: 9, ratedAt: "2026-08-02 03:28:41"), Simkl, Defaults);

        batch.Ratings.Should().BeEmpty();
    }

    [Fact]
    public void The_more_recent_rating_wins()
    {
        var newer = ExportPlanner.Plan([Local([], rating: 7, ratedAt: "2026-10-10 12:00:00")],
            RemoteHas(rating: 9, ratedAt: "2026-08-02 03:28:41"), Simkl, Defaults);
        var older = ExportPlanner.Plan([Local([], rating: 7, ratedAt: "2026-07-01 12:00:00")],
            RemoteHas(rating: 9, ratedAt: "2026-08-02 03:28:41"), Simkl, Defaults);

        newer.Ratings.Should().ContainSingle().Which.Rating.Should().Be(7);
        older.Ratings.Should().BeEmpty();
    }

    [Fact]
    public void A_local_rating_of_unknown_age_never_overwrites_a_different_remote_one()
    {
        var batch = ExportPlanner.Plan([Local([], rating: 7)], RemoteHas(rating: 9, ratedAt: "2026-08-02 03:28:41"), Simkl, Defaults);

        batch.Ratings.Should().BeEmpty();
    }

    [Fact]
    public void A_remote_rating_of_unknown_age_loses_to_a_dated_local_one()
    {
        var batch = ExportPlanner.Plan([Local([], rating: 7, ratedAt: "2026-10-10 12:00:00")], RemoteHas(rating: 9), Simkl, Defaults);

        batch.Ratings.Should().ContainSingle();
    }

    [Fact]
    public void A_rating_is_not_exported_for_an_unwatched_item_or_out_of_range()
    {
        ExportPlanner.Plan([Local([], rating: 9, watched: false)], Nothing, Simkl, Defaults).Ratings.Should().BeEmpty();
        ExportPlanner.Plan([Local([], rating: 0)], Nothing, Simkl, Defaults).Ratings.Should().BeEmpty();
        ExportPlanner.Plan([Local([], rating: 11)], Nothing, Simkl, Defaults).Ratings.Should().BeEmpty();
    }

    // ── list status ───────────────────────────────────────────────────────────

    [Fact]
    public void List_status_is_not_exported_unless_asked_for()
    {
        ExportPlanner.Plan([Local([], status: "plantowatch", watched: false)], Nothing, Simkl, Defaults)
            .ListStatuses.Should().BeEmpty();
    }

    [Fact]
    public void List_status_is_exported_when_enabled_but_never_over_one_the_service_has()
    {
        var on = new ExportOptions(SyncListStatus: true);

        ExportPlanner.Plan([Local([], status: "plantowatch", watched: false)], Nothing, Simkl, on)
            .ListStatuses.Should().ContainSingle().Which.Status.Should().Be("plantowatch");
        ExportPlanner.Plan([Local([], status: "plantowatch", watched: false)], RemoteHas(status: "dropped"), Simkl, on)
            .ListStatuses.Should().BeEmpty();
    }
}

public class ExportKeysTests
{
    [Theory]
    [InlineData("imdb", "tt22084616", "movie", "imdb:tt22084616")]
    [InlineData("imdb", "22084616", "movie", "imdb:tt22084616")]
    [InlineData("tmdb", "movie:969681", "movie", "tmdb:movie:969681")]
    [InlineData("tmdb", "969681", "movie", "tmdb:movie:969681")]
    [InlineData("tmdb", "tv:92611", "tv", "tmdb:tv:92611")]
    [InlineData("simkl", "simkl:movie:1902343", "movie", "simkl:movie:1902343")]
    [InlineData("tvdb", "356452", "tv", "tvdb:tv:356452")]
    public void Ids_in_any_source_format_normalise_to_one_key(string source, string value, string kind, string expected) =>
        ExportKeys.Normalize(source, value, kind).Should().Be(expected);

    [Theory]
    [InlineData("tmdb", "tv:92611/season:8/episode:3")]   // an episode's own id is not the show's
    [InlineData("tmdb", "collection:726871")]            // a movie collection is a container, not a movie
    [InlineData("tmdb", "tv:92611")]                     // a TV id on a movie
    [InlineData("tmdb", "abc")]
    [InlineData("imdb", "")]
    [InlineData("wikipedia", "wikipedia:en:Foo")]
    public void Unusable_ids_give_no_key(string source, string value) =>
        ExportKeys.Normalize(source, value, "movie").Should().BeNull();

    [Fact]
    public void An_episode_is_keyed_by_its_show_plus_season_and_episode()
    {
        var episode = new ExportItemRef("tv_episode", "Snatch Me If You Can", 2026,
            new Dictionary<string, string> { ["tmdb"] = "tv:92611", ["tvdb"] = "356452" }, "Drag Race UK", 8, 3);

        episode.Keys.Should().BeEquivalentTo("tmdb:tv:92611:s8e3", "tvdb:tv:356452:s8e3");
    }
}
