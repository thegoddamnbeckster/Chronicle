using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Chronicle.Tests.Integration;

public class SyncControllerTests : IClassFixture<ChronicleApiFactory>
{
    private readonly ChronicleApiFactory _factory;
    private const string AdminUser = "sync_admin_fixture";
    private const string AdminPass = "Password123!";

    public SyncControllerTests(ChronicleApiFactory factory)
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
    public async Task PostSync_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsync("/api/v1/sync/chronicle.plugin.trakt", null);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostSync_UnknownPlugin_Returns202WithJobId()
    {
        // Sync is fire-and-forget: the controller always returns 202 Accepted with a jobId.
        // Validation (plugin existence) is deferred to the background job.
        var client = await AdminClientAsync();
        var resp = await client.PostAsync("/api/v1/sync/chronicle.plugin.nonexistent?fullSync=true", null);
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("jobId");
    }
}
