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
    // A game collection is marked in the Library exactly as a movie collection is: the entry's mediaItem says isCollectionContainer, and the
    // page draws the "Collection" badge from that. A game with no series, and an ordinary one-level type without collection support, are not marked.
    public class GameCollectionLibraryTests : IClassFixture<ChronicleApiFactory>
    {
        private readonly ChronicleApiFactory _factory;

        public GameCollectionLibraryTests(ChronicleApiFactory factory)
        {
            factory.SeedDatabase();
            _factory = factory;
        }

        private async Task<(HttpClient Client, int UserId)> AuthedAsync()
        {
            var client = _factory.CreateClient();
            var username = $"gamecoll_{Guid.NewGuid():N}";
            var reg = await client.PostAsJsonAsync("/api/v1/auth/register", new { username, password = "Password123!" });
            var token = JsonDocument.Parse(await reg.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("token").GetString()!;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            return (client, (await db.Users.SingleAsync(u => u.Username == username)).Id);
        }

        [Fact]
        public async Task A_game_collection_with_games_in_it_is_marked_and_a_lone_game_is_not()
        {
            var (client, userId) = await AuthedAsync();
            string collName = $"Series {Guid.NewGuid():N}", loneName = $"Lone {Guid.NewGuid():N}";
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                var type = await db.MediaTypes.FirstOrDefaultAsync(t => t.Name == "game")
                    ?? (await db.MediaTypes.AddAsync(new MediaType
                    {
                        Name = "game", DisplayName = "Video Games", HierarchyLevels = 1, InteractionVerb = "played", ProgressUnit = "percent",
                        IsActive = true, IsTrackable = true, CreatedAt = DateTime.UtcNow,
                    })).Entity;
                type.SupportsCollections = true; type.IsTrackable = true;
                await db.SaveChangesAsync();

                var coll = new MediaItem { MediaTypeId = type.Id, Name = collName, HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
                var lone = new MediaItem { MediaTypeId = type.Id, Name = loneName, HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
                db.MediaItems.AddRange(coll, lone);
                await db.SaveChangesAsync();
                var member = new MediaItem { MediaTypeId = type.Id, Name = collName + " 2", HierarchyLevel = 1, ParentId = coll.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
                db.MediaItems.Add(member);
                await db.SaveChangesAsync();
                db.UserLibraries.AddRange(
                    new UserLibrary { UserId = userId, MediaItemId = coll.Id, Status = LibraryStatus.Watching, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
                    new UserLibrary { UserId = userId, MediaItemId = lone.Id, Status = LibraryStatus.Unwatched, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }

            var resp = await client.GetAsync("/api/v1/library?rootOnly=true&perPage=200");
            resp.EnsureSuccessStatusCode();
            var rows = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("data").EnumerateArray().ToList();
            JsonElement Row(string name) => rows.Single(r => r.GetProperty("mediaItem").GetProperty("name").GetString() == name).GetProperty("mediaItem");

            Row(collName).GetProperty("isCollectionContainer").GetBoolean().Should().BeTrue();
            Row(loneName).GetProperty("isCollectionContainer").GetBoolean().Should().BeFalse();
        }
    }
}
