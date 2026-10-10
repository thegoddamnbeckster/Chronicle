using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Scan;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Tests.Integration
{
    /// <summary>Related files and scan hints on a real SQLite file with the real migrations.</summary>
    public class RelatedFilesAndScanHintsTests : IClassFixture<SqliteApiFactory>
    {
        private const string Password = "Password123!";
        private readonly SqliteApiFactory _factory;

        public RelatedFilesAndScanHintsTests(SqliteApiFactory factory) => _factory = factory;

        private static async Task<JsonElement> Json(HttpResponseMessage r) =>
            JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

        private async Task<HttpClient> AdminAsync()
        {
            var response = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { username = $"rf_{Guid.NewGuid():N}", password = Password });
            var data = (await Json(response)).GetProperty("data");
            var id = data.GetProperty("user").GetProperty("id").GetInt32();
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                (await db.Users.FirstAsync(u => u.Id == id)).IsAdmin = true;
                await db.SaveChangesAsync();
            }
            var c = _factory.CreateClient();
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", data.GetProperty("token").GetString()!);
            return c;
        }

        [Fact]
        public async Task TheMigration_SeedsUsableHints_ForTheBuiltInTypes()
        {
            using var scope = _factory.Services.CreateScope();
            var types = await scope.ServiceProvider.GetRequiredService<ChronicleDbContext>().MediaTypes.AsNoTracking().ToListAsync();

            var tv = types.Single(t => t.Name == "tv");
            var movies = types.Single(t => t.Name == "movies");
            var music = types.Single(t => t.Name == "music");

            ScanHints.Parse(tv.ScanHintsJson)!.MatchesDistinctively("C:/x/Show - S01E02 - Title.mkv").Should().BeTrue();
            ScanHints.Parse(tv.ScanHintsJson)!.MatchesDistinctively("C:/x/Show/Season 3/a.mkv").Should().BeTrue();
            ScanHints.Parse(tv.ScanHintsJson)!.MatchesDistinctively("C:/x/Heat (1995)/Heat (1995).mkv").Should().BeFalse();
            ScanHints.Parse(movies.ScanHintsJson)!.MatchesExtension("a.mkv").Should().BeTrue();
            ScanHints.Parse(music.ScanHintsJson)!.MatchesExtension("a.flac").Should().BeTrue();
        }

        [Fact]
        public async Task EpisodesFiledUnderMovies_AreRecognisedWithTheRealSeededHints()
        {
            using var scope = _factory.Services.CreateScope();
            var types = await scope.ServiceProvider.GetRequiredService<ChronicleDbContext>().MediaTypes.AsNoTracking().ToListAsync();
            var movies = types.Single(t => t.Name == "movies");

            var s = MediaTypeMismatchDetector.Detect(
                ["C:/M/Show/Show.S02E01.mkv", "C:/M/Show/Show.S02E02.mkv", "C:/M/Show/Show.S02E03.mkv"], movies, types);

            s.Should().NotBeNull();
            s!.MediaTypeName.Should().Be(types.Single(t => t.Name == "tv").DisplayName);
        }

        [Fact]
        public async Task Hints_RoundTripThroughTheMediaTypesApi_AndBadOnesAreRefused()
        {
            var admin = await AdminAsync();
            var name = "t" + Guid.NewGuid().ToString("N")[..10];
            object Body(string? hints) => new
            {
                name, displayName = "Comics", description = "", hierarchyLevels = 1, hierarchyLabels = new[] { "Item" },
                interactionVerb = "read", progressUnit = "pages", supportsCollections = false, isTrackable = true, scanHints = hints, isActive = true,
            };

            var bad = await admin.PostAsJsonAsync("/api/v1/media-types", Body("{\"filePatterns\":[\"(oops\"]}"));
            bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await Json(bad)).GetProperty("error").GetProperty("code").GetString().Should().Be("INVALID_SCAN_HINTS");

            var created = await admin.PostAsJsonAsync("/api/v1/media-types", Body("{\"extensions\":[\".cbz\"]}"));
            created.StatusCode.Should().Be(HttpStatusCode.OK);
            var data = (await Json(created)).GetProperty("data");
            data.GetProperty("scanHints").GetString().Should().Contain(".cbz");
            var id = data.GetProperty("id").GetInt32();

            // Omitting the field on an edit leaves the hints alone; an empty string clears them.
            var keep = await admin.PutAsJsonAsync($"/api/v1/media-types/{id}", new
            {
                displayName = "Comics 2", description = "", hierarchyLevels = 1, hierarchyLabels = new[] { "Item" },
                interactionVerb = "read", progressUnit = "pages", supportsCollections = false, isTrackable = true, isActive = true,
            });
            keep.StatusCode.Should().Be(HttpStatusCode.OK);
            (await Json(keep)).GetProperty("data").GetProperty("scanHints").GetString().Should().Contain(".cbz");

            var cleared = await admin.PutAsJsonAsync($"/api/v1/media-types/{id}", Body("") );
            cleared.StatusCode.Should().Be(HttpStatusCode.OK);
            (await Json(cleared)).GetProperty("data").GetProperty("scanHints").ValueKind.Should().Be(JsonValueKind.Null);
        }

        [Fact]
        public async Task RelatedFilesEndpoint_ListsWhatWasRecorded_AndNeedsSignIn()
        {
            var admin = await AdminAsync();
            int itemId;
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                var typeId = (await db.MediaTypes.FirstAsync(t => t.Name == "movies")).Id;
                var item = new MediaItem { Name = "Heat", MediaTypeId = typeId };
                db.MediaItems.Add(item);
                await db.SaveChangesAsync();
                db.MediaItemRelatedFiles.AddRange(
                    new MediaItemRelatedFile { MediaItemId = item.Id, Path = "C:/m/Heat/Heat.srt", Kind = RelatedFileKinds.Subtitle },
                    new MediaItemRelatedFile { MediaItemId = item.Id, Path = "C:/m/Heat/poster.jpg", Kind = RelatedFileKinds.Artwork, MissingSince = DateTime.UtcNow });
                await db.SaveChangesAsync();
                itemId = item.Id;
            }

            var list = await admin.GetAsync($"/api/v1/media/{itemId}/related-files");
            list.StatusCode.Should().Be(HttpStatusCode.OK);
            var rows = (await Json(list)).GetProperty("data").EnumerateArray().ToList();
            rows.Should().HaveCount(2);
            rows.Single(r => r.GetProperty("kind").GetString() == "artwork").GetProperty("missingSince").ValueKind.Should().Be(JsonValueKind.String);

            (await _factory.CreateClient().GetAsync($"/api/v1/media/{itemId}/related-files")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task DeletingAnItem_TakesItsRelatedFileRowsWithIt()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var typeId = (await db.MediaTypes.FirstAsync(t => t.Name == "movies")).Id;
            var item = new MediaItem { Name = "Gone", MediaTypeId = typeId };
            db.MediaItems.Add(item);
            await db.SaveChangesAsync();
            db.MediaItemRelatedFiles.Add(new MediaItemRelatedFile { MediaItemId = item.Id, Path = "C:/m/Gone/a.srt", Kind = RelatedFileKinds.Subtitle });
            await db.SaveChangesAsync();

            db.MediaItems.Remove(item);
            await db.SaveChangesAsync();

            (await db.MediaItemRelatedFiles.CountAsync(r => r.Path == "C:/m/Gone/a.srt")).Should().Be(0);
        }

        [Fact]
        public async Task TheMigration_GivesExistingTypesTheFamilyAndHeadingTheirNamesImplied()
        {
            using var scope = _factory.Services.CreateScope();
            var types = await scope.ServiceProvider.GetRequiredService<ChronicleDbContext>().MediaTypes.AsNoTracking().ToListAsync();

            types.Single(t => t.Name == "tv").ProviderFamily.Should().Be("tv");
            types.Single(t => t.Name == "music").ProviderFamily.Should().Be("music");
            types.Single(t => t.Name == "movies").ProviderFamily.Should().Be("movie");
            types.Single(t => t.Name == "music").CastHeading.Should().Be("Band Members");
            types.Single(t => t.Name == "tv").CastHeading.Should().BeNull();
        }

        [Fact]
        public async Task FamilyAndHeading_RoundTrip_AreValidated_AndTheTypePickerCarriesTheHeading()
        {
            var admin = await AdminAsync();
            var name = "t" + Guid.NewGuid().ToString("N")[..10];
            object Body(string? family, string? heading) => new
            {
                name, displayName = "Podcasts", description = "", hierarchyLevels = 1, hierarchyLabels = new[] { "Item" },
                interactionVerb = "listened", progressUnit = "minutes", supportsCollections = false, isTrackable = true,
                providerFamily = family, castHeading = heading, isActive = true,
            };

            var bad = await admin.PostAsJsonAsync("/api/v1/media-types", Body("Not A Family", null));
            bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var created = await admin.PostAsJsonAsync("/api/v1/media-types", Body("music", "Hosts"));
            created.StatusCode.Should().Be(HttpStatusCode.OK);
            var data = (await Json(created)).GetProperty("data");
            data.GetProperty("providerFamily").GetString().Should().Be("music");
            data.GetProperty("castHeading").GetString().Should().Be("Hosts");

            var picker = await Json(await admin.GetAsync("/api/v1/media/types"));
            picker.GetProperty("data").EnumerateArray()
                .Single(t => t.GetProperty("name").GetString() == name)
                .GetProperty("castHeading").GetString().Should().Be("Hosts");

        }

        [Fact]
        public async Task AnEditThatOmitsFamilyAndHeading_LeavesThem_AnEmptyValueClearsThem()
        {
            var admin = await AdminAsync();
            var name = "t" + Guid.NewGuid().ToString("N")[..10];
            var created = await admin.PostAsJsonAsync("/api/v1/media-types", new
            {
                name, displayName = "Shows", description = "", hierarchyLevels = 1, hierarchyLabels = new[] { "Item" },
                interactionVerb = "watched", progressUnit = "minutes", supportsCollections = false, isTrackable = true,
                providerFamily = "tv", castHeading = "Hosts", isActive = true,
            });
            var id = (await Json(created)).GetProperty("data").GetProperty("id").GetInt32();
            object Edit(string? family, string? heading) => new
            {
                displayName = "Shows", description = "", hierarchyLevels = 1, hierarchyLabels = new[] { "Item" },
                interactionVerb = "watched", progressUnit = "minutes", supportsCollections = false, isTrackable = true,
                providerFamily = family, castHeading = heading, isActive = true,
            };

            var kept = (await Json(await admin.PutAsJsonAsync($"/api/v1/media-types/{id}", Edit(null, null)))).GetProperty("data");
            kept.GetProperty("providerFamily").GetString().Should().Be("tv");
            kept.GetProperty("castHeading").GetString().Should().Be("Hosts");

            var cleared = (await Json(await admin.PutAsJsonAsync($"/api/v1/media-types/{id}", Edit("", "")))).GetProperty("data");
            cleared.GetProperty("providerFamily").ValueKind.Should().Be(JsonValueKind.Null);
            cleared.GetProperty("castHeading").ValueKind.Should().Be(JsonValueKind.Null);
        }
    }
}