using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Chronicle.Tests.Integration;

/// <summary>
/// HTTP-layer tests for GET /scraper/kodi-refresh-signal. The underlying matching logic (does
/// an item's own change since a device's last scrape make it "due") is covered thoroughly at
/// the service layer by KodiDeviceServiceTests; these confirm the endpoint itself is reachable,
/// versioned, validated, and correctly resolves the caller via a real X-API-Key (GetApiTokenId()
/// only ever resolves that claim, never a bearer JWT -- see ScraperEndpointsAsApiKeyCaller_*
/// below for why a JWT-only caller intentionally gets the documented empty "nothing to do"
/// shape instead of an error).
/// </summary>
public class KodiRefreshSignalTests : IClassFixture<ChronicleApiFactory>
{
    private readonly ChronicleApiFactory _factory;

    public KodiRefreshSignalTests(ChronicleApiFactory factory)
    {
        factory.SeedDatabase();
        _factory = factory;
    }

    private async Task<(HttpClient client, string token)> AuthClientAsync()
    {
        var client = _factory.CreateClient();
        var username = $"refresh_signal_{Guid.NewGuid():N}";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { username, password = "Password123!" });
        var token = JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, token);
    }

    private static async Task<string> CreateApiKeyAsync(HttpClient bearerClient, string name = "Kodi Scraper")
    {
        var resp = await bearerClient.PostAsJsonAsync("/api/v1/tokens", new { name });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("data");
        return data.GetProperty("token").GetString()!;
    }

    [Fact]
    public async Task GetRefreshSignal_JwtCaller_ReturnsEmptyItemsRatherThanAnError()
    {
        // GetApiTokenId() only resolves an API-key claim -- a bearer/JWT caller (a person using
        // the web app, not a Kodi addon) must get the same "nothing to do" empty shape every
        // other scraper endpoint gives a non-API-key caller, never an error.
        var (client, _) = await AuthClientAsync();

        var resp = await client.GetAsync("/api/v1/scraper/kodi-refresh-signal?kinds=movie");

        resp.EnsureSuccessStatusCode();
        (await resp.Content.ReadAsStringAsync()).Should().Contain("\"items\":[]");
    }

    [Fact]
    public async Task GetRefreshSignal_ApiKeyCallerMissingKinds_ReturnsBadRequest()
    {
        var (bearerClient, _) = await AuthClientAsync();
        var rawKey = await CreateApiKeyAsync(bearerClient);
        var apiKeyClient = _factory.CreateClient();
        apiKeyClient.DefaultRequestHeaders.Add("X-API-Key", rawKey);

        var resp = await apiKeyClient.GetAsync("/api/v1/scraper/kodi-refresh-signal");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetRefreshSignal_ApiKeyCallerWithNoRegisteredDevice_ReturnsEmptyItems()
    {
        // No POST to devices/kodi/register anywhere in this test -- "Allow remote control via
        // HTTP" is off, the same vanilla-install shape kodi-scan-signal already has to tolerate.
        var (bearerClient, _) = await AuthClientAsync();
        var rawKey = await CreateApiKeyAsync(bearerClient);
        var apiKeyClient = _factory.CreateClient();
        apiKeyClient.DefaultRequestHeaders.Add("X-API-Key", rawKey);

        var resp = await apiKeyClient.GetAsync("/api/v1/scraper/kodi-refresh-signal?kinds=movie");

        resp.EnsureSuccessStatusCode();
        (await resp.Content.ReadAsStringAsync()).Should().Contain("\"items\":[]");
    }
}
