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
    /// <summary>Listing every recorded file of an item, and serving recorded local artwork.</summary>
    public class MediaFilesEndpointsTests : IClassFixture<ChronicleApiFactory>, IDisposable
    {
        private const string Password = "Password123!";
        private readonly ChronicleApiFactory _factory;
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "chronicle-media-files-" + Guid.NewGuid().ToString("N"));

        public MediaFilesEndpointsTests(ChronicleApiFactory factory)
        {
            factory.SeedDatabase();
            _factory = factory;
            Directory.CreateDirectory(_dir);
        }

        public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* scratch */ } }

        private static async Task<JsonElement> Json(HttpResponseMessage r) =>
            JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

        private async Task<HttpClient> SignInAsync()
        {
            var reg = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { username = $"mf_{Guid.NewGuid():N}", password = Password });
            var data = (await Json(reg)).GetProperty("data");
            var c = _factory.CreateClient();
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", data.GetProperty("token").GetString()!);
            return c;
        }

        private async Task<int> ItemAsync(string name, string? metadataJson = null)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var typeId = (await db.MediaTypes.FirstAsync()).Id;
            var item = new MediaItem { Name = name, MediaTypeId = typeId, MetadataJson = metadataJson };
            db.MediaItems.Add(item);
            await db.SaveChangesAsync();
            return item.Id;
        }

        private async Task<int> RelatedAsync(int itemId, string path, string kind)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var row = new MediaItemRelatedFile { MediaItemId = itemId, Path = path, Kind = kind };
            db.MediaItemRelatedFiles.Add(row);
            await db.SaveChangesAsync();
            return row.Id;
        }

        private static string Meta(params string[] paths) =>
            JsonSerializer.Serialize(new { fileScanner = new { filePaths = paths } });

        // ── /files ─────────────────────────────────────────────────────────

        [Fact]
        public async Task Files_ListsEveryRecordedPath_WithSizeOrAMissingFlag()
        {
            var present = Path.Combine(_dir, "part1.mkv");
            await File.WriteAllBytesAsync(present, new byte[2048]);
            var folder = Path.Combine(_dir, "Album");
            Directory.CreateDirectory(folder);
            var missing = Path.Combine(_dir, "part2.mkv");
            var id = await ItemAsync("Heat", Meta(present, missing, folder, present));
            var client = await SignInAsync();

            var response = await client.GetAsync($"/api/v1/media/{id}/files");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var rows = (await Json(response)).GetProperty("data").EnumerateArray().ToList();
            rows.Should().HaveCount(3);   // the duplicate is listed once
            var first = rows.Single(r => r.GetProperty("path").GetString() == present);
            first.GetProperty("exists").GetBoolean().Should().BeTrue();
            first.GetProperty("sizeBytes").GetInt64().Should().Be(2048);
            rows.Single(r => r.GetProperty("path").GetString() == missing).GetProperty("exists").GetBoolean().Should().BeFalse();
            rows.Single(r => r.GetProperty("path").GetString() == folder).GetProperty("type").GetString().Should().Be("folder");
        }

        [Fact]
        public async Task Files_IsEmptyForAnItemWithNoRecordedFiles_And404ForNoSuchItem()
        {
            var id = await ItemAsync("Plain");
            var client = await SignInAsync();

            var ok = await client.GetAsync($"/api/v1/media/{id}/files");
            (await Json(ok)).GetProperty("data").GetArrayLength().Should().Be(0);

            (await client.GetAsync("/api/v1/media/99999999/files")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task Files_NeedsSignIn() =>
            (await _factory.CreateClient().GetAsync("/api/v1/media/1/files")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // ── artwork content ────────────────────────────────────────────────

        [Fact]
        public async Task RecordedArtwork_IsServedWithTheRightContentType()
        {
            var png = Path.Combine(_dir, "poster.png");
            await File.WriteAllBytesAsync(png, [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
            var id = await ItemAsync("Heat");
            var fileId = await RelatedAsync(id, png, RelatedFileKinds.Artwork);
            var client = await SignInAsync();

            var response = await client.GetAsync($"/api/v1/media/{id}/related-files/{fileId}/content");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
            (await response.Content.ReadAsByteArrayAsync()).Should().StartWith(new byte[] { 0x89, 0x50 });
        }

        [Fact]
        public async Task OnlyArtworkIsServed_NotSubtitlesOrAnythingElse()
        {
            var srt = Path.Combine(_dir, "Heat.srt");
            await File.WriteAllTextAsync(srt, "1\n00:00:01,000 --> 00:00:02,000\nhi");
            var id = await ItemAsync("Heat");
            var fileId = await RelatedAsync(id, srt, RelatedFileKinds.Subtitle);
            var client = await SignInAsync();

            (await client.GetAsync($"/api/v1/media/{id}/related-files/{fileId}/content")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task ArtworkWhoseFileIsNotAnImage_IsNotServed()
        {
            var sneaky = Path.Combine(_dir, "notes.txt");
            await File.WriteAllTextAsync(sneaky, "secret");
            var id = await ItemAsync("Heat");
            var fileId = await RelatedAsync(id, sneaky, RelatedFileKinds.Artwork);
            var client = await SignInAsync();

            (await client.GetAsync($"/api/v1/media/{id}/related-files/{fileId}/content")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task ArtworkIsOnlyServedThroughItsOwnItem()
        {
            var jpg = Path.Combine(_dir, "poster.jpg");
            await File.WriteAllBytesAsync(jpg, [0xFF, 0xD8, 0xFF]);
            var owner = await ItemAsync("Owner");
            var other = await ItemAsync("Other");
            var fileId = await RelatedAsync(owner, jpg, RelatedFileKinds.Artwork);
            var client = await SignInAsync();

            (await client.GetAsync($"/api/v1/media/{other}/related-files/{fileId}/content")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await client.GetAsync($"/api/v1/media/{owner}/related-files/{fileId}/content")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task ArtworkThatWentMissing_Is404()
        {
            var id = await ItemAsync("Heat");
            var fileId = await RelatedAsync(id, Path.Combine(_dir, "gone.jpg"), RelatedFileKinds.Artwork);
            var client = await SignInAsync();

            (await client.GetAsync($"/api/v1/media/{id}/related-files/{fileId}/content")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task ArtworkContent_NeedsSignIn() =>
            (await _factory.CreateClient().GetAsync("/api/v1/media/1/related-files/1/content")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
