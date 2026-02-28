using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Chronicle.Tests.Integration
{
    /// <summary>
    /// Integration tests for API token management and X-API-Key authentication.
    /// </summary>
    public class ApiTokenTests : IClassFixture<ChronicleApiFactory>
    {
        private readonly ChronicleApiFactory _factory;

        public ApiTokenTests(ChronicleApiFactory factory)
        {
            factory.SeedDatabase();
            _factory = factory;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Registers a fresh user and returns an authenticated Bearer client.</summary>
        private async Task<(HttpClient client, string token)> AuthClientAsync()
        {
            var client = _factory.CreateClient();
            var username = $"tok_{Guid.NewGuid():N}";

            var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
                new { username, password = "Password123!" });

            var token = JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("token").GetString()!;

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            return (client, token);
        }

        private static async Task<(int id, string rawKey)> CreateTokenAsync(HttpClient client, string name = "Test Token")
        {
            var resp = await client.PostAsJsonAsync("/api/v1/tokens", new { name });
            resp.StatusCode.Should().Be(HttpStatusCode.OK);

            var data = JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data");

            return (data.GetProperty("id").GetInt32(),
                    data.GetProperty("token").GetString()!);
        }

        // ── Tests ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task CreateToken_ValidRequest_ReturnsRawKeyOnce()
        {
            var (client, _) = await AuthClientAsync();

            var resp = await client.PostAsJsonAsync("/api/v1/tokens", new { name = "MyScrobbler" });
            resp.StatusCode.Should().Be(HttpStatusCode.OK);

            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
            doc.GetProperty("success").GetBoolean().Should().BeTrue();

            var rawKey = doc.GetProperty("data").GetProperty("token").GetString()!;
            // Format: "chr_live_" (9 chars) + 32 hex chars (16 random bytes) = 41 chars total
            rawKey.Should().StartWith("chr_live_");
            rawKey.Should().HaveLength(41);
        }

        [Fact]
        public async Task CreateToken_WithoutAuth_Returns401()
        {
            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/api/v1/tokens", new { name = "NoAuth" });
            resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task ListTokens_ReturnsCreatedTokens()
        {
            var (client, _) = await AuthClientAsync();

            await CreateTokenAsync(client, "Token Alpha");
            await CreateTokenAsync(client, "Token Beta");

            var resp = await client.GetAsync("/api/v1/tokens");
            resp.StatusCode.Should().Be(HttpStatusCode.OK);

            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
            doc.GetProperty("data").GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
        }

        [Fact]
        public async Task ListTokens_DoesNotIncludeOtherUsersTokens()
        {
            var (clientA, _) = await AuthClientAsync();
            var (clientB, _) = await AuthClientAsync();

            await CreateTokenAsync(clientA, "User A's Token");

            // User B should see an empty list (no tokens created yet for B)
            var resp = await clientB.GetAsync("/api/v1/tokens");
            resp.StatusCode.Should().Be(HttpStatusCode.OK);

            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
            doc.GetProperty("data").GetArrayLength().Should().Be(0);
        }

        [Fact]
        public async Task RevokeToken_ExistingToken_Returns200()
        {
            var (client, _) = await AuthClientAsync();
            var (tokenId, _) = await CreateTokenAsync(client);

            var resp = await client.DeleteAsync($"/api/v1/tokens/{tokenId}");
            resp.StatusCode.Should().Be(HttpStatusCode.OK);

            // After revocation the token list should be empty
            var listResp = await client.GetAsync("/api/v1/tokens");
            var doc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync()).RootElement;
            doc.GetProperty("data").GetArrayLength().Should().Be(0);
        }

        [Fact]
        public async Task RevokeToken_NonExistentId_Returns404()
        {
            var (client, _) = await AuthClientAsync();
            var resp = await client.DeleteAsync("/api/v1/tokens/999999");
            resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task RevokeToken_OtherUsersToken_Returns404()
        {
            // User A creates a token
            var (clientA, _) = await AuthClientAsync();
            var (tokenId, _) = await CreateTokenAsync(clientA);

            // User B tries to revoke User A's token
            var (clientB, _) = await AuthClientAsync();
            var resp = await clientB.DeleteAsync($"/api/v1/tokens/{tokenId}");
            resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        /// <summary>
        /// End-to-end test: creates an API token, then uses the raw key in the X-API-Key
        /// header to authenticate a scrobble request — verifying the full API key auth path.
        /// </summary>
        [Fact]
        public async Task ScrobbleWithApiKey_ValidKey_Returns200()
        {
            // Step 1: Register and get bearer client
            var (bearerClient, _) = await AuthClientAsync();

            // Step 2: Create an API token and capture the raw value
            var (_, rawKey) = await CreateTokenAsync(bearerClient, "Kodi Scrobbler");

            // Step 3: Create a media item to scrobble (using bearer)
            var mediaResp = await bearerClient.PostAsJsonAsync("/api/v1/media", new
            {
                mediaTypeId = 1,
                name = "API Key Test Show S01E01",
                hierarchyLevel = 2,
                runtimeMinutes = 45
            });
            var mediaId = JsonDocument.Parse(await mediaResp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("id").GetInt32();

            // Step 4: Scrobble using X-API-Key header (new client, no Bearer token)
            var apiKeyClient = _factory.CreateClient();
            apiKeyClient.DefaultRequestHeaders.Add("X-API-Key", rawKey);

            var scrobbleResp = await apiKeyClient.PostAsJsonAsync("/api/v1/scrobble", new
            {
                mediaItemId = mediaId,
                progressPercent = 55.0,
                deviceName = "Kodi"
            });

            scrobbleResp.StatusCode.Should().Be(HttpStatusCode.OK);

            var doc = JsonDocument.Parse(await scrobbleResp.Content.ReadAsStringAsync()).RootElement;
            doc.GetProperty("success").GetBoolean().Should().BeTrue();
        }

        [Fact]
        public async Task ScrobbleWithApiKey_InvalidKey_Returns401()
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-API-Key", "chr_live_not_a_real_key_00000000");

            var resp = await client.PostAsJsonAsync("/api/v1/scrobble", new
            {
                mediaItemId = 1,
                progressPercent = 50.0
            });

            resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
    }
}
