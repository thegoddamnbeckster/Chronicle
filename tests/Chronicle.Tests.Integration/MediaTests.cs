using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Chronicle.Tests.Integration
{
    public class MediaTests : IClassFixture<ChronicleApiFactory>
    {
        private readonly ChronicleApiFactory _factory;
        private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

        public MediaTests(ChronicleApiFactory factory)
        {
            factory.SeedDatabase();
            _factory = factory;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private async Task<HttpClient> AuthClientAsync()
        {
            var client = _factory.CreateClient();
            var username = $"media_{Guid.NewGuid():N}";

            var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
                new { username, password = "Password123!" });

            var token = JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("token").GetString()!;

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            return client;
        }

        private static async Task<int> CreateMediaItemAsync(HttpClient client, string name = "Test Movie")
        {
            var resp = await client.PostAsJsonAsync("/api/v1/media", new
            {
                mediaTypeId = 1,
                name,
                hierarchyLevel = 0,
                runtimeMinutes = 120
            });

            return JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("id").GetInt32();
        }

        // ── Tests ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task CreateMedia_WithoutAuth_Returns401()
        {
            var client = _factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/v1/media", new
            {
                mediaTypeId = 1,
                name = "Unauthorized",
                hierarchyLevel = 0
            });

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task CreateMedia_Valid_Returns201WithId()
        {
            var client = await AuthClientAsync();

            var response = await client.PostAsJsonAsync("/api/v1/media", new
            {
                mediaTypeId = 1,
                name = "Inception",
                year = 2010,
                hierarchyLevel = 0,
                runtimeMinutes = 148
            });

            response.StatusCode.Should().Be(HttpStatusCode.Created);

            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            doc.GetProperty("success").GetBoolean().Should().BeTrue();
            doc.GetProperty("data").GetProperty("id").GetInt32().Should().BePositive();
            doc.GetProperty("data").GetProperty("name").GetString().Should().Be("Inception");
        }

        [Fact]
        public async Task GetMedia_ExistingItem_Returns200WithData()
        {
            var client = await AuthClientAsync();
            var id = await CreateMediaItemAsync(client, "The Matrix");

            var response = await client.GetAsync($"/api/v1/media/{id}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            doc.GetProperty("data").GetProperty("name").GetString().Should().Be("The Matrix");
        }

        [Fact]
        public async Task GetMedia_NonExistentId_Returns404()
        {
            var client = await AuthClientAsync();
            var response = await client.GetAsync("/api/v1/media/999999");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task SearchMedia_WithMatchingQuery_ReturnsResults()
        {
            var client = await AuthClientAsync();
            await CreateMediaItemAsync(client, "Unique Film Title XYZZY");

            var response = await client.GetAsync("/api/v1/media/search?query=XYZZY");

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            doc.GetProperty("data").GetArrayLength().Should().BeGreaterThan(0);
        }

        [Fact]
        public async Task SearchMedia_EmptyQuery_ReturnsAll()
        {
            var client = await AuthClientAsync();
            await CreateMediaItemAsync(client, "Any Film A");
            await CreateMediaItemAsync(client, "Any Film B");

            var response = await client.GetAsync("/api/v1/media/search");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            doc.GetProperty("data").GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
        }

        [Fact]
        public async Task UpdateMedia_ValidData_Returns200()
        {
            var client = await AuthClientAsync();
            var id = await CreateMediaItemAsync(client, "Old Title");

            var response = await client.PatchAsJsonAsync($"/api/v1/media/{id}", new
            {
                name = "Updated Title",
                year = 2024
            });

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            doc.GetProperty("data").GetProperty("name").GetString().Should().Be("Updated Title");
        }

        [Fact]
        public async Task UpdateMedia_NonExistentId_Returns404()
        {
            var client = await AuthClientAsync();
            var response = await client.PatchAsJsonAsync("/api/v1/media/999999", new { name = "Ghost" });
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task DeleteMedia_ExistingItem_Returns204()
        {
            var client = await AuthClientAsync();
            var id = await CreateMediaItemAsync(client, "To Delete");

            var deleteResp = await client.DeleteAsync($"/api/v1/media/{id}");
            deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

            // Confirm it's gone
            var getResp = await client.GetAsync($"/api/v1/media/{id}");
            getResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task DeleteMedia_NonExistentId_Returns404()
        {
            var client = await AuthClientAsync();
            var response = await client.DeleteAsync("/api/v1/media/999999");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }
}
