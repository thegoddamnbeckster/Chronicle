using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Notifications;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Tests.Unit.Services;

public class NotificationServiceTests : IDisposable
{
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private readonly ChronicleDbContext _db;
    private readonly FakeClock _clock = new();
    private readonly NotificationService _svc;

    public NotificationServiceTests()
    {
        _db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _svc = new NotificationService(_db, _clock);
    }

    public void Dispose() => _db.Dispose();

    private User AddUser(string name, bool admin = false, bool active = true, string[]? muted = null)
    {
        var prefs = muted is null ? "{}" : JsonSerializer.Serialize(new UserPreferences { MutedNotificationKinds = muted });
        var u = new User { Username = name, IsAdmin = admin, IsActive = active, PreferencesJson = prefs, PasswordHash = "x", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.Users.Add(u);
        _db.SaveChanges();
        return u;
    }

    // ══ who gets told ═════════════════════════════════════════════════════════

    [Fact]
    public async Task AdminNotices_GoToEveryActiveAdministrator_AndNobodyElse()
    {
        var a1 = AddUser("a1", admin: true); var a2 = AddUser("a2", admin: true);
        var inactiveAdmin = AddUser("old", admin: true, active: false); var ordinary = AddUser("bob");

        var told = await _svc.NotifyAdminsAsync(NotificationKinds.TaskFailed, "Backup failed");

        told.Should().Be(2);
        _db.Notifications.Select(n => n.UserId).Should().BeEquivalentTo([a1.Id, a2.Id]);
    }

    [Fact]
    public async Task ANoticeForOnePerson_GoesToThemAlone()
    {
        var a = AddUser("a", admin: true); var b = AddUser("b");

        await _svc.NotifyUserAsync(b.Id, NotificationKinds.ScanImported, "Hi");

        _db.Notifications.Single().UserId.Should().Be(b.Id);
    }

    [Fact]
    public async Task APersonWhoSwitchedAKindOff_IsNotToldAboutIt_ButStillToldAboutOthers()
    {
        var quiet = AddUser("quiet", admin: true, muted: [NotificationKinds.ScanImported]);
        var loud = AddUser("loud", admin: true);

        (await _svc.NotifyAdminsAsync(NotificationKinds.ScanImported, "Found things")).Should().Be(1);
        (await _svc.NotifyAdminsAsync(NotificationKinds.TaskFailed, "Broke")).Should().Be(2);

        _db.Notifications.Where(n => n.UserId == quiet.Id).Select(n => n.Kind).Should().Equal(NotificationKinds.TaskFailed);
        _db.Notifications.Count(n => n.UserId == loud.Id).Should().Be(2);
    }

    [Fact]
    public async Task UnreadablePreferences_DoNotStopNotifications()
    {
        var a = AddUser("a", admin: true);
        a.PreferencesJson = "{not json";
        await _db.SaveChangesAsync();

        (await _svc.NotifyAdminsAsync(NotificationKinds.TaskFailed, "Broke")).Should().Be(1);
    }

    [Fact]
    public async Task NoAdministrators_MeansNobodyIsToldAndNothingThrows()
    {
        AddUser("bob");

        (await _svc.NotifyAdminsAsync(NotificationKinds.TaskFailed, "Broke")).Should().Be(0);
    }

    // ══ not saying it twice ═══════════════════════════════════════════════════

    [Fact]
    public async Task TheSameThing_IsNotAnnouncedAgainWhileTheFirstIsStillUnread()
    {
        var a = AddUser("a", admin: true);
        await _svc.NotifyAdminsAsync(NotificationKinds.TaskFailed, "Backup failed", dedupeKey: "task:backup");

        (await _svc.NotifyAdminsAsync(NotificationKinds.TaskFailed, "Backup failed again", dedupeKey: "task:backup")).Should().Be(0);
        _db.Notifications.Should().ContainSingle();

        await _svc.MarkAllReadAsync(a.Id);
        (await _svc.NotifyAdminsAsync(NotificationKinds.TaskFailed, "Backup failed a third time", dedupeKey: "task:backup")).Should().Be(1,
            "once it was read, a recurrence is news again");
    }

    [Fact]
    public async Task AThingAnnouncedOnce_IsNeverAnnouncedAgain_EvenAfterItWasRead()
    {
        var a = AddUser("a", admin: true);
        await _svc.NotifyAdminsAsync(NotificationKinds.PluginUpdate, "TMDB 2.0", dedupeKey: "tmdb:2.0", once: true);
        await _svc.MarkAllReadAsync(a.Id);

        (await _svc.NotifyAdminsAsync(NotificationKinds.PluginUpdate, "TMDB 2.0", dedupeKey: "tmdb:2.0", once: true)).Should().Be(0);
        (await _svc.NotifyAdminsAsync(NotificationKinds.PluginUpdate, "TMDB 2.1", dedupeKey: "tmdb:2.1", once: true)).Should().Be(1, "a newer version is a new thing");
    }

    [Fact]
    public async Task DedupeIsPerPersonAndPerKind()
    {
        var a = AddUser("a", admin: true); var b = AddUser("b", admin: true);
        await _svc.NotifyUserAsync(a.Id, NotificationKinds.TaskFailed, "x", dedupeKey: "k");

        (await _svc.NotifyAdminsAsync(NotificationKinds.TaskFailed, "x", dedupeKey: "k")).Should().Be(1, "only b had not been told");
        (await _svc.NotifyAdminsAsync(NotificationKinds.ScanImported, "x", dedupeKey: "k")).Should().Be(2, "same key, different kind");
    }

    [Fact]
    public async Task WithoutADedupeKey_EveryNoticeIsDelivered()
    {
        AddUser("a", admin: true);
        await _svc.NotifyAdminsAsync(NotificationKinds.ScanImported, "5 new items");
        await _svc.NotifyAdminsAsync(NotificationKinds.ScanImported, "5 new items");

        _db.Notifications.Count().Should().Be(2);
    }

    // ══ what is stored ════════════════════════════════════════════════════════

    [Theory]
    [InlineData("/settings/database", "/settings/database")]
    [InlineData("/library?x=1", "/library?x=1")]
    [InlineData("https://evil.example/login", null)]
    [InlineData("//evil.example", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("/a\\b", null)]
    [InlineData("settings", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public async Task OnlyInAppPathsAreKeptAsLinks(string? link, string? expected)
    {
        var a = AddUser("a", admin: true);

        await _svc.NotifyUserAsync(a.Id, NotificationKinds.TaskFailed, "t", link: link);

        _db.Notifications.Single().Link.Should().Be(expected);
    }

    [Fact]
    public async Task OverlongTextIsClipped_NotRejected()
    {
        var a = AddUser("a", admin: true);

        await _svc.NotifyUserAsync(a.Id, NotificationKinds.TaskFailed, new string('t', 500), new string('b', 5000), dedupeKey: new string('k', 500));

        var n = _db.Notifications.Single();
        n.Title.Length.Should().Be(NotificationService.MaxTitle);
        n.Body!.Length.Should().Be(NotificationService.MaxBody);
        n.DedupeKey!.Length.Should().Be(NotificationService.MaxKey);
    }

    [Fact]
    public async Task UnknownKinds_AndBlankTitles_AreProgrammingErrors()
    {
        var a = AddUser("a", admin: true);

        await _svc.Invoking(s => s.NotifyUserAsync(a.Id, "made.up", "t")).Should().ThrowAsync<ArgumentException>();
        await _svc.Invoking(s => s.NotifyUserAsync(a.Id, NotificationKinds.TaskFailed, "   ")).Should().ThrowAsync<ArgumentException>();
        _db.Notifications.Should().BeEmpty();
    }

    // ══ reading and tidying ═══════════════════════════════════════════════════

    [Fact]
    public async Task TheList_IsNewestFirst_LimitedAndCountsUnread()
    {
        var a = AddUser("a", admin: true);
        for (var i = 1; i <= 5; i++)
        {
            await _svc.NotifyUserAsync(a.Id, NotificationKinds.ScanImported, $"n{i}");
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        var page = await _svc.ListAsync(a.Id, 3, unreadOnly: false);

        page.Items.Select(i => i.Title).Should().Equal("n5", "n4", "n3");
        page.Unread.Should().Be(5);
        page.Items.Should().OnlyContain(i => !i.IsRead);
    }

    [Fact]
    public async Task ThePageSizeIsClamped()
    {
        var a = AddUser("a", admin: true);
        await _svc.NotifyUserAsync(a.Id, NotificationKinds.ScanImported, "n");

        (await _svc.ListAsync(a.Id, 0, false)).Items.Should().HaveCount(1);
        (await _svc.ListAsync(a.Id, 100000, false)).Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task ReadingIsPerPerson_AndAnotherPersonsNoticeLooksMissing()
    {
        var a = AddUser("a", admin: true); var b = AddUser("b", admin: true);
        await _svc.NotifyAdminsAsync(NotificationKinds.TaskFailed, "Broke");
        var aId = _db.Notifications.Single(n => n.UserId == a.Id).Id;

        (await _svc.MarkReadAsync(b.Id, aId)).Should().BeFalse("it is not b's");
        (await _svc.DeleteAsync(b.Id, aId)).Should().BeFalse();
        (await _svc.MarkReadAsync(a.Id, aId)).Should().BeTrue();

        (await _svc.UnreadCountAsync(a.Id)).Should().Be(0);
        (await _svc.UnreadCountAsync(b.Id)).Should().Be(1, "b still has theirs");
    }

    [Fact]
    public async Task MarkingReadTwice_KeepsTheFirstTime()
    {
        var a = AddUser("a", admin: true);
        await _svc.NotifyUserAsync(a.Id, NotificationKinds.ScanImported, "n");
        var id = _db.Notifications.Single().Id;
        await _svc.MarkReadAsync(a.Id, id);
        var first = _db.Notifications.Single().ReadAt;
        _clock.Advance(TimeSpan.FromHours(1));

        await _svc.MarkReadAsync(a.Id, id);

        _db.Notifications.Single().ReadAt.Should().Be(first);
    }

    [Fact]
    public async Task UnreadOnly_HidesWhatWasRead()
    {
        var a = AddUser("a", admin: true);
        await _svc.NotifyUserAsync(a.Id, NotificationKinds.ScanImported, "old");
        await _svc.MarkAllReadAsync(a.Id);
        await _svc.NotifyUserAsync(a.Id, NotificationKinds.ScanImported, "new");

        (await _svc.ListAsync(a.Id, 10, unreadOnly: true)).Items.Select(i => i.Title).Should().Equal("new");
    }

    [Fact]
    public async Task ClearingRead_LeavesUnreadAlone_AndOnlyTouchesTheCaller()
    {
        var a = AddUser("a", admin: true); var b = AddUser("b", admin: true);
        await _svc.NotifyAdminsAsync(NotificationKinds.TaskFailed, "one", dedupeKey: "1");
        await _svc.MarkAllReadAsync(a.Id);
        await _svc.NotifyUserAsync(a.Id, NotificationKinds.ScanImported, "two");

        (await _svc.DeleteReadAsync(a.Id)).Should().Be(1);

        _db.Notifications.Count(n => n.UserId == a.Id).Should().Be(1);
        _db.Notifications.Count(n => n.UserId == b.Id).Should().Be(1);
    }

    [Fact]
    public async Task OldNoticesArePurged_RecentOnesKept()
    {
        var a = AddUser("a", admin: true);
        await _svc.NotifyUserAsync(a.Id, NotificationKinds.ScanImported, "ancient");
        _clock.Advance(TimeSpan.FromDays(100));
        await _svc.NotifyUserAsync(a.Id, NotificationKinds.ScanImported, "recent");

        (await _svc.PurgeOlderThanAsync(TimeSpan.FromDays(90))).Should().Be(1);

        _db.Notifications.Select(n => n.Title).Should().Equal("recent");
    }

    [Fact]
    public void DeletingAUser_RemovesTheirNotices_ByCascadeInTheModel()
    {
        _db.Model.FindEntityType(typeof(Notification))!.GetForeignKeys().Single().DeleteBehavior
            .Should().Be(DeleteBehavior.Cascade);
    }

    [Fact]
    public void EveryKindHasALabelAndADescription_AndIsKnown()
    {
        foreach (var info in NotificationKinds.All)
        {
            info.Label.Should().NotBeNullOrWhiteSpace();
            info.Description.Should().NotBeNullOrWhiteSpace();
            NotificationKinds.IsKnown(info.Kind).Should().BeTrue();
        }
        NotificationKinds.All.Select(i => i.Kind).Should().OnlyHaveUniqueItems();
        NotificationKinds.IsKnown("nope").Should().BeFalse();
        NotificationKinds.IsKnown(null).Should().BeFalse();
    }
}
