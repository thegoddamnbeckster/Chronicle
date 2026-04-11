using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Chronicle.Tests.Integration;

public class SettingsTests : IClassFixture<ChronicleApiFactory>
{
    private readonly ChronicleApiFactory _factory;

    private const string AdminUser = "settings_admin_fixture";
    private const string AdminPass = "Password123!";

    public SettingsTests(ChronicleApiFactory factory)
    {
        factory.SeedDatabase();
        _factory = factory;
        EnsureAdminRegistered(factory).GetAwaiter().GetResult();
    }

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

    [Fact]
    public async Task MetadataAssignment_RoundTrip_SavesAndReturnsConfig()
    {
        var client = await AdminClientAsync();

        var config = new
        {
            assignments = new
            {
                movies = new { title = new[] { "chronicle.plugin.tmdb" } }
            }
        };

        var putResp = await client.PutAsJsonAsync("/api/v1/settings/metadata-assignment", config);
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        var getResp = await client.GetAsync("/api/v1/settings/metadata-assignment");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);

        var body = await getResp.Content.ReadFromJsonAsync<JsonElement>();
        var titlePlugins = body
            .GetProperty("data")
            .GetProperty("assignments")
            .GetProperty("movies")
            .GetProperty("title");

        Assert.Equal("chronicle.plugin.tmdb", titlePlugins[0].GetString());
    }

    [Fact]
    public async Task MetadataAssignment_Get_ReturnsAssignableFieldsAndPlugins()
    {
        var client = await AdminClientAsync();

        var getResp = await client.GetAsync("/api/v1/settings/metadata-assignment");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);

        var body = await getResp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("success").GetBoolean().Should().BeTrue();

        var data = body.GetProperty("data");
        data.TryGetProperty("assignments", out _).Should().BeTrue();
        data.TryGetProperty("assignableFields", out var fields).Should().BeTrue();
        data.TryGetProperty("availablePlugins", out _).Should().BeTrue();

        // movies should have title in assignable fields
        fields.GetProperty("movies").EnumerateArray().Select(e => e.GetString())
            .Should().Contain("title");
    }

    [Fact]
    public async Task MetadataAssignment_Put_RejectsBadMediaType()
    {
        var client = await AdminClientAsync();
        var config = new { assignments = new { unknown_type = new { title = new[] { "tmdb" } } } };
        var resp = await client.PutAsJsonAsync("/api/v1/settings/metadata-assignment", config);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task MetadataAssignment_Put_RejectsBadFieldName()
    {
        var client = await AdminClientAsync();
        var config = new { assignments = new { movies = new { not_a_field = new[] { "tmdb" } } } };
        var resp = await client.PutAsJsonAsync("/api/v1/settings/metadata-assignment", config);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task MetadataAssignment_Put_RequiresAdmin()
    {
        // Register a non-admin user (second+ registration = regular user)
        var client = _factory.CreateClient();
        var username = $"nonadmin_{Guid.NewGuid():N}";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { username, password = "Password123!" });
        var token = JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var config = new { assignments = new { movies = new { title = new[] { "chronicle.plugin.tmdb" } } } };
        var putResp = await client.PutAsJsonAsync("/api/v1/settings/metadata-assignment", config);

        putResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
