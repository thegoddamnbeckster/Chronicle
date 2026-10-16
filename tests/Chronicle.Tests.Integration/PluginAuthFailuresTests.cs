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
    /// <summary>GET /plugins/auth-failures lists exactly the installed plugins that have an AuthFailed enrichment row.</summary>
    public class PluginAuthFailuresTests : IClassFixture<ChronicleApiFactory>
    {
        private readonly ChronicleApiFactory _factory;

        public PluginAuthFailuresTests(ChronicleApiFactory factory)
        {
            factory.SeedDatabase();
            _factory = factory;
        }

        private async Task<HttpClient> UserClientAsync()
        {
            var client = _factory.CreateClient();
            var reg = await client.PostAsJsonAsync("/api/v1/auth/register", new { username = $"af_{Guid.NewGuid():N}", password = "Password123!" });
            var token = JsonDocument.Parse(await reg.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("token").GetString();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }

        [Fact]
        public async Task ListsOnlyPluginsWithAnAuthFailedRow()
        {
            int itemId;
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                foreach (var id in new[] { "chronicle.plugin.needs-key", "chronicle.plugin.fine" })
                    db.Plugins.Add(new Plugin { PluginId = id, Name = id, Version = "1.0", Author = "t", DllPath = "x.dll", InstalledAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
                var item = new MediaItem { Name = "x", MediaTypeId = (await db.MediaTypes.FirstAsync()).Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
                db.MediaItems.Add(item);
                await db.SaveChangesAsync();
                itemId = item.Id;
                db.MediaEnrichments.Add(new MediaItemEnrichment { MediaItemId = itemId, PluginId = "chronicle.plugin.needs-key", Status = EnrichmentStatus.AuthFailed });
                db.MediaEnrichments.Add(new MediaItemEnrichment { MediaItemId = itemId, PluginId = "chronicle.plugin.fine", Status = EnrichmentStatus.Completed });
                await db.SaveChangesAsync();
            }

            var response = await (await UserClientAsync()).GetAsync("/api/v1/plugins/auth-failures");
            response.EnsureSuccessStatusCode();
            var ids = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("data")
                .EnumerateArray().Select(e => e.GetProperty("pluginId").GetString()).ToList();

            ids.Should().Contain("chronicle.plugin.needs-key");
            ids.Should().NotContain("chronicle.plugin.fine");
        }
    }
}
