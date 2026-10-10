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
    /// <summary>An automatic-detect scan imports each group as the media type it was sorted into.</summary>
    public class AutoDetectImportTests : IClassFixture<ChronicleApiFactory>
    {
        private const string Password = "Password123!";
        private readonly ChronicleApiFactory _factory;

        public AutoDetectImportTests(ChronicleApiFactory factory) { factory.SeedDatabase(); _factory = factory; }

        private static async Task<JsonElement> Json(HttpResponseMessage r) =>
            JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

        private async Task<HttpClient> SignInAsync()
        {
            var reg = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { username = $"ad_{Guid.NewGuid():N}", password = Password });
            var data = (await Json(reg)).GetProperty("data");
            var c = _factory.CreateClient();
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", data.GetProperty("token").GetString()!);
            return c;
        }

        private async Task<JsonElement> WaitForImportAsync(HttpClient client)
        {
            for (var i = 0; i < 100; i++)
            {
                var state = (await Json(await client.GetAsync("/api/v1/scan/import-progress"))).GetProperty("data");
                if (state.GetProperty("isComplete").GetBoolean()) return state;
                await Task.Delay(100);
            }
            throw new TimeoutException("The import did not finish.");
        }

        private static object Group(string name, int? typeId, string file) => new
        {
            name, year = (int?)null, number = (int?)null, posterPath = (string?)null, children = Array.Empty<object>(),
            files = new[] { file }, folderPath = (string?)null, relatedFiles = Array.Empty<string>(), mediaTypeId = typeId,
        };

        [Fact]
        public async Task GroupsOfSeveralTypes_AreEachImportedAsTheirOwnType_UnderOneProgressRun()
        {
            var client = await SignInAsync();
            int movies, music;
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                movies = (await db.MediaTypes.FirstAsync(t => t.Name == "movies")).Id;
                music = (await db.MediaTypes.FirstAsync(t => t.Name == "music")).Id;
            }
            var unique = Guid.NewGuid().ToString("N")[..8];

            var response = await client.PostAsJsonAsync("/api/v1/scan/import-groups", new
            {
                groups = new[]
                {
                    Group($"Auto Movie {unique}", movies, $"C:/auto/{unique}/movie.mkv"),
                    Group($"Auto Artist {unique}", music, $"C:/auto/{unique}/song.mp3"),
                },
                mediaTypeId = 0,
            });
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
            var state = await WaitForImportAsync(client);

            state.GetProperty("error").ValueKind.Should().Be(JsonValueKind.Null);
            state.GetProperty("result").GetProperty("imported").GetInt32().Should().Be(2);
            using var verify = _factory.Services.CreateScope();
            var check = verify.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            (await check.MediaItems.SingleAsync(m => m.Name == $"Auto Movie {unique}")).MediaTypeId.Should().Be(movies);
            (await check.MediaItems.SingleAsync(m => m.Name == $"Auto Artist {unique}")).MediaTypeId.Should().Be(music);
        }

        [Fact]
        public async Task AGroupWithoutAType_UsesTheRequestsType_AsBefore()
        {
            var client = await SignInAsync();
            int movies;
            using (var scope = _factory.Services.CreateScope())
                movies = (await scope.ServiceProvider.GetRequiredService<ChronicleDbContext>().MediaTypes.FirstAsync(t => t.Name == "movies")).Id;
            var unique = Guid.NewGuid().ToString("N")[..8];

            var response = await client.PostAsJsonAsync("/api/v1/scan/import-groups", new
            {
                groups = new[] { Group($"Plain Movie {unique}", null, $"C:/auto/{unique}/plain.mkv") },
                mediaTypeId = movies,
            });
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
            await WaitForImportAsync(client);

            using var verify = _factory.Services.CreateScope();
            (await verify.ServiceProvider.GetRequiredService<ChronicleDbContext>().MediaItems
                .SingleAsync(m => m.Name == $"Plain Movie {unique}")).MediaTypeId.Should().Be(movies);
        }
    }
}
