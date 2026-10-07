using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Live;
using Chronicle.Services.Notifications;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Tests.Integration
{
    public class NotificationsAndLiveUpdatesTests : IClassFixture<ChronicleApiFactory>
    {
        private const string Password = "Password123!";
        private readonly ChronicleApiFactory _factory;

        public NotificationsAndLiveUpdatesTests(ChronicleApiFactory factory) { factory.SeedDatabase(); _factory = factory; }

        private static async Task<JsonElement> Json(HttpResponseMessage r) =>
            JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

        private sealed record Person(int Id, HttpClient Client, string Key);

        private async Task<Person> SignInAsync(bool admin)
        {
            var reg = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { username = $"nl_{Guid.NewGuid():N}", password = Password });
            var data = (await Json(reg)).GetProperty("data");
            var id = data.GetProperty("user").GetProperty("id").GetInt32();
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                (await db.Users.FirstAsync(u => u.Id == id)).IsAdmin = admin;
                await db.SaveChangesAsync();
            }
            var key = data.GetProperty("token").GetString()!;
            var c = _factory.CreateClient();
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return new Person(id, c, key);
        }

        private async Task NotifyAsync(int userId, string title, string kind = NotificationKinds.ScanImported, string? key = null)
        {
            using var scope = _factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<INotificationService>().NotifyUserAsync(userId, kind, title, dedupeKey: key);
        }

        // ══ notifications ═════════════════════════════════════════════════════

        [Theory]
        [InlineData("GET", "/api/v1/notifications")]
        [InlineData("GET", "/api/v1/notifications/kinds")]
        [InlineData("POST", "/api/v1/notifications/1/read")]
        [InlineData("POST", "/api/v1/notifications/read-all")]
        [InlineData("DELETE", "/api/v1/notifications/1")]
        [InlineData("DELETE", "/api/v1/notifications/read")]
        [InlineData("GET", "/api/v1/library/changes")]
        public async Task EveryRoute_NeedsASignedInUser(string method, string url)
        {
            (await _factory.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), url))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task AccountsSeeOnlyTheirOwnNotifications()
        {
            var a = await SignInAsync(false); var b = await SignInAsync(false);
            await NotifyAsync(a.Id, "For A");
            await NotifyAsync(b.Id, "For B");

            var list = (await Json(await a.Client.GetAsync("/api/v1/notifications"))).GetProperty("data");

            list.GetProperty("unread").GetInt32().Should().Be(1);
            list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()).Should().Equal("For A");
        }

        [Fact]
        public async Task MarkingRead_Deleting_AndClearing_OnlyEverTouchTheCallersOwn()
        {
            var a = await SignInAsync(false); var b = await SignInAsync(false);
            await NotifyAsync(a.Id, "A1", key: "1"); await NotifyAsync(a.Id, "A2", key: "2"); await NotifyAsync(b.Id, "B1");
            var aItems = (await Json(await a.Client.GetAsync("/api/v1/notifications"))).GetProperty("data").GetProperty("items").EnumerateArray().ToList();
            var bItem = (await Json(await b.Client.GetAsync("/api/v1/notifications"))).GetProperty("data").GetProperty("items")[0].GetProperty("id").GetInt32();
            var first = aItems[0].GetProperty("id").GetInt32();

            (await a.Client.PostAsync($"/api/v1/notifications/{bItem}/read", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await a.Client.DeleteAsync($"/api/v1/notifications/{bItem}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await b.Client.GetAsync("/api/v1/notifications")).Content.ReadAsStringAsync().Result.Should().Contain("\"unread\":1", "B's notice was untouched");

            (await a.Client.PostAsync($"/api/v1/notifications/{first}/read", null)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await Json(await a.Client.GetAsync("/api/v1/notifications"))).GetProperty("data").GetProperty("unread").GetInt32().Should().Be(1);

            (await a.Client.PostAsync("/api/v1/notifications/read-all", null)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await Json(await a.Client.GetAsync("/api/v1/notifications"))).GetProperty("data").GetProperty("unread").GetInt32().Should().Be(0);

            (await a.Client.DeleteAsync("/api/v1/notifications/read")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await Json(await a.Client.GetAsync("/api/v1/notifications"))).GetProperty("data").GetProperty("items").GetArrayLength().Should().Be(0);
            (await Json(await b.Client.GetAsync("/api/v1/notifications"))).GetProperty("data").GetProperty("items").GetArrayLength().Should().Be(1);
        }

        [Fact]
        public async Task OneNotificationCanBeDeleted()
        {
            var a = await SignInAsync(false);
            await NotifyAsync(a.Id, "Gone soon");
            var id = (await Json(await a.Client.GetAsync("/api/v1/notifications"))).GetProperty("data").GetProperty("items")[0].GetProperty("id").GetInt32();

            (await a.Client.DeleteAsync($"/api/v1/notifications/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await a.Client.DeleteAsync($"/api/v1/notifications/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task UnreadOnly_AndLimit_Work()
        {
            var a = await SignInAsync(false);
            for (var i = 0; i < 5; i++) await NotifyAsync(a.Id, $"n{i}");
            await a.Client.PostAsync("/api/v1/notifications/read-all", null);
            await NotifyAsync(a.Id, "fresh");

            var unread = (await Json(await a.Client.GetAsync("/api/v1/notifications?unreadOnly=true"))).GetProperty("data").GetProperty("items");
            var limited = (await Json(await a.Client.GetAsync("/api/v1/notifications?limit=2"))).GetProperty("data").GetProperty("items");

            unread.EnumerateArray().Select(i => i.GetProperty("title").GetString()).Should().Equal("fresh");
            limited.GetArrayLength().Should().Be(2);
        }

        [Fact]
        public async Task TheKindsEndpoint_ListsWhatCanBeSwitchedOff()
        {
            var a = await SignInAsync(false);

            var kinds = (await Json(await a.Client.GetAsync("/api/v1/notifications/kinds"))).GetProperty("data").EnumerateArray().ToList();

            kinds.Select(k => k.GetProperty("kind").GetString()).Should().Contain([NotificationKinds.TaskFailed, NotificationKinds.ScanImported, NotificationKinds.PluginUpdate]);
            kinds.Should().OnlyContain(k => k.GetProperty("label").GetString()!.Length > 0);
        }

        [Fact]
        public async Task SwitchingAKindOff_StopsThoseNotices_AndIsRememberedAcrossRequests()
        {
            var admin = await SignInAsync(true);

            var patch = await admin.Client.PatchAsJsonAsync("/api/v1/users/me/preferences", new { mutedNotificationKinds = new[] { NotificationKinds.TaskFailed, "made-up-kind" } });
            patch.StatusCode.Should().Be(HttpStatusCode.OK);
            (await Json(patch)).GetProperty("data").GetProperty("mutedNotificationKinds").EnumerateArray().Select(k => k.GetString())
                .Should().Equal(NotificationKinds.TaskFailed);   // unknown kinds are dropped

            using (var scope = _factory.Services.CreateScope())
            {
                var svc = scope.ServiceProvider.GetRequiredService<INotificationService>();
                await svc.NotifyUserAsync(admin.Id, NotificationKinds.TaskFailed, "muted one");
                await svc.NotifyUserAsync(admin.Id, NotificationKinds.ScanImported, "wanted one");
            }

            (await Json(await admin.Client.GetAsync("/api/v1/notifications"))).GetProperty("data").GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("title").GetString()).Should().Equal("wanted one");
            (await Json(await admin.Client.GetAsync("/api/v1/users/me/preferences"))).GetProperty("data").GetProperty("mutedNotificationKinds")
                .EnumerateArray().Select(k => k.GetString()).Should().Equal(NotificationKinds.TaskFailed);

            await admin.Client.PatchAsJsonAsync("/api/v1/users/me/preferences", new { mutedNotificationKinds = Array.Empty<string>() });
            (await Json(await admin.Client.GetAsync("/api/v1/users/me/preferences"))).GetProperty("data").GetProperty("mutedNotificationKinds").GetArrayLength().Should().Be(0);
        }

        [Fact]
        public async Task PatchingOtherPreferences_LeavesTheMutedKindsAlone()
        {
            var a = await SignInAsync(false);
            await a.Client.PatchAsJsonAsync("/api/v1/users/me/preferences", new { mutedNotificationKinds = new[] { NotificationKinds.PluginUpdate } });

            await a.Client.PatchAsJsonAsync("/api/v1/users/me/preferences", new { showNowPlayingBanner = false });

            (await Json(await a.Client.GetAsync("/api/v1/users/me/preferences"))).GetProperty("data").GetProperty("mutedNotificationKinds")
                .EnumerateArray().Select(k => k.GetString()).Should().Equal(NotificationKinds.PluginUpdate);
        }

        // ══ live updates ══════════════════════════════════════════════════════

        private static async Task<(Guid Epoch, long Revision, bool Reset, List<(int Id, string Kind)> Changes, int Unread)> ChangesAsync(HttpClient c, long? since = null, Guid? epoch = null)
        {
            var url = "/api/v1/library/changes" + (since is null ? "" : $"?since={since}&epoch={epoch}");
            var d = (await Json(await c.GetAsync(url))).GetProperty("data");
            return (d.GetProperty("epoch").GetGuid(), d.GetProperty("revision").GetInt64(), d.GetProperty("reset").GetBoolean(),
                d.GetProperty("changes").EnumerateArray().Select(x => (x.GetProperty("itemId").GetInt32(), x.GetProperty("kind").GetString()!)).ToList(),
                d.GetProperty("unreadNotifications").GetInt32());
        }

        private async Task<int> CreateItemAsync(HttpClient c, string name)
        {
            var response = await c.PostAsJsonAsync("/api/v1/media", new { name, mediaTypeId = 1 });
            response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
            return (await Json(response)).GetProperty("data").GetProperty("id").GetInt32();
        }

        [Fact]
        public async Task TheFirstAsk_GivesABaseline_AndNothingElse()
        {
            var a = await SignInAsync(false);

            var first = await ChangesAsync(a.Client);

            first.Reset.Should().BeFalse();
            first.Changes.Should().BeEmpty();
            first.Epoch.Should().NotBe(Guid.Empty);
        }

        [Fact]
        public async Task ANewItem_ShowsUpInTheNextAsk_AndNotTheOneAfter()
        {
            var a = await SignInAsync(false);
            var baseline = await ChangesAsync(a.Client);

            var id = await CreateItemAsync(a.Client, $"Live {Guid.NewGuid():N}");
            var next = await ChangesAsync(a.Client, baseline.Revision, baseline.Epoch);
            var after = await ChangesAsync(a.Client, next.Revision, next.Epoch);

            next.Changes.Should().Contain((id, "changed"));
            after.Changes.Should().NotContain(c => c.Id == id);
        }

        [Fact]
        public async Task APersonsOwnLibraryChange_IsVisibleToThemAndNotToSomeoneElse()
        {
            var a = await SignInAsync(false); var b = await SignInAsync(false);
            var itemId = await CreateItemAsync(a.Client, $"Shared {Guid.NewGuid():N}");
            var aBase = await ChangesAsync(a.Client); var bBase = await ChangesAsync(b.Client);

            var add = await a.Client.PostAsJsonAsync("/api/v1/library", new { mediaItemId = itemId, status = "Watching" });
            add.StatusCode.Should().Be(HttpStatusCode.OK);

            (await ChangesAsync(a.Client, aBase.Revision, aBase.Epoch)).Changes.Select(c => c.Id).Should().Contain(itemId);
            (await ChangesAsync(b.Client, bBase.Revision, bBase.Epoch)).Changes.Select(c => c.Id).Should().NotContain(itemId,
                "that is A's library entry, not B's");
        }

        [Fact]
        public async Task ADeletedItem_IsReportedAsDeleted()
        {
            var a = await SignInAsync(true);
            var id = await CreateItemAsync(a.Client, $"Doomed {Guid.NewGuid():N}");
            var baseline = await ChangesAsync(a.Client);

            (await a.Client.DeleteAsync($"/api/v1/media/{id}")).StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);

            (await ChangesAsync(a.Client, baseline.Revision, baseline.Epoch)).Changes.Should().Contain((id, "deleted"));
        }

        [Fact]
        public async Task AnotherServerRun_MeansRefetch()
        {
            var a = await SignInAsync(false);
            var baseline = await ChangesAsync(a.Client);

            var stale = await ChangesAsync(a.Client, baseline.Revision, Guid.NewGuid());

            stale.Reset.Should().BeTrue();
            stale.Epoch.Should().Be(baseline.Epoch, "the client is handed the current epoch to continue from");
        }

        [Fact]
        public async Task TheUnreadCount_RidesOnTheSamePoll()
        {
            var a = await SignInAsync(false);
            var before = await ChangesAsync(a.Client);
            await NotifyAsync(a.Id, "ping");

            var after = await ChangesAsync(a.Client, before.Revision, before.Epoch);

            after.Unread.Should().Be(before.Unread + 1);
        }

        [Fact]
        public async Task ABulkReset_TellsEveryoneToRefetch()
        {
            var a = await SignInAsync(false);
            var baseline = await ChangesAsync(a.Client);

            _factory.Services.GetRequiredService<LibraryChangeFeed>().MarkAllChanged();

            (await ChangesAsync(a.Client, baseline.Revision, baseline.Epoch)).Reset.Should().BeTrue();
        }

        [Fact]
        public async Task PollingInTheBackground_DoesNotKeepASessionAlive()
        {
            var a = await SignInAsync(false);
            var store = _factory.Services.GetRequiredService<Chronicle.Services.Security.ISessionStore>();
            var before = store.Validate(a.Key, touch: false).Session!.LastSeenAt;
            await Task.Delay(30);

            var poll = new HttpRequestMessage(HttpMethod.Get, "/api/v1/library/changes");
            poll.Headers.Authorization = new AuthenticationHeaderValue("Bearer", a.Key);
            poll.Headers.Add("X-Chronicle-Background", "1");
            (await _factory.CreateClient().SendAsync(poll)).StatusCode.Should().Be(HttpStatusCode.OK);

            store.Validate(a.Key, touch: false).Session!.LastSeenAt.Should().Be(before);
        }
    }
}
