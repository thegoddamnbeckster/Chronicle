using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Chronicle.Tests.Integration
{
    public class AuthTests : IClassFixture<ChronicleApiFactory>
    {
        private readonly HttpClient _client;
        private readonly ChronicleApiFactory _factory;
        private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

        public AuthTests(ChronicleApiFactory factory)
        {
            factory.SeedDatabase();
            _factory = factory;
            _client = factory.CreateClient();
        }

        [Fact]
        public async Task Health_ReturnsOk()
        {
            var response = await _client.GetAsync("/api/health");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task Register_ValidUser_Returns200WithToken()
        {
            var response = await _client.PostAsJsonAsync("/api/v1/auth/register", new
            {
                username = $"user_{Guid.NewGuid():N}",
                password = "Password123!",
                email = "test@example.com"
            });

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var body = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(body).RootElement;

            doc.GetProperty("success").GetBoolean().Should().BeTrue();
            doc.GetProperty("data").GetProperty("token").GetString().Should().NotBeNullOrEmpty();
        }

        [Fact]
        public async Task Register_DuplicateUsername_Returns409()
        {
            var username = $"dup_{Guid.NewGuid():N}";

            await _client.PostAsJsonAsync("/api/v1/auth/register", new
            {
                username,
                password = "Password123!"
            });

            var response = await _client.PostAsJsonAsync("/api/v1/auth/register", new
            {
                username,
                password = "Password123!"
            });

            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        [Fact]
        public async Task Login_ValidCredentials_Returns200WithToken()
        {
            var username = $"login_{Guid.NewGuid():N}";
            await _client.PostAsJsonAsync("/api/v1/auth/register", new { username, password = "Password123!" });

            var response = await _client.PostAsJsonAsync("/api/v1/auth/login", new
            {
                username,
                password = "Password123!"
            });

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var body = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(body).RootElement;
            doc.GetProperty("success").GetBoolean().Should().BeTrue();
            doc.GetProperty("data").GetProperty("token").GetString().Should().NotBeNullOrEmpty();
        }

        [Fact]
        public async Task Login_WrongPassword_Returns401()
        {
            var username = $"badpw_{Guid.NewGuid():N}";
            await _client.PostAsJsonAsync("/api/v1/auth/register", new { username, password = "Password123!" });

            var response = await _client.PostAsJsonAsync("/api/v1/auth/login", new
            {
                username,
                password = "WRONG"
            });

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task GetMe_WithoutToken_Returns401()
        {
            var response = await _client.GetAsync("/api/v1/users/me");
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task GetMe_WithToken_Returns200()
        {
            var username = $"me_{Guid.NewGuid():N}";
            var reg = await _client.PostAsJsonAsync("/api/v1/auth/register", new { username, password = "Password123!" });
            var body = await reg.Content.ReadAsStringAsync();
            var token = JsonDocument.Parse(body).RootElement
                .GetProperty("data").GetProperty("token").GetString()!;

            // Use factory client so requests go through the in-process TestServer
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var meResponse = await client.GetAsync("/api/v1/users/me");
            meResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var meBody = await meResponse.Content.ReadAsStringAsync();
            var meDoc = JsonDocument.Parse(meBody).RootElement;
            meDoc.GetProperty("data").GetProperty("username").GetString().Should().Be(username);
        }
    }
}
