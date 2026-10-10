using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Tests.Integration
{
    /// <summary>Settings -> Media Types: listing, registering a type of your own, editing, protecting edits from plugins, deleting.</summary>
    public class MediaTypesEndpointsTests : IClassFixture<ChronicleApiFactory>
    {
        private const string Password = "Password123!";
        private readonly ChronicleApiFactory _factory;

        public MediaTypesEndpointsTests(ChronicleApiFactory factory) { factory.SeedDatabase(); _factory = factory; }

        private static async Task<JsonElement> Json(HttpResponseMessage r) =>
            JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

        private async Task<(HttpClient Client, string Key)> SignInAsync(bool admin)
        {
            var reg = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { username = $"mt_{Guid.NewGuid():N}", password = Password });
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
            return (c, key);
        }

        private async Task<HttpClient> AdminAsync() => (await SignInAsync(true)).Client;

        private static object Request(string? name = null, string display = "Comics", int levels = 3, string[]? labels = null,
            string verb = "read", string unit = "pages", string? scan = null, bool active = true) => new
        {
            name, displayName = display, description = "Comic books", hierarchyLevels = levels,
            hierarchyLabels = labels ?? (levels == 3 ? ["Series", "Volume", "Issue"] : Enumerable.Repeat("Item", levels).ToArray()),
            interactionVerb = verb, progressUnit = unit, supportsCollections = false, isTrackable = true, scanStrategy = scan, isActive = active,
        };

        private static string UniqueName() => "t" + Guid.NewGuid().ToString("N")[..10];

        private async Task<JsonElement> CreateAsync(HttpClient admin, string? name = null, object? body = null)
        {
            var response = await admin.PostAsJsonAsync("/api/v1/media-types", body ?? Request(name ?? UniqueName()));
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            return (await Json(response)).GetProperty("data");
        }

        // ══ access ════════════════════════════════════════════════════════════

        [Theory]
        [InlineData("GET", "/api/v1/media-types")]
        [InlineData("POST", "/api/v1/media-types")]
        [InlineData("PUT", "/api/v1/media-types/1")]
        [InlineData("DELETE", "/api/v1/media-types/1")]
        [InlineData("POST", "/api/v1/media-types/1/release-to-plugins")]
        public async Task EveryRoute_NeedsAnAdministratorWithABrowserSession(string method, string url)
        {
            var (user, _) = await SignInAsync(admin: false);
            var (admin, _) = await SignInAsync(admin: true);
            var minted = await admin.PostAsJsonAsync("/api/v1/tokens", new { name = "s", scope = "full" });
            var byKey = _factory.CreateClient();
            byKey.DefaultRequestHeaders.Add("X-API-Key", (await Json(minted)).GetProperty("data").GetProperty("token").GetString()!);

            (await _factory.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), url))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await user.SendAsync(new HttpRequestMessage(new HttpMethod(method), url))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await byKey.SendAsync(new HttpRequestMessage(new HttpMethod(method), url))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // ══ listing ═══════════════════════════════════════════════════════════

        [Fact]
        public async Task List_ShowsEveryType_WithItsLabelsAndItemCount()
        {
            var admin = await AdminAsync();

            var types = (await Json(await admin.GetAsync("/api/v1/media-types"))).GetProperty("data").EnumerateArray().ToList();

            var tv = types.Single(t => t.GetProperty("name").GetString() == "tv");
            tv.GetProperty("hierarchyLabels").EnumerateArray().Select(l => l.GetString()).Should().Equal("Show", "Season", "Episode");
            tv.GetProperty("interactionVerb").GetString().Should().Be("watched");
            tv.GetProperty("isBuiltIn").GetBoolean().Should().BeTrue();
            tv.GetProperty("itemCount").GetInt32().Should().BeGreaterThanOrEqualTo(0);
            tv.GetProperty("plugins").ValueKind.Should().Be(JsonValueKind.Array);
        }

        [Fact]
        public async Task ThePublicTypeList_NowCarriesTheVerbAndLevelNames_ForWordingTheInterface()
        {
            var (user, _) = await SignInAsync(admin: false);

            var types = (await Json(await user.GetAsync("/api/v1/media/types"))).GetProperty("data").EnumerateArray().ToList();

            var tv = types.Single(t => t.GetProperty("name").GetString() == "tv");
            tv.GetProperty("interactionVerb").GetString().Should().Be("watched");
            tv.GetProperty("hierarchyLabels").EnumerateArray().Select(l => l.GetString()).Should().Equal("Show", "Season", "Episode");
        }

        // ══ create ════════════════════════════════════════════════════════════

        [Fact]
        public async Task ACustomTypeCanBeRegistered_AndShowsUpEverywhere()
        {
            var admin = await AdminAsync();
            var name = UniqueName();

            var created = await CreateAsync(admin, name);

            created.GetProperty("name").GetString().Should().Be(name);
            created.GetProperty("isBuiltIn").GetBoolean().Should().BeFalse();
            created.GetProperty("isUserModified").GetBoolean().Should().BeTrue("a person made it, so no plugin may rewrite it");
            created.GetProperty("itemCount").GetInt32().Should().Be(0);
            created.GetProperty("plugins").GetArrayLength().Should().Be(0, "no installed plugin handles it yet");

            var picker = (await Json(await admin.GetAsync("/api/v1/media/types"))).GetProperty("data").EnumerateArray();
            var mine = picker.Single(t => t.GetProperty("name").GetString() == name);
            mine.GetProperty("interactionVerb").GetString().Should().Be("read");
            mine.GetProperty("hierarchyLabels").EnumerateArray().Select(l => l.GetString()).Should().Equal("Series", "Volume", "Issue");
        }

        [Fact]
        public async Task ADuplicateName_IsRefused()
        {
            var admin = await AdminAsync();
            var name = UniqueName();
            await CreateAsync(admin, name);

            var again = await admin.PostAsJsonAsync("/api/v1/media-types", Request(name));

            again.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await admin.PostAsJsonAsync("/api/v1/media-types", Request("tv"))).StatusCode.Should().Be(HttpStatusCode.Conflict, "built-in names are taken too");
        }

        [Theory]
        [InlineData("Bad Name", 3, 3, "read", "pages", "internal name")]
        [InlineData("fine-name", 3, 2, "read", "pages", "one label for each")]
        [InlineData("fine-name", 9, 9, "read", "pages", "levels")]
        [InlineData("fine-name", 3, 3, "Read!", "pages", "action word")]
        [InlineData("fine-name", 3, 3, "read", "Pages!", "progress unit")]
        public async Task InvalidTypes_AreRefused_WithAReadableReason(string name, int levels, int labelCount, string verb, string unit, string mentions)
        {
            var admin = await AdminAsync();
            var body = Request(name + Guid.NewGuid().ToString("N")[..4], levels: levels, labels: Enumerable.Repeat("L", labelCount).ToArray(), verb: verb, unit: unit);
            if (name == "Bad Name") body = Request(name, levels: levels, labels: Enumerable.Repeat("L", labelCount).ToArray(), verb: verb, unit: unit);

            var response = await admin.PostAsJsonAsync("/api/v1/media-types", body);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await Json(response)).GetProperty("error").GetProperty("message").GetString().Should().ContainEquivalentOf(mentions);
        }

        // ══ edit ══════════════════════════════════════════════════════════════

        [Fact]
        public async Task Editing_ChangesTheFields_ButNeverTheInternalName_AndMarksItAdministratorOwned()
        {
            var admin = await AdminAsync();
            var created = await CreateAsync(admin);
            var id = created.GetProperty("id").GetInt32();
            var name = created.GetProperty("name").GetString()!;

            var response = await admin.PutAsJsonAsync($"/api/v1/media-types/{id}",
                Request("a-different-name", "Graphic Novels", 3, ["Imprint", "Volume", "Issue"], "enjoyed", "chapters", "audiobook"));

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var t = (await Json(response)).GetProperty("data");
            t.GetProperty("name").GetString().Should().Be(name, "plugins refer to the internal name");
            t.GetProperty("displayName").GetString().Should().Be("Graphic Novels");
            t.GetProperty("interactionVerb").GetString().Should().Be("enjoyed");
            t.GetProperty("progressUnit").GetString().Should().Be("chapters");
            t.GetProperty("scanStrategy").GetString().Should().Be("audiobook");
            t.GetProperty("isUserModified").GetBoolean().Should().BeTrue();
        }

        [Fact]
        public async Task TheNumberOfLevels_CannotChangeOnceItemsExist_ButCanBeforeThen()
        {
            var admin = await AdminAsync();
            var created = await CreateAsync(admin);
            var id = created.GetProperty("id").GetInt32();

            (await admin.PutAsJsonAsync($"/api/v1/media-types/{id}", Request(display: "Two level", levels: 2, labels: ["A", "B"]))).StatusCode
                .Should().Be(HttpStatusCode.OK, "empty types can be reshaped");

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                db.MediaItems.Add(new MediaItem { Name = "First", MediaTypeId = id, HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }

            var blocked = await admin.PutAsJsonAsync($"/api/v1/media-types/{id}", Request(display: "Three level", levels: 3));
            blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await Json(blocked)).GetProperty("error").GetProperty("code").GetString().Should().Be("HAS_ITEMS");

            (await admin.PutAsJsonAsync($"/api/v1/media-types/{id}", Request(display: "Renamed", levels: 2, labels: ["A", "B"]))).StatusCode
                .Should().Be(HttpStatusCode.OK, "everything except the level count can still change");
        }

        [Fact]
        public async Task ABuiltInTypeThatIsInUse_CannotBeSwitchedOff()
        {
            var admin = await AdminAsync();
            int tvId;
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                var tv = await db.MediaTypes.FirstAsync(t => t.Name == "tv");
                tvId = tv.Id;
                if (!await db.MediaItems.AnyAsync(i => i.MediaTypeId == tvId))
                {
                    db.MediaItems.Add(new MediaItem { Name = "A Show", MediaTypeId = tvId, HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
                    await db.SaveChangesAsync();
                }
            }

            var response = await admin.PutAsJsonAsync($"/api/v1/media-types/{tvId}",
                Request(display: "TV Shows", levels: 3, labels: ["Show", "Season", "Episode"], verb: "watched", unit: "minutes", active: false));

            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        [Fact]
        public async Task ASwitchedOffType_DisappearsFromThePickers_ButStaysInTheAdminList()
        {
            var admin = await AdminAsync();
            var created = await CreateAsync(admin);
            var id = created.GetProperty("id").GetInt32();
            var name = created.GetProperty("name").GetString();

            await admin.PutAsJsonAsync($"/api/v1/media-types/{id}", Request(display: "Off", active: false));

            (await Json(await admin.GetAsync("/api/v1/media/types"))).GetProperty("data").EnumerateArray()
                .Select(t => t.GetProperty("name").GetString()).Should().NotContain(name);
            (await Json(await admin.GetAsync("/api/v1/media-types"))).GetProperty("data").EnumerateArray()
                .Select(t => t.GetProperty("name").GetString()).Should().Contain(name);
        }

        [Fact]
        public async Task EditingAnUnknownType_Is404()
        {
            var admin = await AdminAsync();

            (await admin.PutAsJsonAsync("/api/v1/media-types/999999", Request())).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task ReleasingAType_HandsItBackToItsPlugins()
        {
            var admin = await AdminAsync();
            var created = await CreateAsync(admin);
            var id = created.GetProperty("id").GetInt32();

            var response = await admin.PostAsync($"/api/v1/media-types/{id}/release-to-plugins", null);

            (await Json(response)).GetProperty("data").GetProperty("isUserModified").GetBoolean().Should().BeFalse();
            (await admin.PostAsync("/api/v1/media-types/999999/release-to-plugins", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        // ══ delete ════════════════════════════════════════════════════════════

        [Fact]
        public async Task AnEmptyCustomType_CanBeDeleted()
        {
            var admin = await AdminAsync();
            var created = await CreateAsync(admin);

            (await admin.DeleteAsync($"/api/v1/media-types/{created.GetProperty("id").GetInt32()}")).StatusCode.Should().Be(HttpStatusCode.OK);

            (await Json(await admin.GetAsync("/api/v1/media-types"))).GetProperty("data").EnumerateArray()
                .Select(t => t.GetProperty("name").GetString()).Should().NotContain(created.GetProperty("name").GetString());
        }

        [Fact]
        public async Task ATypeWithItems_CannotBeDeleted()
        {
            var admin = await AdminAsync();
            var created = await CreateAsync(admin);
            var id = created.GetProperty("id").GetInt32();
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                db.MediaItems.Add(new MediaItem { Name = "Keep me", MediaTypeId = id, HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }

            var response = await admin.DeleteAsync($"/api/v1/media-types/{id}");

            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await Json(response)).GetProperty("error").GetProperty("code").GetString().Should().Be("HAS_ITEMS");
        }

        [Fact]
        public async Task BuiltInTypes_CannotBeDeleted()
        {
            var admin = await AdminAsync();
            int movies;
            using (var scope = _factory.Services.CreateScope())
                movies = (await scope.ServiceProvider.GetRequiredService<ChronicleDbContext>().MediaTypes.FirstAsync(t => t.Name == "movies")).Id;

            var response = await admin.DeleteAsync($"/api/v1/media-types/{movies}");

            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await Json(response)).GetProperty("error").GetProperty("code").GetString().Should().Be("BUILT_IN");
        }

        [Fact]
        public async Task DeletingAnUnknownType_Is404()
        {
            var admin = await AdminAsync();

            (await admin.DeleteAsync("/api/v1/media-types/999999")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }
}
