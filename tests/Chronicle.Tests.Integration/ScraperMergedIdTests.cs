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
    // A Kodi library keeps the Chronicle id it was first given and asks for it on every refresh. A merge deletes the losing item, so
    // the scraper endpoints must follow the merge record to the item that replaced it instead of answering 404 for good.
    public class ScraperMergedIdTests : IClassFixture<ChronicleApiFactory>
    {
        private readonly ChronicleApiFactory _factory;

        public ScraperMergedIdTests(ChronicleApiFactory factory)
        {
            factory.SeedDatabase();
            _factory = factory;
        }

        private async Task<HttpClient> AuthedClientAsync()
        {
            var client = _factory.CreateClient();
            var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
                new { username = $"mergedid_{Guid.NewGuid():N}", password = "Password123!" });
            var token = JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("token").GetString()!;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }

        private static MediaItemMerge Merge(int loser, int winner) => new()
        {
            WinnerId = winner, LoserOriginalId = loser, LoserName = "Merged away", LoserMediaTypeId = 1,
            LoserHierarchyLevel = 0, MergedAt = DateTime.UtcNow,
        };

        [Fact]
        public async Task AMergedAwayShowId_AnswersWithTheShowThatReplacedIt_AlsoThroughAChain_AndAnUnknownIdStays404()
        {
            int showId, episodeId;
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
                MediaItem Item(string name, int level, int? parent, int? number = null) => new()
                {
                    MediaTypeId = tv.Id, Name = name, HierarchyLevel = level, ParentId = parent, Number = number,
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                };
                var show = Item("Merge Target Show", 0, null);
                db.MediaItems.Add(show);
                await db.SaveChangesAsync();
                var season = Item("Season 1", 1, show.Id, 1);
                db.MediaItems.Add(season);
                await db.SaveChangesAsync();
                var episode = Item("Pilot", 2, season.Id, 1);
                db.MediaItems.Add(episode);
                await db.SaveChangesAsync();
                // 880001 was merged into 880002, which was later merged into the surviving show; neither row exists any more.
                db.MediaItemMerges.AddRange(Merge(880002, show.Id), Merge(880001, 880002));
                await db.SaveChangesAsync();
                (showId, episodeId) = (show.Id, episode.Id);
            }
            var client = await AuthedClientAsync();

            var details = await client.GetAsync("/api/v1/scraper/tv/details?id=880001");
            details.StatusCode.Should().Be(HttpStatusCode.OK);
            JsonDocument.Parse(await details.Content.ReadAsStringAsync()).RootElement
                .GetProperty("data").GetProperty("title").GetString().Should().Be("Merge Target Show");

            var episodes = await client.GetAsync("/api/v1/scraper/tv/episodes?showId=880002");
            episodes.StatusCode.Should().Be(HttpStatusCode.OK);
            JsonDocument.Parse(await episodes.Content.ReadAsStringAsync()).RootElement
                .GetProperty("data").GetArrayLength().Should().Be(1);

            (await client.GetAsync($"/api/v1/scraper/tv/details?id={showId}")).StatusCode.Should().Be(HttpStatusCode.OK);   // a live id is untouched
            (await client.GetAsync("/api/v1/scraper/tv/details?id=880099")).StatusCode.Should().Be(HttpStatusCode.NotFound); // no merge record: still unknown
            (await client.GetAsync("/api/v1/scraper/tv/episode-details?id=880099")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            episodeId.Should().BeGreaterThan(0);
        }
    }
}
