using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Chronicle.Tests.Integration
{
    public class ScanFolderControllerTests : IClassFixture<ChronicleApiFactory>
    {
        private readonly ChronicleApiFactory _factory;
        private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

        public ScanFolderControllerTests(ChronicleApiFactory factory)
        {
            factory.SeedDatabase();
            _factory = factory;
        }

        private async Task<HttpClient> AuthClientAsync()
        {
            var client = _factory.CreateClient();
            var username = $"scanfolder_{Guid.NewGuid():N}";

            var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
                new { username, password = "Password123!" });

            var token = JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("token").GetString()!;

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            return client;
        }

        [Fact]
        public async Task GetAll_ReturnsEmptyListWhenNoFolders()
        {
            var client = await AuthClientAsync();

            var response = await client.GetAsync("/api/v1/scan-folders");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = body.RootElement.GetProperty("data");
            data.GetArrayLength().Should().Be(0);
        }

        [Fact]
        public async Task ValidatePath_WithTempPath_ReturnsValid()
        {
            // FileScanController is [Authorize] at the class level with no [AllowAnonymous]
            // override on this action (unlike GetStatus, which is deliberately anonymous so
            // the frontend can decide nav visibility pre-login) — validate-path is only ever
            // called from the authenticated Scan Folders admin UI, so an anonymous client
            // correctly gets 401 here.
            var client = await AuthClientAsync();
            var tempPath = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var response = await client.PostAsJsonAsync("/api/v1/scan/validate-path",
                new { path = tempPath });

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var valid = body.RootElement.GetProperty("data").GetProperty("valid").GetBoolean();
            valid.Should().BeTrue();
        }

        [Fact]
        public async Task CreateFolder_WithTempPath_Returns201()
        {
            var client = await AuthClientAsync();
            var tempPath = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var response = await client.PostAsJsonAsync("/api/v1/scan-folders",
                new { path = tempPath, mediaTypeId = 1, recursive = true });

            response.StatusCode.Should().Be(HttpStatusCode.Created);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = body.RootElement.GetProperty("data");
            data.GetProperty("path").GetString().Should().Be(tempPath);
            data.GetProperty("mediaTypeId").GetInt32().Should().Be(1);
            data.GetProperty("id").GetInt32().Should().BeGreaterThan(0);
        }
    }
}
