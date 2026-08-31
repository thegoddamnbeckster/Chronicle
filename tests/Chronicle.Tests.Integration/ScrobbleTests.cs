using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Chronicle.Tests.Integration
{
    public class ScrobbleTests : IClassFixture<ChronicleApiFactory>
    {
        private readonly ChronicleApiFactory _factory;

        public ScrobbleTests(ChronicleApiFactory factory)
        {
            factory.SeedDatabase();
            _factory = factory;
        }

        private async Task<(HttpClient client, int mediaItemId)> SetupAsync()
        {
            var client = _factory.CreateClient();
            var username = $"scr_{Guid.NewGuid():N}";

            var reg = await client.PostAsJsonAsync("/api/v1/auth/register", new { username, password = "Password123!" });
            var body = await reg.Content.ReadAsStringAsync();
            var token = JsonDocument.Parse(body).RootElement
                .GetProperty("data").GetProperty("token").GetString()!;

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // Create a media item to scrobble
            var mediaResp = await client.PostAsJsonAsync("/api/v1/media", new
            {
                mediaTypeId = 1,
                name = "Test Show S01E01",
                hierarchyLevel = 2,
                runtimeMinutes = 45
            });
            var mediaBody = await mediaResp.Content.ReadAsStringAsync();
            var mediaId = JsonDocument.Parse(mediaBody).RootElement
                .GetProperty("data").GetProperty("id").GetInt32();

            return (client, mediaId);
        }

        [Fact]
        public async Task Scrobble_ValidRequest_Returns200()
        {
            var (client, mediaId) = await SetupAsync();

            var response = await client.PostAsJsonAsync("/api/v1/scrobble", new
            {
                mediaItemId = mediaId,
                progressPercent = 50.0
            });

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var body = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(body).RootElement;
            doc.GetProperty("success").GetBoolean().Should().BeTrue();
            doc.GetProperty("data").GetProperty("markedAsWatched").GetBoolean().Should().BeFalse();
        }

        [Fact]
        public async Task Scrobble_Over80Percent_MarksAsWatched()
        {
            var (client, mediaId) = await SetupAsync();

            var response = await client.PostAsJsonAsync("/api/v1/scrobble", new
            {
                mediaItemId = mediaId,
                progressPercent = 90.0,
                deviceName = "Kodi"
            });

            var body = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(body).RootElement;
            doc.GetProperty("data").GetProperty("markedAsWatched").GetBoolean().Should().BeTrue();
        }

        [Fact]
        public async Task Scrobble_WithoutToken_Returns401()
        {
            var client = _factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/v1/scrobble", new { mediaItemId = 1, progressPercent = 50.0 });
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // ScrobbleService.GetHistoryAsync collapses consecutive same-item scrobbles into a
        // single "session" row (a gap over 30 minutes starts a new one) -- per-user confirmed
        // report (2026-08-29): a single ~20-minute episode was showing as a dozen near-
        // duplicate history rows, one per raw progress tick. These two scrobbles land well
        // within the same session, so history correctly reports one row (the session's
        // latest/highest progress), not two -- this test previously asserted the pre-fix
        // one-row-per-scrobble behavior.
        [Fact]
        public async Task GetHistory_CollapsesSameSessionScrobblesIntoOneRow()
        {
            var (client, mediaId) = await SetupAsync();

            await client.PostAsJsonAsync("/api/v1/scrobble", new { mediaItemId = mediaId, progressPercent = 60.0 });
            await client.PostAsJsonAsync("/api/v1/scrobble", new { mediaItemId = mediaId, progressPercent = 85.0 });

            var response = await client.GetAsync("/api/v1/scrobble/history");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var body = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(body).RootElement;
            var data = doc.GetProperty("data");
            data.GetArrayLength().Should().Be(1);
            data[0].GetProperty("progressPercent").GetDouble().Should().Be(85.0);
        }
    }
}
