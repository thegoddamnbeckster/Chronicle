using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Services.Export;
using Chronicle.Services.Plugins;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services.Export;

public class ExportServiceTests
{
    private const string PluginId = "chronicle.plugin.simkl";
    private const int UserId = 1;

    private static DateTime T(string utc) => DateTime.SpecifyKind(DateTime.Parse(utc), DateTimeKind.Utc);
    private static DateTimeOffset O(string utc) => new(T(utc));

    private sealed class Fixture
    {
        public ChronicleDbContext Db = null!;
        public Mock<IImportProvider> Import = null!;
        public Mock<IExportProvider> Export = null!;
        public ExportService Service = null!;
        public ExportBatch? Pushed;
        public int PushCalls;
    }

    private static Fixture Make(RemoteState remote, ExportOptions? options = null, bool remoteThrows = false)
    {
        var f = new Fixture
        {
            Db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options),
        };
        f.Db.MediaTypes.Add(new MediaType { Id = 1, Name = "tv", DisplayName = "TV", HierarchyLevels = 3, HierarchyLabels = "Show,Season,Episode", CreatedAt = DateTime.UtcNow });
        f.Db.MediaTypes.Add(new MediaType { Id = 2, Name = "movies", DisplayName = "Movies", HierarchyLevels = 1, HierarchyLabels = "Movie", CreatedAt = DateTime.UtcNow });

        f.Import = new Mock<IImportProvider>();
        f.Import.Setup(p => p.Name).Returns("Simkl");
        f.Import.Setup(p => p.PluginId).Returns(PluginId);
        f.Import.Setup(p => p.IsAuthenticatedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        f.Export = f.Import.As<IExportProvider>();
        f.Export.Setup(p => p.GetExportOptions()).Returns(options ?? new ExportOptions());
        f.Export.Setup(p => p.GetExportCapabilities()).Returns(new ExportCapabilities(true, true, true, true, TimeSpan.FromDays(2)));
        if (remoteThrows)
            f.Export.Setup(p => p.GetRemoteStateAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("SIMKL is down"));
        else
            f.Export.Setup(p => p.GetRemoteStateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(remote);
        f.Export.Setup(p => p.PushAsync(It.IsAny<ExportBatch>(), It.IsAny<CancellationToken>()))
            .Callback<ExportBatch, CancellationToken>((b, _) => { f.Pushed = b; f.PushCalls++; })
            .ReturnsAsync((ExportBatch b, CancellationToken _) =>
                new ExportPushResult(b.Watches.Count, b.Ratings.Count, b.ListStatuses.Count, 0, []));

        var registry = new Mock<IPluginRegistry>();
        registry.Setup(r => r.GetImportProvider(PluginId)).Returns(f.Import.Object);
        registry.Setup(r => r.GetImportProviders()).Returns([f.Import.Object]);

        var sp = new ServiceCollection().AddSingleton(f.Db).BuildServiceProvider();
        f.Service = new ExportService(sp.GetRequiredService<IServiceScopeFactory>(), registry.Object,
            NullLogger<ExportService>.Instance);
        return f;
    }

    private static void SeedBrandNewDay(ChronicleDbContext db, int? rating = null)
    {
        db.MediaItems.Add(new MediaItem { Id = 800226, MediaTypeId = 2, Name = "Spider-Man: Brand New Day", Year = 2026, HierarchyLevel = 0 });
        db.MediaExternalIds.AddRange(
            new MediaExternalId { MediaItemId = 800226, Source = "imdb", ExternalId = "tt22084616" },
            new MediaExternalId { MediaItemId = 800226, Source = "tmdb", ExternalId = "movie:969681" },
            new MediaExternalId { MediaItemId = 800226, Source = "simkl", ExternalId = "simkl:movie:1902343" });
        db.UserLibraries.Add(new UserLibrary
        {
            UserId = UserId, MediaItemId = 800226, Status = LibraryStatus.Completed,
            CompletedAt = T("2026-10-15 03:21:56"), UserRating = rating,
            UserRatingUpdatedAt = rating is null ? null : T("2026-08-02 03:28:41"),
        });
        // The first watch, as SIMKL's own import recorded it ...
        db.InteractionEvents.Add(new InteractionEvent
        {
            UserId = UserId, MediaItemId = 800226, Timestamp = T("2026-08-02 03:33:56"),
            ProgressPercent = 100, MarkedAsWatched = true, DeviceName = "Simkl",
        });
        // ... and the rewatch the upstairs Shield scrobbled: progress events every 30s up to 99.8%.
        var t = T("2026-10-15 02:50:00");
        for (var p = 70.0; p <= 99.8; p += 0.35, t = t.AddSeconds(30))
            db.InteractionEvents.Add(new InteractionEvent
            {
                UserId = UserId, MediaItemId = 800226, Timestamp = t, ProgressPercent = p,
                MarkedAsWatched = p >= 80, DeviceName = "Kodi upstairs",
            });
        // The reconciled completion pulled from the device afterwards.
        db.InteractionEvents.Add(new InteractionEvent
        {
            UserId = UserId, MediaItemId = 800226, Timestamp = T("2026-10-15 03:21:56"), ProgressPercent = 100,
            MarkedAsWatched = true, DeviceName = "Chronicle Scraper (reconciled from local Kodi playback)",
        });
        db.SaveChanges();
    }

    private static ExportItemRef RemoteMovie() => new("movie", "Spider-Man: Brand New Day", 2026,
        new Dictionary<string, string> { ["imdb"] = "tt22084616", ["tmdb"] = "969681", ["simkl"] = "1902343" });

    private static RemoteState SimklHasOnlyTheFirstWatch(int? rating = null) => new(
        [new RemoteWatch(RemoteMovie(), O("2026-08-02 03:33:56"))],
        rating is null ? [] : [new RemoteRating(RemoteMovie(), rating.Value, O("2026-08-02 03:28:41"))],
        []);

    [Fact]
    public async Task Brand_New_Day_the_rewatch_missing_from_the_service_is_pushed_once_as_a_rewatch()
    {
        var f = Make(SimklHasOnlyTheFirstWatch(rating: 9));
        SeedBrandNewDay(f.Db, rating: 9);

        var summary = await f.Service.ExportAsync(PluginId, UserId);

        summary.Skipped.Should().BeNull();
        f.PushCalls.Should().Be(1);
        var watch = f.Pushed!.Watches.Should().ContainSingle().Subject;
        watch.IsRewatch.Should().BeTrue();
        var lastPlaybackEvent = f.Db.InteractionEvents.Where(e => e.DeviceName == "Kodi upstairs").Max(e => e.Timestamp);
        watch.WatchedAt.Should().Be(new DateTimeOffset(DateTime.SpecifyKind(lastPlaybackEvent, DateTimeKind.Utc)));   // when the playback ended, not the later reconciled stamp
        f.Pushed.Ratings.Should().BeEmpty();                                                       // same rating on both sides
    }

    [Fact]
    public async Task Once_the_service_has_the_rewatch_nothing_more_is_sent()
    {
        var remote = new RemoteState(
            [new RemoteWatch(RemoteMovie(), O("2026-08-02 03:33:56")), new RemoteWatch(RemoteMovie(), O("2026-10-15 03:21:56"))],
            [], []);
        var f = Make(remote);
        SeedBrandNewDay(f.Db);

        var summary = await f.Service.ExportAsync(PluginId, UserId);

        f.PushCalls.Should().Be(0);
        summary.WatchesPlanned.Should().Be(0);
    }

    [Fact]
    public async Task Preview_only_works_out_the_plan_but_sends_nothing()
    {
        var f = Make(SimklHasOnlyTheFirstWatch(), new ExportOptions(PreviewOnly: true));
        SeedBrandNewDay(f.Db);

        var summary = await f.Service.ExportAsync(PluginId, UserId);

        summary.PreviewOnly.Should().BeTrue();
        summary.WatchesPlanned.Should().Be(1);
        f.PushCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_run_is_skipped_when_neither_side_changed_since_the_last_complete_one()
    {
        var f = Make(SimklHasOnlyTheFirstWatch());
        f.Export.Setup(p => p.GetRemoteFingerprintAsync(It.IsAny<CancellationToken>())).ReturnsAsync("2026-10-19T00:42:03Z");
        SeedBrandNewDay(f.Db);

        var first = await f.Service.ExportAsync(PluginId, UserId);          // sends the rewatch, remembers "caught up"
        var second = await f.Service.ExportAsync(PluginId, UserId);         // nothing changed: skipped, no full read

        first.Skipped.Should().BeNull();
        second.Skipped.Should().Contain("nothing has changed");
        f.PushCalls.Should().Be(1);
        f.Export.Verify(p => p.GetRemoteStateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_change_on_the_service_or_in_chronicle_ends_the_skip()
    {
        var f = Make(SimklHasOnlyTheFirstWatch());
        var fingerprint = "A";
        f.Export.Setup(p => p.GetRemoteFingerprintAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => fingerprint);
        SeedBrandNewDay(f.Db);
        await f.Service.ExportAsync(PluginId, UserId);

        fingerprint = "B";                                                   // something changed on the service
        (await f.Service.ExportAsync(PluginId, UserId)).Skipped.Should().BeNull();
        (await f.Service.ExportAsync(PluginId, UserId)).Skipped.Should().NotBeNull();   // gated again at B

        f.Db.InteractionEvents.Add(new InteractionEvent
        {
            UserId = UserId, MediaItemId = 800226, Timestamp = DateTime.UtcNow, ProgressPercent = 5,
            DeviceName = "Kodi upstairs", CreatedAt = DateTime.UtcNow,
        });
        f.Db.SaveChanges();                                                  // something changed in Chronicle
        (await f.Service.ExportAsync(PluginId, UserId)).Skipped.Should().BeNull();
    }

    [Fact]
    public async Task A_run_that_left_items_unsent_to_respect_the_quota_is_never_marked_caught_up()
    {
        var f = Make(SimklHasOnlyTheFirstWatch());
        f.Export.Setup(p => p.GetRemoteFingerprintAsync(It.IsAny<CancellationToken>())).ReturnsAsync("A");
        f.Export.Setup(p => p.PushAsync(It.IsAny<ExportBatch>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExportPushResult(0, 0, 0, 0, [], Deferred: 5));
        SeedBrandNewDay(f.Db);

        await f.Service.ExportAsync(PluginId, UserId);
        var again = await f.Service.ExportAsync(PluginId, UserId);

        again.Skipped.Should().BeNull();                                     // tries again to send the rest
    }

    [Fact]
    public async Task A_switched_off_export_does_nothing()
    {
        var f = Make(SimklHasOnlyTheFirstWatch(), new ExportOptions(Enabled: false));
        SeedBrandNewDay(f.Db);

        var summary = await f.Service.ExportAsync(PluginId, UserId);

        summary.Skipped.Should().NotBeNull();
        f.PushCalls.Should().Be(0);
        f.Export.Verify(p => p.GetRemoteStateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_failed_read_of_the_service_sends_nothing_and_never_throws()
    {
        var f = Make(new RemoteState([], [], []), remoteThrows: true);
        SeedBrandNewDay(f.Db);

        var summary = await f.Service.ExportAsync(PluginId, UserId);

        summary.Skipped.Should().Contain("SIMKL is down");
        f.PushCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_plugin_that_cannot_export_is_skipped()
    {
        var f = Make(new RemoteState([], [], []));
        var registry = new Mock<IPluginRegistry>();
        registry.Setup(r => r.GetImportProvider(PluginId)).Returns(new Mock<IImportProvider>().Object);
        var service = new ExportService(new ServiceCollection().AddSingleton(f.Db).BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>(), registry.Object, NullLogger<ExportService>.Instance);

        (await service.ExportAsync(PluginId, UserId)).Skipped.Should().Contain("cannot export");
    }

    [Fact]
    public async Task A_watched_episode_the_service_lacks_is_pushed_under_its_shows_ids()
    {
        var f = Make(new RemoteState([], [], []));
        f.Db.MediaItems.AddRange(
            new MediaItem { Id = 10, MediaTypeId = 1, Name = "RuPaul's Drag Race UK", Year = 2019, HierarchyLevel = 0 },
            new MediaItem { Id = 11, MediaTypeId = 1, Name = "Season 8", Number = 8, ParentId = 10, HierarchyLevel = 1 },
            new MediaItem { Id = 12, MediaTypeId = 1, Name = "Snatch Me If You Can", Number = 3, ParentId = 11, HierarchyLevel = 2 });
        f.Db.MediaExternalIds.AddRange(
            new MediaExternalId { MediaItemId = 10, Source = "tvdb", ExternalId = "356452" },
            new MediaExternalId { MediaItemId = 10, Source = "tmdb", ExternalId = "tv:92611" },
            new MediaExternalId { MediaItemId = 12, Source = "imdb", ExternalId = "tt44890392" });     // the episode's own id: not used
        f.Db.UserLibraries.Add(new UserLibrary
        {
            UserId = UserId, MediaItemId = 12, Status = LibraryStatus.Completed, CompletedAt = T("2026-10-13 02:28:42"),
        });
        f.Db.SaveChanges();

        await f.Service.ExportAsync(PluginId, UserId);

        var watch = f.Pushed!.Watches.Should().ContainSingle().Subject;
        watch.IsRewatch.Should().BeFalse();
        watch.Item.MediaType.Should().Be("tv_episode");
        (watch.Item.SeasonNumber, watch.Item.EpisodeNumber).Should().Be((8, 3));
        watch.Item.ShowTitle.Should().Be("RuPaul's Drag Race UK");
        watch.Item.Ids.Keys.Should().BeEquivalentTo("tvdb", "tmdb");
    }
}
