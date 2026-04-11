using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

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

        [Fact]
        public async Task GetMediaItem_WithFileScannerData_HasPhysicalFileTrue()
        {
            // Arrange — create a movie with fileScanner metadata (filePaths array style)
            const string metaJson =
                """{"fileScanner": {"filePaths": ["/path/to/file.mkv"], "importedAt": "2026-01-01T00:00:00Z"}}""";

            int seededId;
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<Chronicle.Data.ChronicleDbContext>();
                var item = new Chronicle.Core.Models.MediaItem
                {
                    MediaTypeId = 1,
                    Name = "HasPhysicalFile Test Movie",
                    HierarchyLevel = 0,
                    MetadataJson = metaJson,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                db.MediaItems.Add(item);
                await db.SaveChangesAsync();
                seededId = item.Id;
            }

            var client = await AuthClientAsync();

            // Act
            var response = await client.GetAsync($"/api/v1/media/{seededId}");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            var data = body.GetProperty("data");
            data.GetProperty("hasPhysicalFile").GetBoolean().Should().BeTrue();
            data.GetProperty("hasMetadataOnly").GetBoolean().Should().BeFalse();
        }

        [Fact]
        public async Task GetMediaItem_WithoutFileScannerData_HasMetadataOnlyTrue()
        {
            // Arrange — create a movie with only plugin metadata (no fileScanner key)
            const string metaJson =
                """{"chronicle.plugin.tmdb": {"title": "Some Movie"}}""";

            int seededId;
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<Chronicle.Data.ChronicleDbContext>();
                var item = new Chronicle.Core.Models.MediaItem
                {
                    MediaTypeId = 1,
                    Name = "HasMetadataOnly Test Movie",
                    HierarchyLevel = 0,
                    MetadataJson = metaJson,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                db.MediaItems.Add(item);
                await db.SaveChangesAsync();
                seededId = item.Id;
            }

            var client = await AuthClientAsync();

            // Act
            var response = await client.GetAsync($"/api/v1/media/{seededId}");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            var data = body.GetProperty("data");
            data.GetProperty("hasPhysicalFile").GetBoolean().Should().BeFalse();
            data.GetProperty("hasMetadataOnly").GetBoolean().Should().BeTrue();
        }

        [Fact]
        public async Task GetMediaItem_ImportDirectItem_ReturnsFileScannerMeta()
        {
            // Arrange: seed a media item that has only fileScanner metadata (no plugin data yet).
            // This simulates an item imported via /scan/import-direct before metadata enrichment.
            const string metaJson =
                """{"fileScanner": {"filePath": "/some/path.mkv", "localPosterPath": null, "nfoPosterUrl": null}}""";

            int seededId;
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<Chronicle.Data.ChronicleDbContext>();
                var item = new Chronicle.Core.Models.MediaItem
                {
                    MediaTypeId = 1,
                    Name = "ImportDirect Film",
                    HierarchyLevel = 0,
                    MetadataJson = metaJson,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                db.MediaItems.Add(item);
                await db.SaveChangesAsync();
                seededId = item.Id;
            }

            var client = await AuthClientAsync();

            // Act
            var response = await client.GetAsync($"/api/v1/media/{seededId}");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            doc.GetProperty("success").GetBoolean().Should().BeTrue();

            var data = doc.GetProperty("data");

            // fileScannerMeta must be present and non-null
            data.TryGetProperty("fileScannerMeta", out var fsMeta).Should().BeTrue();
            fsMeta.ValueKind.Should().NotBe(JsonValueKind.Null);

            fsMeta.GetProperty("filePath").GetString().Should().Be("/some/path.mkv");

            // pluginMetadata must be null/absent (item was imported directly without any plugin metadata)
            if (data.TryGetProperty("pluginMetadata", out var pluginMetadata))
                pluginMetadata.ValueKind.Should().Be(JsonValueKind.Null);
        }

        [Fact]
        public async Task GetMediaItem_ShowWithEpisodeFile_HasPhysicalFileTrueAndHasMetadataOnlyFalse()
        {
            // Arrange — create a TV Show (level 0) with no own fileScanner data,
            // a Season child (level 1) with no file, and an Episode grandchild (level 2) with a file.
            // The Show should report hasPhysicalFile=true and hasMetadataOnly=false because
            // a descendant (the Episode) has a physical file.
            const string episodeMeta =
                """{"fileScanner": {"filePaths": ["/tv/show/s01e01.mkv"], "importedAt": "2026-01-01T00:00:00Z"}}""";
            const string showMeta =
                """{"chronicle.plugin.tmdb": {"title": "Descendant Test Show"}}""";

            int showId;
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<Chronicle.Data.ChronicleDbContext>();

                var show = new Chronicle.Core.Models.MediaItem
                {
                    MediaTypeId = 1,
                    Name = "Descendant Test Show",
                    HierarchyLevel = 0,
                    MetadataJson = showMeta,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                db.MediaItems.Add(show);
                await db.SaveChangesAsync();
                showId = show.Id;

                var season = new Chronicle.Core.Models.MediaItem
                {
                    MediaTypeId = 1,
                    ParentId = showId,
                    Name = "Season 1",
                    HierarchyLevel = 1,
                    Number = 1,
                    MetadataJson = null,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                db.MediaItems.Add(season);
                await db.SaveChangesAsync();

                var episode = new Chronicle.Core.Models.MediaItem
                {
                    MediaTypeId = 1,
                    ParentId = season.Id,
                    Name = "Pilot",
                    HierarchyLevel = 2,
                    Number = 1,
                    MetadataJson = episodeMeta,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                db.MediaItems.Add(episode);
                await db.SaveChangesAsync();
            }

            var client = await AuthClientAsync();

            // Act
            var response = await client.GetAsync($"/api/v1/media/{showId}");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            var data = body.GetProperty("data");
            data.GetProperty("hasPhysicalFile").GetBoolean().Should().BeTrue(
                "the show has a grandchild episode with a physical file");
            data.GetProperty("hasMetadataOnly").GetBoolean().Should().BeFalse(
                "the show is not metadata-only because at least one descendant has a file");
        }

        [Fact]
        public async Task GetLibrary_ItemWithFileScannerData_HasPhysicalFileTrueInResponse()
        {
            // Arrange — seed a movie with fileScanner metadata, then add it to the user's library
            const string metaJson =
                """{"fileScanner": {"filePaths": ["/path/to/library-movie.mkv"], "importedAt": "2026-01-01T00:00:00Z"}}""";

            int seededId;
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<Chronicle.Data.ChronicleDbContext>();
                var item = new Chronicle.Core.Models.MediaItem
                {
                    MediaTypeId = 1,
                    Name = "Library Physical File Test Movie",
                    HierarchyLevel = 0,
                    MetadataJson = metaJson,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                db.MediaItems.Add(item);
                await db.SaveChangesAsync();
                seededId = item.Id;
            }

            var client = await AuthClientAsync();

            // Add item to library
            var addResp = await client.PostAsJsonAsync("/api/v1/library", new
            {
                mediaItemId = seededId,
                status = "PlanToWatch"
            });
            addResp.StatusCode.Should().Be(HttpStatusCode.OK);

            // Act
            var response = await client.GetAsync("/api/v1/library");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            var entries = doc.GetProperty("data");

            // Find the library entry for our seeded item
            JsonElement? match = null;
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.GetProperty("mediaItem").GetProperty("id").GetInt32() == seededId)
                {
                    match = entry;
                    break;
                }
            }

            match.Should().NotBeNull("the library entry for the seeded item should be present");
            var mediaItem = match!.Value.GetProperty("mediaItem");
            mediaItem.GetProperty("hasPhysicalFile").GetBoolean().Should().BeTrue(
                "the item has fileScanner data with a filePaths entry");
            mediaItem.GetProperty("hasMetadataOnly").GetBoolean().Should().BeFalse(
                "the item has a physical file so it is not metadata-only");
        }

        [Fact]
        public async Task GetMediaItem_ShowWithMixedEpisodes_HasBothPhysicalFileAndMetadataOnly()
        {
            // Arrange — a TV Show (level 0) with one season containing two episodes:
            // one episode has a file, the other does not.  The show should report
            // hasPhysicalFile=true AND hasMetadataOnly=true (mixed state).
            const string episodeWithFileMeta =
                """{"fileScanner": {"filePaths": ["/tv/mixed/s01e01.mkv"], "importedAt": "2026-01-01T00:00:00Z"}}""";
            // episode without any file scanner data (metadata-only)
            const string episodeNoFileMeta =
                """{"chronicle.plugin.tmdb": {"title": "Episode 2"}}""";

            int showId;
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<Chronicle.Data.ChronicleDbContext>();

                var show = new Chronicle.Core.Models.MediaItem
                {
                    MediaTypeId = 1,
                    Name = "Mixed Episodes Test Show",
                    HierarchyLevel = 0,
                    MetadataJson = null,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                db.MediaItems.Add(show);
                await db.SaveChangesAsync();
                showId = show.Id;

                var season = new Chronicle.Core.Models.MediaItem
                {
                    MediaTypeId = 1,
                    ParentId = showId,
                    Name = "Season 1",
                    HierarchyLevel = 1,
                    Number = 1,
                    MetadataJson = null,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                db.MediaItems.Add(season);
                await db.SaveChangesAsync();

                var ep1 = new Chronicle.Core.Models.MediaItem
                {
                    MediaTypeId = 1,
                    ParentId = season.Id,
                    Name = "Pilot",
                    HierarchyLevel = 2,
                    Number = 1,
                    MetadataJson = episodeWithFileMeta,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                var ep2 = new Chronicle.Core.Models.MediaItem
                {
                    MediaTypeId = 1,
                    ParentId = season.Id,
                    Name = "Episode 2",
                    HierarchyLevel = 2,
                    Number = 2,
                    MetadataJson = episodeNoFileMeta,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                db.MediaItems.AddRange(ep1, ep2);
                await db.SaveChangesAsync();
            }

            var client = await AuthClientAsync();

            // Act
            var response = await client.GetAsync($"/api/v1/media/{showId}");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            var data = body.GetProperty("data");
            data.GetProperty("hasPhysicalFile").GetBoolean().Should().BeTrue(
                "at least one episode has a physical file");
            data.GetProperty("hasMetadataOnly").GetBoolean().Should().BeTrue(
                "at least one episode is metadata-only, so the show is in a mixed state");
        }
    }
}
