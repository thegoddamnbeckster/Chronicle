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
    // GET /media/{id}/children reports, for each child that holds others (a season), how many episodes sit beneath it and how
    // many the signed-in user has finished, so the show page can draw a season's watched progress.
    public class MediaChildrenProgressTests : IClassFixture<ChronicleApiFactory>
    {
        private readonly ChronicleApiFactory _factory;

        public MediaChildrenProgressTests(ChronicleApiFactory factory)
        {
            factory.SeedDatabase();
            _factory = factory;
        }

        [Fact]
        public async Task Children_OfAShow_CarryEachSeasonsWatchedEpisodeCount_ForTheCallingUserOnly()
        {
            var client = _factory.CreateClient();
            var username = $"seasonprogress_{Guid.NewGuid():N}";
            var reg = await client.PostAsJsonAsync("/api/v1/auth/register", new { username, password = "Password123!" });
            var token = JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("token").GetString()!;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            int showId, season1Id, season2Id;
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                var tv = await db.MediaTypes.FirstOrDefaultAsync(t => t.Name == "tv")
                    ?? (await db.MediaTypes.AddAsync(new MediaType
                    {
                        Name = "tv", DisplayName = "TV Shows", HierarchyLevels = 3, InteractionVerb = "watched",
                        ProgressUnit = "minutes", IsBuiltIn = true, IsActive = true, CreatedAt = DateTime.UtcNow,
                    })).Entity;
                await db.SaveChangesAsync();
                var user = await db.Users.SingleAsync(u => u.Username == username);
                var otherUser = new User { Username = $"other_{Guid.NewGuid():N}", Email = $"{Guid.NewGuid():N}@x.test", PasswordHash = "x" };
                db.Users.Add(otherUser);

                MediaItem Item(string name, int level, int? parent, int? number = null) => new()
                {
                    MediaTypeId = tv.Id, Name = name, HierarchyLevel = level, ParentId = parent, Number = number,
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                };
                var show = Item("Progress Show", 0, null);
                db.MediaItems.Add(show);
                await db.SaveChangesAsync();
                var s1 = Item("Season 1", 1, show.Id, 1);
                var s2 = Item("Season 2", 1, show.Id, 2);
                db.MediaItems.AddRange(s1, s2);
                await db.SaveChangesAsync();
                var eps = new[]
                {
                    Item("S1E1", 2, s1.Id, 1), Item("S1E2", 2, s1.Id, 2), Item("S1E3", 2, s1.Id, 3),
                    Item("S2E1", 2, s2.Id, 1), Item("S2E2", 2, s2.Id, 2),
                };
                db.MediaItems.AddRange(eps);
                await db.SaveChangesAsync();

                void Entry(int userId, MediaItem m, LibraryStatus status) => db.UserLibraries.Add(new UserLibrary
                {
                    UserId = userId, MediaItemId = m.Id, Status = status, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                });
                Entry(user.Id, eps[0], LibraryStatus.Completed);
                Entry(user.Id, eps[1], LibraryStatus.Completed);
                Entry(user.Id, eps[2], LibraryStatus.Watching);        // part-way is not finished
                Entry(user.Id, eps[3], LibraryStatus.Unwatched);
                Entry(otherUser.Id, eps[3], LibraryStatus.Completed);  // someone else's watch never counts
                Entry(otherUser.Id, eps[4], LibraryStatus.Completed);
                await db.SaveChangesAsync();
                (showId, season1Id, season2Id) = (show.Id, s1.Id, s2.Id);
            }

            var response = await client.GetAsync($"/api/v1/media/{showId}/children");
            response.EnsureSuccessStatusCode();
            var seasons = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("data");

            JsonElement Season(int id) => seasons.EnumerateArray().Single(s => s.GetProperty("id").GetInt32() == id);
            Season(season1Id).GetProperty("leafCount").GetInt32().Should().Be(3);
            Season(season1Id).GetProperty("watchedLeafCount").GetInt32().Should().Be(2);
            Season(season2Id).GetProperty("leafCount").GetInt32().Should().Be(2);
            Season(season2Id).GetProperty("watchedLeafCount").GetInt32().Should().Be(0);
        }
    }
}
