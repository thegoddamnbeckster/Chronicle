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
    /// <summary>Saved scan folders that sort each file into its own type, and per-folder related files, on a real SQLite file
    /// with the real migrations (the migration makes the type optional).</summary>
    public class ScanFolderDetectTests : IClassFixture<SqliteApiFactory>
    {
        private const string Password = "Password123!";
        private readonly SqliteApiFactory _factory;

        public ScanFolderDetectTests(SqliteApiFactory factory) => _factory = factory;

        private static async Task<JsonElement> Json(HttpResponseMessage r) =>
            JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

        private async Task<HttpClient> AdminAsync()
        {
            var response = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { username = $"sf_{Guid.NewGuid():N}", password = Password });
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

        private static string NewFolder()
        {
            var dir = Path.Combine(Path.GetTempPath(), "chronicle-sf-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public async Task AFolderWithTypeZero_IsSavedAsAutomatic_WithNoTypeStored()
        {
            var admin = await AdminAsync();
            var dir = NewFolder();
            try
            {
                var response = await admin.PostAsJsonAsync("/api/v1/scan-folders", new { path = dir, mediaTypeId = 0, recursive = true, bundleRelatedFiles = true });

                response.StatusCode.Should().Be(HttpStatusCode.Created);
                var data = (await Json(response)).GetProperty("data");
                data.GetProperty("mediaTypeId").ValueKind.Should().Be(JsonValueKind.Null);
                data.GetProperty("mediaTypeName").GetString().Should().Be("Detect automatically");
                data.GetProperty("bundleRelatedFiles").GetBoolean().Should().BeTrue();
                using var scope = _factory.Services.CreateScope();
                var row = await scope.ServiceProvider.GetRequiredService<ChronicleDbContext>().ScanFolders.SingleAsync(f => f.Path == dir);
                row.MediaTypeId.Should().BeNull();
                row.BundleRelatedFiles.Should().BeTrue();
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task AFolderWithATypeAndNoRelatedFilesChoice_IsSavedAsBefore_FollowingTheGlobalSetting()
        {
            var admin = await AdminAsync();
            var dir = NewFolder();
            try
            {
                int typeId;
                using (var scope = _factory.Services.CreateScope())
                    typeId = (await scope.ServiceProvider.GetRequiredService<ChronicleDbContext>().MediaTypes.FirstAsync(t => t.Name == "movies")).Id;

                var response = await admin.PostAsJsonAsync("/api/v1/scan-folders", new { path = dir, mediaTypeId = typeId, recursive = true });

                response.StatusCode.Should().Be(HttpStatusCode.Created);
                var data = (await Json(response)).GetProperty("data");
                data.GetProperty("mediaTypeId").GetInt32().Should().Be(typeId);
                data.GetProperty("mediaTypeName").GetString().Should().NotBe("Detect automatically");
                data.GetProperty("bundleRelatedFiles").ValueKind.Should().Be(JsonValueKind.Null);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task EditingAFolder_CanSwitchItBetweenAutomaticAndATypeAndBack()
        {
            var admin = await AdminAsync();
            var dir = NewFolder();
            try
            {
                int typeId;
                using (var scope = _factory.Services.CreateScope())
                    typeId = (await scope.ServiceProvider.GetRequiredService<ChronicleDbContext>().MediaTypes.FirstAsync(t => t.Name == "tv")).Id;
                var created = await Json(await admin.PostAsJsonAsync("/api/v1/scan-folders", new { path = dir, mediaTypeId = typeId, recursive = true }));
                var id = created.GetProperty("data").GetProperty("id").GetInt32();

                var auto = await Json(await admin.PutAsJsonAsync($"/api/v1/scan-folders/{id}", new { path = dir, mediaTypeId = 0, recursive = true, isEnabled = true, bundleRelatedFiles = false }));
                auto.GetProperty("data").GetProperty("mediaTypeId").ValueKind.Should().Be(JsonValueKind.Null);
                auto.GetProperty("data").GetProperty("bundleRelatedFiles").GetBoolean().Should().BeFalse();

                var back = await Json(await admin.PutAsJsonAsync($"/api/v1/scan-folders/{id}", new { path = dir, mediaTypeId = typeId, recursive = true, isEnabled = true }));
                back.GetProperty("data").GetProperty("mediaTypeId").GetInt32().Should().Be(typeId);
                back.GetProperty("data").GetProperty("bundleRelatedFiles").ValueKind.Should().Be(JsonValueKind.Null);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task TheListShowsAutomaticFolders()
        {
            var admin = await AdminAsync();
            var dir = NewFolder();
            try
            {
                await admin.PostAsJsonAsync("/api/v1/scan-folders", new { path = dir, mediaTypeId = (int?)null, recursive = false });

                var list = (await Json(await admin.GetAsync("/api/v1/scan-folders"))).GetProperty("data").EnumerateArray().ToList();

                list.Single(f => f.GetProperty("path").GetString() == dir).GetProperty("mediaTypeName").GetString().Should().Be("Detect automatically");
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
