using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Chronicle.Tests.Integration
{
    public class FileScanTests : IClassFixture<ChronicleApiFactory>
    {
        private readonly ChronicleApiFactory _factory;
        private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

        public FileScanTests(ChronicleApiFactory factory)
        {
            factory.SeedDatabase();
            _factory = factory;
        }

        private async Task<HttpClient> AuthClientAsync()
        {
            var client = _factory.CreateClient();
            var username = $"scan_{Guid.NewGuid():N}";

            var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
                new { username, password = "Password123!" });

            var token = JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("token").GetString()!;

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            return client;
        }

        [Fact]
        public async Task PreviewGrouped_ReturnsBadRequest_WhenPathIsEmpty()
        {
            var client = await AuthClientAsync();

            var response = await client.PostAsJsonAsync("/api/v1/scan/preview-grouped",
                new { path = "", recursive = true, mediaTypeId = 1 });

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task ImportGroups_ReturnsUnauthorized_WhenNoToken()
        {
            var client = _factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/v1/scan/import-groups",
                new { groups = Array.Empty<object>(), mediaTypeId = 1 });
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
    }
}
