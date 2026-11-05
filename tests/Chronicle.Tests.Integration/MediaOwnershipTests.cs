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
    // MediaController.GetOwnership (GET /media/{id}/ownership): what the SIGNED-IN user has of a title on each platform. The page shows
    // "Steam: 56 hours, last played ..." from it; the platform's whole raw record stays in the database and is never sent.
    public class MediaOwnershipTests : IClassFixture<ChronicleApiFactory>
    {
        private readonly ChronicleApiFactory _factory;

        public MediaOwnershipTests(ChronicleApiFactory factory)
        {
            factory.SeedDatabase();
            _factory = factory;
        }

        private async Task<(HttpClient Client, string Username)> AuthedClientAsync()
        {
            var client = _factory.CreateClient();
            var username = $"ownership_{Guid.NewGuid():N}";
            var reg = await client.PostAsJsonAsync("/api/v1/auth/register", new { username, password = "Password123!" });
            var token = JsonDocument.Parse(await reg.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("token").GetString()!;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return (client, username);
        }

        [Fact]
        public async Task The_users_own_rows_come_back_summarised_and_another_users_do_not()
        {
            var (client, username) = await AuthedClientAsync();
            var (other, _) = await AuthedClientAsync();
            int itemId;
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                var type = await db.MediaTypes.FirstOrDefaultAsync(t => t.Name == "game")
                    ?? (await db.MediaTypes.AddAsync(new MediaType
                    {
                        Name = "game", DisplayName = "Video Games", HierarchyLevels = 1, InteractionVerb = "played", ProgressUnit = "percent",
                        IsActive = true, CreatedAt = DateTime.UtcNow,
                    })).Entity;
                await db.SaveChangesAsync();
                var item = new MediaItem { MediaTypeId = type.Id, Name = "Test Game", HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
                db.MediaItems.Add(item);
                await db.SaveChangesAsync();
                itemId = item.Id;
                var userId = (await db.Users.SingleAsync(u => u.Username == username)).Id;
                db.UserGameOwnerships.AddRange(
                    new UserGameOwnership
                    {
                        UserId = userId, MediaItemId = itemId, Source = "steam", SourceId = "10", Platform = "PC", PlaytimeMinutes = 90,
                        LastPlayedAt = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc), TagsJson = "[\"dlc\"]",
                        RawJson = "{\"big\":\"record that must not be sent\"}", FirstSeenAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                    },
                    new UserGameOwnership
                    {
                        UserId = userId, MediaItemId = itemId, Source = "psn", SourceId = "CUSA1", Platform = "PS5", IsWishlist = true,
                        FirstSeenAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                    });
                await db.SaveChangesAsync();
            }

            var resp = await client.GetAsync($"/api/v1/media/{itemId}/ownership");
            resp.EnsureSuccessStatusCode();
            var text = await resp.Content.ReadAsStringAsync();
            var rows = JsonDocument.Parse(text).RootElement.GetProperty("data").EnumerateArray().ToList();

            rows.Select(r => r.GetProperty("source").GetString()).Should().Equal("psn", "steam");   // by source
            var steam = rows.Single(r => r.GetProperty("source").GetString() == "steam");
            steam.GetProperty("playtimeMinutes").GetInt32().Should().Be(90);
            steam.GetProperty("platform").GetString().Should().Be("PC");
            steam.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).Should().Equal("dlc");
            rows.Single(r => r.GetProperty("source").GetString() == "psn").GetProperty("isWishlist").GetBoolean().Should().BeTrue();
            text.Should().NotContain("must not be sent");                                              // the raw record stays in the database

            var theirs = await other.GetAsync($"/api/v1/media/{itemId}/ownership");
            JsonDocument.Parse(await theirs.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetArrayLength().Should().Be(0);
        }

        [Fact]
        public async Task An_item_no_library_import_touched_has_no_rows_and_a_signed_out_caller_is_refused()
        {
            var (client, _) = await AuthedClientAsync();
            var resp = await client.GetAsync("/api/v1/media/999999/ownership");
            resp.EnsureSuccessStatusCode();
            JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetArrayLength().Should().Be(0);

            var anon = _factory.CreateClient();
            (await anon.GetAsync("/api/v1/media/1/ownership")).StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
        }
    }
}
