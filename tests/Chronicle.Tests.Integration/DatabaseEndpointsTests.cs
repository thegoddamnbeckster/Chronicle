using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Chronicle.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Tests.Integration
{
    /// <summary>The Database settings API on a real SQLite file with the real migrations.</summary>
    public class DatabaseEndpointsTests : IClassFixture<SqliteApiFactory>
    {
        private const string Password = "Password123!";
        private readonly SqliteApiFactory _factory;

        public DatabaseEndpointsTests(SqliteApiFactory factory) => _factory = factory;

        // ── helpers ───────────────────────────────────────────────────────────

        private static async Task<JsonElement> Json(HttpResponseMessage r) =>
            JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

        private async Task<(HttpClient Client, int UserId, string Key)> SignInAsync(bool admin)
        {
            var anon = _factory.CreateClient();
            var response = await anon.PostAsJsonAsync("/api/v1/auth/register", new { username = $"db_{Guid.NewGuid():N}", password = Password });
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var data = (await Json(response)).GetProperty("data");
            var id = data.GetProperty("user").GetProperty("id").GetInt32();
            var key = data.GetProperty("token").GetString()!;

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                var u = await db.Users.FirstAsync(x => x.Id == id);
                u.IsAdmin = admin;
                await db.SaveChangesAsync();
            }
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return (client, id, key);
        }

        private async Task<HttpClient> AdminAsync() => (await SignInAsync(admin: true)).Client;

        private static async Task<string> BackupNameAsync(HttpClient admin)
        {
            var response = await admin.PostAsync("/api/v1/database/backups", null);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return (await Json(response)).GetProperty("data").GetProperty("fileName").GetString()!;
        }

        // ══ access ════════════════════════════════════════════════════════════

        [Theory]
        [InlineData("GET", "/api/v1/database/status")]
        [InlineData("GET", "/api/v1/database/backups")]
        [InlineData("POST", "/api/v1/database/backups")]
        [InlineData("POST", "/api/v1/database/backups/upload")]
        [InlineData("GET", "/api/v1/database/backups/x.zip/download")]
        [InlineData("DELETE", "/api/v1/database/backups/x.zip")]
        [InlineData("POST", "/api/v1/database/backups/x.zip/restore")]
        [InlineData("POST", "/api/v1/database/maintenance/quick")]
        [InlineData("PUT", "/api/v1/database/settings")]
        public async Task EveryRoute_RejectsAnonymousCallers(string method, string url)
        {
            var response = await _factory.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), url));

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task OrdinaryUsers_AreForbidden_AndCannotDownloadTheDatabase()
        {
            var admin = await AdminAsync();
            var name = await BackupNameAsync(admin);
            var (user, _, _) = await SignInAsync(admin: false);

            (await user.GetAsync("/api/v1/database/status")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await user.GetAsync($"/api/v1/database/backups/{name}/download")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await user.PostAsync("/api/v1/database/backups", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [Fact]
        public async Task AnApiKey_EvenAFullOneOwnedByAnAdmin_CannotTouchTheDatabase()
        {
            var (admin, _, _) = await SignInAsync(admin: true);
            var minted = await admin.PostAsJsonAsync("/api/v1/tokens", new { name = "script", scope = "full" });
            var raw = (await Json(minted)).GetProperty("data").GetProperty("token").GetString()!;
            var byKey = _factory.CreateClient();
            byKey.DefaultRequestHeaders.Add("X-API-Key", raw);

            (await byKey.GetAsync("/api/v1/database/status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await byKey.PostAsync("/api/v1/database/backups", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // ══ status ════════════════════════════════════════════════════════════

        [Fact]
        public async Task Status_DescribesTheRealDatabase()
        {
            var admin = await AdminAsync();

            var data = (await Json(await admin.GetAsync("/api/v1/database/status"))).GetProperty("data");

            data.GetProperty("supported").GetBoolean().Should().BeTrue();
            data.GetProperty("databaseBytes").GetInt64().Should().BeGreaterThan(0);
            data.GetProperty("databaseFile").GetString().Should().Be(_factory.DbFile);
            data.GetProperty("latestMigration").GetString().Should().NotBeNullOrEmpty();
            data.GetProperty("backupDirectory").GetString().Should().Be(_factory.BackupDir);
        }

        // ══ backups ═══════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateListDownloadDelete_RoundTrip()
        {
            var admin = await AdminAsync();

            var name = await BackupNameAsync(admin);
            var list = (await Json(await admin.GetAsync("/api/v1/database/backups"))).GetProperty("data").EnumerateArray().ToList();
            list.Should().Contain(b => b.GetProperty("fileName").GetString() == name);

            var download = await admin.GetAsync($"/api/v1/database/backups/{name}/download");
            download.StatusCode.Should().Be(HttpStatusCode.OK);
            download.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
            using (var zip = new ZipArchive(await download.Content.ReadAsStreamAsync()))
                zip.Entries.Select(e => e.Name).Should().BeEquivalentTo(["chronicle.db", "manifest.json"]);

            (await admin.DeleteAsync($"/api/v1/database/backups/{name}")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await admin.GetAsync($"/api/v1/database/backups/{name}/download")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Theory]
        [InlineData("..%2Fchronicle.db")]
        [InlineData("..%5Cchronicle.db")]
        [InlineData("%2Fetc%2Fpasswd")]
        [InlineData("missing.zip")]
        [InlineData("chronicle.db")]
        public async Task Download_RefusesUnsafeOrUnknownNames(string name)
        {
            var admin = await AdminAsync();

            var response = await admin.GetAsync($"/api/v1/database/backups/{name}/download");

            response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.BadRequest);
            (await response.Content.ReadAsByteArrayAsync()).Length.Should().BeLessThan(2000, "no file content was returned");
        }

        [Fact]
        public async Task Upload_RejectsGarbageWith422_AndReportsWhy()
        {
            var admin = await AdminAsync();

            var response = await admin.PostAsync("/api/v1/database/backups/upload", new ByteArrayContent(new byte[4000]));

            response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await Json(response)).GetProperty("error").GetProperty("code").GetString().Should().Be("INVALID_BACKUP");
            Directory.EnumerateFiles(_factory.BackupDir).Should().NotContain(f => Path.GetFileName(f).StartsWith("uploaded-"));
        }

        [Fact]
        public async Task Upload_AcceptsARealBackup_AndItAppearsAsUploaded()
        {
            var admin = await AdminAsync();
            var name = await BackupNameAsync(admin);
            var bytes = await (await admin.GetAsync($"/api/v1/database/backups/{name}/download")).Content.ReadAsByteArrayAsync();

            var response = await admin.PostAsync("/api/v1/database/backups/upload", new ByteArrayContent(bytes));

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await Json(response)).GetProperty("data").GetProperty("kind").GetString().Should().Be("uploaded");
        }

        [Fact]
        public async Task Validate_ReportsGoodAndBad()
        {
            var admin = await AdminAsync();
            var name = await BackupNameAsync(admin);

            var good = (await Json(await admin.PostAsync($"/api/v1/database/backups/{name}/validate", null))).GetProperty("data");
            good.GetProperty("valid").GetBoolean().Should().BeTrue();

            (await admin.PostAsync("/api/v1/database/backups/nope.zip/validate", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        // ══ restore ═══════════════════════════════════════════════════════════

        [Fact]
        public async Task Restore_WithoutTheConfirmationWord_DoesNothing()
        {
            var admin = await AdminAsync();
            var name = await BackupNameAsync(admin);
            var restartsBefore = _factory.Restart.Requests.Count;

            foreach (var confirm in new[] { null, "", "restore", "yes", "RESTORE " })
            {
                var response = await admin.PostAsJsonAsync($"/api/v1/database/backups/{name}/restore", new { confirm });
                response.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"'{confirm}' is not the confirmation");
            }

            _factory.Restart.Requests.Count.Should().Be(restartsBefore);
            File.Exists(_factory.DbFile + ".restore-pending").Should().BeFalse();
        }

        [Fact]
        public async Task Restore_WithTheConfirmation_StagesIt_AndAsksForARestart()
        {
            var admin = await AdminAsync();
            var name = await BackupNameAsync(admin);
            var restartsBefore = _factory.Restart.Requests.Count;

            try
            {
                var response = await admin.PostAsJsonAsync($"/api/v1/database/backups/{name}/restore", new { confirm = "RESTORE" });

                response.StatusCode.Should().Be(HttpStatusCode.OK);
                (await Json(response)).GetProperty("data").GetProperty("restarting").GetBoolean().Should().BeTrue();
                _factory.Restart.Requests.Count.Should().Be(restartsBefore + 1);
                _factory.Restart.Requests.Last().Should().Contain(name);
                File.Exists(_factory.DbFile + ".restore-pending").Should().BeTrue();
                var list = (await Json(await admin.GetAsync("/api/v1/database/backups"))).GetProperty("data").EnumerateArray();
                list.Should().Contain(b => b.GetProperty("kind").GetString() == "pre-restore", "the current data was backed up first");
            }
            finally
            {
                // The test host never restarts, so remove what was staged rather than leave it for another test.
                File.Delete(_factory.DbFile + ".restore-pending");
            }
        }

        [Fact]
        public async Task Restore_OfAnUnknownBackup_IsA404_AndStagesNothing()
        {
            var admin = await AdminAsync();

            var response = await admin.PostAsJsonAsync("/api/v1/database/backups/nope.zip/restore", new { confirm = "RESTORE" });

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            File.Exists(_factory.DbFile + ".restore-pending").Should().BeFalse();
        }

        // ══ maintenance + settings ════════════════════════════════════════════

        [Fact]
        public async Task QuickMaintenance_Runs_AndAnUnknownKindIsRefused()
        {
            var admin = await AdminAsync();

            (await admin.PostAsync("/api/v1/database/maintenance/quick", null)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await admin.PostAsync("/api/v1/database/maintenance/everything", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task Settings_AreValidated_AndPersisted()
        {
            var admin = await AdminAsync();

            (await admin.PutAsJsonAsync("/api/v1/database/settings", new { backupsToKeep = 0 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await admin.PutAsJsonAsync("/api/v1/database/settings", new { backupsToKeep = 9999 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await admin.PutAsJsonAsync("/api/v1/database/settings", new { warnSizeMb = -5 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var ok = await admin.PutAsJsonAsync("/api/v1/database/settings", new { backupsToKeep = 4, warnSizeMb = 2048 });
            ok.StatusCode.Should().Be(HttpStatusCode.OK);
            var data = (await Json(ok)).GetProperty("data");
            data.GetProperty("retainCount").GetInt32().Should().Be(4);
            data.GetProperty("warnSizeBytes").GetInt64().Should().Be(2048L * 1024 * 1024);

            // Put the defaults back for the other tests sharing this database.
            await admin.PutAsJsonAsync("/api/v1/database/settings", new { backupsToKeep = 10, warnSizeMb = 5120 });
        }

        // ══ the scheduled tasks are registered ════════════════════════════════

        [Fact]
        public async Task TheDatabaseTasks_AppearOnTheBackgroundTasksPage()
        {
            var admin = await AdminAsync();

            var tasks = (await Json(await admin.GetAsync("/api/v1/background-tasks"))).GetProperty("data").EnumerateArray()
                .Select(t => t.GetProperty("taskId").GetString()).ToList();

            tasks.Should().Contain(["database_backup", "database_maintenance_light", "database_maintenance_full"]);
        }
    }

    /// <summary>What the in-memory provider answers: honest "not supported", not a crash.</summary>
    public class DatabaseEndpointsOnInMemoryTests : IClassFixture<ChronicleApiFactory>
    {
        private readonly ChronicleApiFactory _factory;
        public DatabaseEndpointsOnInMemoryTests(ChronicleApiFactory factory) { factory.SeedDatabase(); _factory = factory; }

        [Fact]
        public async Task Status_SaysItIsNotSupported_AndBackupIsA400()
        {
            var anon = _factory.CreateClient();
            var reg = await anon.PostAsJsonAsync("/api/v1/auth/register", new { username = $"im_{Guid.NewGuid():N}", password = "Password123!" });
            var data = JsonDocument.Parse(await reg.Content.ReadAsStringAsync()).RootElement.GetProperty("data");
            var id = data.GetProperty("user").GetProperty("id").GetInt32();
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                (await db.Users.FirstAsync(u => u.Id == id)).IsAdmin = true;
                await db.SaveChangesAsync();
            }
            var admin = _factory.CreateClient();
            admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", data.GetProperty("token").GetString());

            var status = JsonDocument.Parse(await (await admin.GetAsync("/api/v1/database/status")).Content.ReadAsStringAsync()).RootElement.GetProperty("data");
            status.GetProperty("supported").GetBoolean().Should().BeFalse();

            (await admin.PostAsync("/api/v1/database/backups", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }
}
