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
    /// <summary>One file that holds two episodes: Kodi lists both and asks the scraper about each.</summary>
    public class ScraperMultiEpisodeTests : IClassFixture<ChronicleApiFactory>
    {
        private const string Password = "Password123!";
        private const string DoubleFile = "Star Trek - Enterprise - S01E01-E02 - Broken Bow.mkv";
        private readonly ChronicleApiFactory _factory;

        public ScraperMultiEpisodeTests(ChronicleApiFactory factory) { factory.SeedDatabase(); _factory = factory; }

        private static async Task<JsonElement> Json(HttpResponseMessage r) =>
            JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

        private async Task<HttpClient> SignInAsync()
        {
            var reg = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { username = $"me_{Guid.NewGuid():N}", password = Password });
            var data = (await Json(reg)).GetProperty("data");
            var c = _factory.CreateClient();
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", data.GetProperty("token").GetString()!);
            return c;
        }

        /// <summary>A show with a season holding episodes 1 and 2; the double file is recorded on episode 1 only.</summary>
        private async Task<(int First, int Second)> SeedAsync(string fileName = DoubleFile, string show = "Enterprise")
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var tv = await db.MediaTypes.FirstAsync(t => t.Name == "tv");
            var showItem = new MediaItem { Name = show, MediaTypeId = tv.Id, HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.MediaItems.Add(showItem);
            await db.SaveChangesAsync();
            var season = new MediaItem { Name = "Season 1", MediaTypeId = tv.Id, HierarchyLevel = 1, ParentId = showItem.Id, Number = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.MediaItems.Add(season);
            await db.SaveChangesAsync();
            var one = new MediaItem
            {
                Name = "Broken Bow (1)", MediaTypeId = tv.Id, HierarchyLevel = 2, ParentId = season.Id, Number = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                MetadataJson = JsonSerializer.Serialize(new { fileScanner = new { filePaths = new[] { $"D:\\TV\\Enterprise\\Season 01\\{fileName}" } } }),
            };
            var two = new MediaItem { Name = "Broken Bow (2)", MediaTypeId = tv.Id, HierarchyLevel = 2, ParentId = season.Id, Number = 2, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.MediaItems.AddRange(one, two);
            await db.SaveChangesAsync();
            db.MediaItemKnownFileNames.Add(new MediaItemKnownFileName { MediaItemId = one.Id, FileName = fileName });
            await db.SaveChangesAsync();
            return (one.Id, two.Id);
        }

        [Fact]
        public async Task BothEpisodesOfADoubleFile_ResolveToTheirOwnItem()
        {
            var (first, second) = await SeedAsync();
            var client = await SignInAsync();
            var url = $"/api/v1/scraper/tv/episode-details-by-file?fileName={Uri.EscapeDataString(DoubleFile)}&season=1";

            var ep1 = await client.GetAsync(url + "&episode=1");
            var ep2 = await client.GetAsync(url + "&episode=2");

            ep1.StatusCode.Should().Be(HttpStatusCode.OK);
            (await Json(ep1)).GetProperty("data").GetProperty("episode").GetInt32().Should().Be(1);
            ep2.StatusCode.Should().Be(HttpStatusCode.OK);
            (await Json(ep2)).GetProperty("data").GetProperty("episode").GetInt32().Should().Be(2);
            _ = (first, second);
        }

        [Fact]
        public async Task AnEpisodeTheFileDoesNotCover_IsStillRefused()
        {
            await SeedAsync(fileName: "Star Trek - Enterprise - S01E01-E02 - Broken Bow (copy).mkv", show: "Enterprise Copy");
            var client = await SignInAsync();

            var response = await client.GetAsync($"/api/v1/scraper/tv/episode-details-by-file?fileName={Uri.EscapeDataString("Star Trek - Enterprise - S01E01-E02 - Broken Bow (copy).mkv")}&season=1&episode=5");

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task ASingleEpisodeFile_WithAContradictingNumber_IsStillTreatedAsStale()
        {
            const string single = "Show - S01E01 - Pilot.mkv";
            await SeedAsync(fileName: single, show: "Single Show");
            var client = await SignInAsync();

            var response = await client.GetAsync($"/api/v1/scraper/tv/episode-details-by-file?fileName={Uri.EscapeDataString(single)}&season=1&episode=2");

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }
}
