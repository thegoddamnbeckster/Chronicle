using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Chronicle.Tests.Integration
{
    public class LibraryTests : IClassFixture<ChronicleApiFactory>
    {
        private readonly ChronicleApiFactory _factory;

        // Fixed admin credentials — first registration in this factory instance gets Admin role.
        private const string AdminUser = "lib_admin_fixture";
        private const string AdminPass = "Password123!";

        public LibraryTests(ChronicleApiFactory factory)
        {
            factory.SeedDatabase();
            _factory = factory;
            EnsureAdminRegistered(factory).GetAwaiter().GetResult();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static async Task EnsureAdminRegistered(ChronicleApiFactory factory)
        {
            var client = factory.CreateClient();
            await client.PostAsJsonAsync("/api/v1/auth/register",
                new { username = AdminUser, password = AdminPass });
        }

        private async Task<HttpClient> AdminClientAsync()
        {
            var client = _factory.CreateClient();
            var login = await client.PostAsJsonAsync("/api/v1/auth/login",
                new { username = AdminUser, password = AdminPass });
            var token = JsonDocument.Parse(await login.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("token").GetString()!;
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
            return client;
        }

        private async Task<(HttpClient client, int mediaId)> SetupAsync()
        {
            var client = _factory.CreateClient();
            var username = $"lib_{Guid.NewGuid():N}";

            var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
                new { username, password = "Password123!" });

            var token = JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("token").GetString()!;

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            // Create a media item to add to the library
            var mediaResp = await client.PostAsJsonAsync("/api/v1/media", new
            {
                mediaTypeId = 1,
                name = "Library Test Show",
                hierarchyLevel = 0
            });

            var mediaId = JsonDocument.Parse(await mediaResp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("id").GetInt32();

            return (client, mediaId);
        }

        // ── Tests ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task AddToLibrary_ValidEntry_Returns200WithEntry()
        {
            var (client, mediaId) = await SetupAsync();

            var response = await client.PostAsJsonAsync("/api/v1/library", new
            {
                mediaItemId = mediaId,
                status = "PlanToWatch"
            });

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            doc.GetProperty("success").GetBoolean().Should().BeTrue();
            doc.GetProperty("data").GetProperty("status").GetString().Should().Be("PlanToWatch");
        }

        [Fact]
        public async Task AddToLibrary_WithoutAuth_Returns401()
        {
            var client = _factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/v1/library", new
            {
                mediaItemId = 1,
                status = "Watching"
            });

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task GetLibrary_ReturnsAddedEntries()
        {
            var (client, mediaId) = await SetupAsync();

            await client.PostAsJsonAsync("/api/v1/library", new { mediaItemId = mediaId, status = "Watching" });

            var response = await client.GetAsync("/api/v1/library");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            doc.GetProperty("data").GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
        }

        [Fact]
        public async Task GetLibrary_ReturnsMediaTypeInternalName()
        {
            // Confirmed real bug (2026-08-30, per-user report "Chronicle isn't sending ratings
            // to Kodi's my ratings"): this field was never populated by LibraryController's own
            // ToDto, unlike MediaController's. Chronicle_Scrobbler's whole Kodi rating/art/
            // playcount sync reads this exact field (from this exact endpoint) to decide movie
            // vs. TV-show vs. episode -- with it always null, every branch silently no-opped.
            var (client, mediaId) = await SetupAsync();
            await client.PostAsJsonAsync("/api/v1/library", new { mediaItemId = mediaId, status = "Watching" });

            var response = await client.GetAsync("/api/v1/library");
            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

            var entry = doc.GetProperty("data").EnumerateArray()
                .First(e => e.GetProperty("mediaItem").GetProperty("id").GetInt32() == mediaId);
            entry.GetProperty("mediaItem").GetProperty("mediaTypeInternalName").GetString()
                .Should().Be("tv"); // mediaTypeId 1 is seeded as Name="tv"
        }

        [Fact]
        public async Task GetLibrary_FilterByStatus_ReturnsFiltered()
        {
            var (client, mediaId) = await SetupAsync();

            // Add as PlanToWatch
            await client.PostAsJsonAsync("/api/v1/library", new { mediaItemId = mediaId, status = "PlanToWatch" });

            // Get only Watching — should be empty for this user
            var response = await client.GetAsync("/api/v1/library?status=Watching");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            doc.GetProperty("data").GetArrayLength().Should().Be(0);
        }

        [Fact]
        public async Task UpdateLibraryEntry_ChangeStatus_Returns200()
        {
            var (client, mediaId) = await SetupAsync();

            var addResp = await client.PostAsJsonAsync("/api/v1/library",
                new { mediaItemId = mediaId, status = "PlanToWatch" });
            var entryId = JsonDocument.Parse(await addResp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("id").GetInt32();

            var patchResp = await client.PatchAsJsonAsync($"/api/v1/library/{entryId}",
                new { status = "Completed" });

            patchResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var doc = JsonDocument.Parse(await patchResp.Content.ReadAsStringAsync()).RootElement;
            doc.GetProperty("data").GetProperty("status").GetString().Should().Be("Completed");
        }

        [Fact]
        public async Task UpdateLibraryEntry_InvalidStatus_Returns400()
        {
            var (client, mediaId) = await SetupAsync();

            var addResp = await client.PostAsJsonAsync("/api/v1/library",
                new { mediaItemId = mediaId, status = "Watching" });
            var entryId = JsonDocument.Parse(await addResp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("id").GetInt32();

            var patchResp = await client.PatchAsJsonAsync($"/api/v1/library/{entryId}",
                new { status = "NotARealStatus" });

            patchResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task RemoveFromLibrary_ExistingEntry_Returns204()
        {
            var (client, mediaId) = await SetupAsync();

            var addResp = await client.PostAsJsonAsync("/api/v1/library",
                new { mediaItemId = mediaId, status = "Dropped" });
            var entryId = JsonDocument.Parse(await addResp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("id").GetInt32();

            var deleteResp = await client.DeleteAsync($"/api/v1/library/{entryId}");
            deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        [Fact]
        public async Task RemoveFromLibrary_NonExistentEntry_Returns404()
        {
            var (client, _) = await SetupAsync();
            var response = await client.DeleteAsync("/api/v1/library/999999");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task NuclearReset_Returns400_WhenConfirmationMissing()
        {
            var client = await AdminClientAsync();

            var response = await client.PostAsJsonAsync("/api/v1/library/reset",
                new { confirmationToken = "" });

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task NuclearReset_Returns400_WhenTokenWrong()
        {
            var client = await AdminClientAsync();

            var response = await client.PostAsJsonAsync("/api/v1/library/reset",
                new { confirmationToken = "WRONG" });

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }
}
