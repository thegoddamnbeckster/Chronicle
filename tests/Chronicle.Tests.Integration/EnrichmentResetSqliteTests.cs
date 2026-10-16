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
    /// <summary>Resetting a plugin's enrichment rows is one UPDATE on a real database (not a load of every row), and puts back exactly the rows asked for.</summary>
    public class EnrichmentResetSqliteTests : IClassFixture<SqliteApiFactory>
    {
        private const string Password = "Password123!";
        private const string Plugin = "chronicle.plugin.resettest";
        private readonly SqliteApiFactory _factory;

        public EnrichmentResetSqliteTests(SqliteApiFactory factory) => _factory = factory;

        private static async Task<JsonElement> Json(HttpResponseMessage r) =>
            JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

        private async Task<HttpClient> AdminAsync()
        {
            var response = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { username = $"rs_{Guid.NewGuid():N}", password = Password });
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

        [Fact]
        public async Task ResettingFailedRows_PutsOnlyThoseBackToPending_AndClearsTheirErrors()
        {
            var admin = await AdminAsync();
            int failedId, completedId;
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                var type = await db.MediaTypes.FirstAsync();
                var a = new MediaItem { Name = "A", MediaTypeId = type.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
                var b = new MediaItem { Name = "B", MediaTypeId = type.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
                db.MediaItems.AddRange(a, b);
                await db.SaveChangesAsync();
                failedId = a.Id;
                completedId = b.Id;
                db.MediaEnrichments.Add(new MediaItemEnrichment { MediaItemId = a.Id, PluginId = Plugin, Status = EnrichmentStatus.Failed, RetryCount = 3, ErrorMessage = "boom", LastAttemptedAt = DateTime.UtcNow });
                db.MediaEnrichments.Add(new MediaItemEnrichment { MediaItemId = b.Id, PluginId = Plugin, Status = EnrichmentStatus.Completed, RetryCount = 1 });
                await db.SaveChangesAsync();
            }

            var response = await admin.PostAsJsonAsync($"/api/v1/enrichment/{Plugin}/reset", new { scope = "failed" });
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var check = _factory.Services.CreateScope();
            var rows = await check.ServiceProvider.GetRequiredService<ChronicleDbContext>().MediaEnrichments
                .AsNoTracking().Where(e => e.PluginId == Plugin).ToListAsync();
            var failed = rows.Single(r => r.MediaItemId == failedId);
            failed.Status.Should().Be(EnrichmentStatus.Pending);
            failed.RetryCount.Should().Be(0);
            failed.ErrorMessage.Should().BeNull();
            failed.LastAttemptedAt.Should().BeNull();
            rows.Single(r => r.MediaItemId == completedId).Status.Should().Be(EnrichmentStatus.Completed);
        }
    }
}
