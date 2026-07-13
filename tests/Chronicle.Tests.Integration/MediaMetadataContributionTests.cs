using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Chronicle.Tests.Integration;

public class MediaMetadataContributionTests : IClassFixture<ChronicleApiFactory>
{
    private readonly ChronicleApiFactory _factory;
    private const string AdminUser = "contrib_admin_fixture";
    private const string AdminPass = "Password123!";

    public MediaMetadataContributionTests(ChronicleApiFactory factory)
    {
        factory.SeedDatabase();
        _factory = factory;
        EnsureAdminRegistered(factory).GetAwaiter().GetResult();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task EnsureAdminRegistered(ChronicleApiFactory factory)
    {
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/v1/auth/register", new { username = AdminUser, password = AdminPass });
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = AdminUser, password = AdminPass });
        var token = JsonDocument.Parse(await login.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<int> CreateMediaItemAsync(HttpClient client, string name)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/media", new { mediaTypeId = 1, name, hierarchyLevel = 0 });
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("id").GetInt32();
    }

    private static async Task SetAutoRematchAsync(HttpClient client, bool enabled) =>
        await client.PutAsJsonAsync("/api/v1/settings/app/auto_rematch_on_tag_mismatch",
            new { value = enabled ? "true" : "false" });

    // ── Auth / validation ────────────────────────────────────────────────────

    [Fact]
    public async Task Contribute_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/media/1/metadata/musicbee",
            new { metadata = new { composer = "Hans Zimmer" } });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Contribute_UnknownItem_Returns404()
    {
        var client = await AdminClientAsync();
        var resp = await client.PostAsJsonAsync("/api/v1/media/9999999/metadata/musicbee",
            new { metadata = new { composer = "Hans Zimmer" } });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Contribute_ReservedSourceKey_Returns400()
    {
        var client = await AdminClientAsync();
        var id = await CreateMediaItemAsync(client, nameof(Contribute_ReservedSourceKey_Returns400));

        var resp = await client.PostAsJsonAsync($"/api/v1/media/{id}/metadata/_resolved",
            new { metadata = new { composer = "Hans Zimmer" } });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Contribute_EmptyPayload_Returns400()
    {
        var client = await AdminClientAsync();
        var id = await CreateMediaItemAsync(client, nameof(Contribute_EmptyPayload_Returns400));

        var resp = await client.PostAsJsonAsync($"/api/v1/media/{id}/metadata/musicbee",
            new { metadata = new { } });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Merge behaviour (lossless ingestion) ────────────────────────────────

    [Fact]
    public async Task Contribute_NewField_MergesIntoResolvedAndOtherSourcesUntouched()
    {
        var client = await AdminClientAsync();
        var id = await CreateMediaItemAsync(client, nameof(Contribute_NewField_MergesIntoResolvedAndOtherSourcesUntouched));

        // Seed a pre-existing source partition first — verifies a later contribution from a
        // DIFFERENT source doesn't clobber it.
        await client.PostAsJsonAsync($"/api/v1/media/{id}/metadata/chronicle.plugin.musicbrainz",
            new { metadata = new { title = "Interstellar Soundtrack" } });

        var resp = await client.PostAsJsonAsync($"/api/v1/media/{id}/metadata/musicbee",
            new { metadata = new { composer = "Hans Zimmer" } });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("data");
        body.GetProperty("tagMismatchDetected").GetBoolean().Should().BeFalse(); // first-time field — nothing to disagree with

        var itemResp = await client.GetAsync($"/api/v1/media/{id}");
        var item = JsonDocument.Parse(await itemResp.Content.ReadAsStringAsync()).RootElement.GetProperty("data");

        item.GetProperty("resolvedMetadata").GetProperty("composer").GetString().Should().Be("Hans Zimmer");
        item.GetProperty("resolvedMetadata").GetProperty("title").GetString().Should().Be("Interstellar Soundtrack");

        var pluginMeta = item.GetProperty("pluginMetadata");
        pluginMeta.GetProperty("chronicle.plugin.musicbrainz").GetProperty("title").GetString().Should().Be("Interstellar Soundtrack");
        pluginMeta.GetProperty("musicbee").GetProperty("composer").GetString().Should().Be("Hans Zimmer");
    }

    // ── Tag-mismatch detection + toggle-gated re-match queueing ─────────────

    [Fact]
    public async Task Contribute_DisagreeingField_ToggleOff_DetectsMismatchButDoesNotQueue()
    {
        var client = await AdminClientAsync();
        await SetAutoRematchAsync(client, false);
        var id = await CreateMediaItemAsync(client, nameof(Contribute_DisagreeingField_ToggleOff_DetectsMismatchButDoesNotQueue));

        await client.PostAsJsonAsync($"/api/v1/media/{id}/metadata/chronicle.plugin.musicbrainz",
            new { metadata = new { composer = "Original Composer" } });

        var resp = await client.PostAsJsonAsync($"/api/v1/media/{id}/metadata/musicbee",
            new { metadata = new { composer = "Different Composer" } });

        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("data");
        body.GetProperty("tagMismatchDetected").GetBoolean().Should().BeTrue();
        body.GetProperty("rematchQueued").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Contribute_DisagreeingField_ToggleOn_DetectsMismatchAndQueues()
    {
        var client = await AdminClientAsync();
        await SetAutoRematchAsync(client, true);
        var id = await CreateMediaItemAsync(client, nameof(Contribute_DisagreeingField_ToggleOn_DetectsMismatchAndQueues));

        await client.PostAsJsonAsync($"/api/v1/media/{id}/metadata/chronicle.plugin.musicbrainz",
            new { metadata = new { composer = "Original Composer" } });

        var resp = await client.PostAsJsonAsync($"/api/v1/media/{id}/metadata/musicbee",
            new { metadata = new { composer = "Different Composer" } });

        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("data");
        body.GetProperty("tagMismatchDetected").GetBoolean().Should().BeTrue();
        body.GetProperty("rematchQueued").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Contribute_AgreeingField_NoMismatchDetected()
    {
        var client = await AdminClientAsync();
        await SetAutoRematchAsync(client, true);
        var id = await CreateMediaItemAsync(client, nameof(Contribute_AgreeingField_NoMismatchDetected));

        await client.PostAsJsonAsync($"/api/v1/media/{id}/metadata/chronicle.plugin.musicbrainz",
            new { metadata = new { composer = "Hans Zimmer" } });

        var resp = await client.PostAsJsonAsync($"/api/v1/media/{id}/metadata/musicbee",
            new { metadata = new { composer = "Hans Zimmer" } });

        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("data");
        body.GetProperty("tagMismatchDetected").GetBoolean().Should().BeFalse();
        body.GetProperty("rematchQueued").GetBoolean().Should().BeFalse();
    }

    // ── File-identity fingerprint ────────────────────────────────────────────

    [Fact]
    public async Task Contribute_WithFileSnapshot_FirstReportsChanged_RepeatDoesNot()
    {
        var client = await AdminClientAsync();
        var id = await CreateMediaItemAsync(client, nameof(Contribute_WithFileSnapshot_FirstReportsChanged_RepeatDoesNot));

        var payload = new
        {
            metadata = new { composer = "Hans Zimmer" },
            file = new
            {
                sizeBytes = 48213112L, modifiedUtc = "2026-07-09T21:14:00Z",
                bitrateKbps = 320, sampleRateHz = 44100, durationSeconds = 245, fileType = "mp3"
            }
        };

        var first = await client.PostAsJsonAsync($"/api/v1/media/{id}/metadata/musicbee", payload);
        JsonDocument.Parse(await first.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("fingerprintChanged").GetBoolean().Should().BeTrue();

        var second = await client.PostAsJsonAsync($"/api/v1/media/{id}/metadata/musicbee", payload);
        JsonDocument.Parse(await second.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("fingerprintChanged").GetBoolean().Should().BeFalse();

        var itemResp = await client.GetAsync($"/api/v1/media/{id}");
        var fs = JsonDocument.Parse(await itemResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("fileScannerMeta");
        fs.GetProperty("bitrateKbps").GetInt32().Should().Be(320);
        fs.GetProperty("fileType").GetString().Should().Be("mp3");
    }
}
