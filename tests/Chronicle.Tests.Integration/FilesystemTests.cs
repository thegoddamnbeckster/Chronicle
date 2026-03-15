using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Chronicle.Tests.Integration
{
    public class FilesystemTests : IClassFixture<ChronicleApiFactory>
    {
        private readonly ChronicleApiFactory _factory;

        public FilesystemTests(ChronicleApiFactory factory)
        {
            factory.SeedDatabase();
            _factory = factory;
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private async Task<HttpClient> AuthClientAsync()
        {
            var client = _factory.CreateClient();
            var username = $"fs_{Guid.NewGuid():N}";

            var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
                new { username, password = "Password123!" });

            var token = JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("token").GetString()!;

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            return client;
        }

        // ── Tests ─────────────────────────────────────────────────────────────────

        [Fact]
        public async Task GetFilesystem_NoToken_Returns401()
        {
            var client = _factory.CreateClient();
            var resp = await client.GetAsync("/api/v1/filesystem");
            resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task GetFilesystem_NoPath_ReturnsDriveRoots()
        {
            var client = await AuthClientAsync();
            var resp = await client.GetAsync("/api/v1/filesystem");

            resp.StatusCode.Should().Be(HttpStatusCode.OK);

            var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

            var data = body.RootElement.GetProperty("data");

            // At drive roots, path and parent are null
            data.GetProperty("path").ValueKind.Should().Be(JsonValueKind.Null);
            data.GetProperty("parent").ValueKind.Should().Be(JsonValueKind.Null);

            // At least one drive / mount point must exist
            var dirs = data.GetProperty("directories");
            dirs.GetArrayLength().Should().BeGreaterThan(0);

            // Each entry has non-empty name and path
            var first = dirs[0];
            first.GetProperty("name").GetString().Should().NotBeNullOrEmpty();
            first.GetProperty("path").GetString().Should().NotBeNullOrEmpty();
        }

        [Fact]
        public async Task GetFilesystem_ValidPath_ReturnsSubdirectories()
        {
            var client = await AuthClientAsync();

            // Temp directory always exists and is readable on any OS
            var tempPath = Path.GetTempPath()
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var resp = await client.GetAsync(
                $"/api/v1/filesystem?path={Uri.EscapeDataString(tempPath)}");

            resp.StatusCode.Should().Be(HttpStatusCode.OK);

            var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

            var data = body.RootElement.GetProperty("data");
            data.GetProperty("path").GetString().Should().NotBeNullOrEmpty();
            data.GetProperty("parent").ValueKind.Should().NotBe(JsonValueKind.Null);

            // directories is always an array (may be empty if temp has no subdirs)
            data.GetProperty("directories").ValueKind.Should().Be(JsonValueKind.Array);
        }

        [Fact]
        public async Task GetFilesystem_InvalidPath_Returns400()
        {
            var client = await AuthClientAsync();

            var fakePath = Path.Combine(Path.GetTempPath(), "chronicle_nonexistent_" + Guid.NewGuid().ToString("N"));
            var resp = await client.GetAsync(
                $"/api/v1/filesystem?path={Uri.EscapeDataString(fakePath)}");

            resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
            body.RootElement.GetProperty("error").GetProperty("code").GetString()
                .Should().Be("PATH_NOT_FOUND");
        }
    }
}
