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
    /// <summary>Chronicle running on PostgreSQL: the schema comes from the PostgreSQL migrations and the main paths work.</summary>
    public class PostgresSmokeTests : IClassFixture<PostgresApiFactory>
    {
        private const string Password = "Password123!";
        private readonly PostgresApiFactory _factory;

        public PostgresSmokeTests(PostgresApiFactory factory) => _factory = factory;

        private static async Task<JsonElement> Json(HttpResponseMessage r) =>
            JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

        private async Task<HttpClient> AdminAsync()
        {
            var response = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { username = $"pg_{Guid.NewGuid():N}", password = Password });
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var data = (await Json(response)).GetProperty("data");
            var id = data.GetProperty("user").GetProperty("id").GetInt32();
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                (await db.Users.FirstAsync(u => u.Id == id)).IsAdmin = true;
                await db.SaveChangesAsync();
            }
            var c = _factory.CreateClient();
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", data.GetProperty("token").GetString()!);
            return c;
        }

        [PostgresFact]
        public async Task TheApplicationStarts_OnTheMigratedSchema_AndKnowsItsMigrationHistory()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

            db.Database.IsNpgsql().Should().BeTrue();
            (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
            (await db.Database.GetAppliedMigrationsAsync()).Should().NotBeEmpty();
            (await db.MediaTypes.CountAsync()).Should().BeGreaterThan(2);
        }

        [PostgresFact]
        public async Task SignUp_SignIn_AndWhoAmI_Work()
        {
            var admin = await AdminAsync();

            var me = await admin.GetAsync("/api/v1/users/me");

            me.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [PostgresFact]
        public async Task MediaTypes_CanBeListedCreatedAndEdited()
        {
            var admin = await AdminAsync();
            var name = "t" + Guid.NewGuid().ToString("N")[..10];

            var created = await admin.PostAsJsonAsync("/api/v1/media-types", new
            {
                name, displayName = "Comics", description = "", hierarchyLevels = 1, hierarchyLabels = new[] { "Item" },
                interactionVerb = "read", progressUnit = "pages", supportsCollections = false, isTrackable = true, isActive = true,
            });

            created.StatusCode.Should().Be(HttpStatusCode.OK);
            var list = await Json(await admin.GetAsync("/api/v1/media-types"));
            list.GetProperty("data").EnumerateArray().Select(t => t.GetProperty("name").GetString()).Should().Contain(name);
        }

        [PostgresFact]
        public async Task KnownFileNames_AreLookedUpIgnoringCase_LikeOnSqlite()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var type = await db.MediaTypes.FirstAsync();
            var item = new MediaItem { Name = "Case Test", MediaTypeId = type.Id, HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.MediaItems.Add(item);
            await db.SaveChangesAsync();
            db.MediaItemKnownFileNames.Add(new MediaItemKnownFileName { MediaItemId = item.Id, FileName = "The Exorcist (1973).MKV" });
            await db.SaveChangesAsync();

            var found = await db.MediaItemKnownFileNames.AnyAsync(k => k.FileName == "the exorcist (1973).mkv");

            found.Should().BeTrue();
        }

        [PostgresFact]
        public async Task Search_IgnoresCase_LikeOnSqlite()
        {
            var admin = await AdminAsync();
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                var type = await db.MediaTypes.FirstAsync();
                db.MediaItems.Add(new MediaItem { Name = "The Zebra Matrix Quest", MediaTypeId = type.Id, HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }

            var response = await admin.GetAsync("/api/v1/media/search?query=ZEBRA%20matrix");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync()).Should().Contain("The Zebra Matrix Quest");
        }

        [PostgresFact]
        public async Task ScanFolders_CanBeSavedWithOrWithoutAType()
        {
            var admin = await AdminAsync();
            var dir = Path.Combine(Path.GetTempPath(), "chronicle-pg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var response = await admin.PostAsJsonAsync("/api/v1/scan-folders", new { path = dir, mediaTypeId = 0, recursive = true, bundleRelatedFiles = true });

                response.StatusCode.Should().Be(HttpStatusCode.Created);
                (await Json(response)).GetProperty("data").GetProperty("mediaTypeId").ValueKind.Should().Be(JsonValueKind.Null);
            }
            finally { Directory.Delete(dir, true); }
        }

        [PostgresFact]
        public async Task Notifications_AndTheLibraryChangeFeed_Work()
        {
            var admin = await AdminAsync();

            var changes = await admin.GetAsync("/api/v1/library/changes");
            var notices = await admin.GetAsync("/api/v1/notifications");

            changes.StatusCode.Should().Be(HttpStatusCode.OK);
            notices.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [PostgresFact]
        public async Task ThePluginsAndCatalogEndpoints_Work()
        {
            var admin = await AdminAsync();

            (await admin.GetAsync("/api/v1/plugins")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await admin.GetAsync("/api/v1/plugins/catalog/source")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [PostgresFact]
        public async Task TheDatabasePage_SaysBackupsAreNotAvailableHere_InsteadOfFailing()
        {
            var admin = await AdminAsync();

            var response = await admin.GetAsync("/api/v1/database/status");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await Json(response)).GetProperty("data").GetProperty("supported").GetBoolean().Should().BeFalse();
        }
    }
}
