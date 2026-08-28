using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>
    /// Regression test for a real reported bug: a canonical field ("directors") was renamed to
    /// "crew" (commit b86fa5f), but existing saved config still had a "directors" entry under
    /// "movies"/"tv" — never migrated. Because PutMetadataAssignment always receives (and
    /// GetMetadataAssignment always returns) the FULL stored config, the stale field made every
    /// future save fail with "Field 'directors' is not assignable for media type 'tv'",
    /// regardless of what the user actually changed or which media type/section they were in.
    ///
    /// This seeds that exact scenario directly against the DB (bypassing PUT's own validation,
    /// which would rightly reject "directors" if it went through the normal save path — the
    /// point is to simulate data that predates a field rename, not to test PUT's validation
    /// again) and asserts: (1) GET filters the stale field out before the frontend ever sees it,
    /// and (2) a subsequent save — built the same way the frontend builds one, from the GET
    /// response — succeeds and the stored config comes out permanently clean, with no separate
    /// migration step required.
    /// </summary>
    [Fact]
    public async Task MetadataAssignment_StaleFieldFromPriorRename_FilteredOnReadAndSelfHealsOnSave()
    {
        var client = await AdminClientAsync();

        // Seed stale legacy config directly — "directors" alongside the still-valid "title".
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var staleJson = JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, string[]>>
            {
                ["movies"] = new()
                {
                    ["title"] = ["chronicle.plugin.tmdb"],
                    ["directors"] = ["chronicle.plugin.trakt", "chronicle.plugin.tmdb"],
                },
            });

            var existing = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "metadata_assignment.config");
            if (existing is null)
                db.AppSettings.Add(new AppSetting { Key = "metadata_assignment.config", Value = staleJson });
            else
                existing.Value = staleJson;
            await db.SaveChangesAsync();
        }

        // GET must filter the stale field out — the frontend must never see it.
        var getResp = await client.GetAsync("/api/v1/settings/metadata-assignment");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);

        var body = await getResp.Content.ReadFromJsonAsync<JsonElement>();
        var movies = body.GetProperty("data").GetProperty("assignments").GetProperty("movies");

        movies.TryGetProperty("directors", out _).Should().BeFalse("the stale field must be filtered before reaching the frontend");
        movies.GetProperty("title")[0].GetString().Should().Be("chronicle.plugin.tmdb", "the still-valid field must survive filtering");

        // A save built from that (now-clean) GET response — exactly how the frontend's full-
        // config spread works — must succeed. Before the fix, this failed for ANY save, on ANY
        // media type, anywhere on the page, as long as the stale field sat in stored config.
        var next = new Dictionary<string, object>
        {
            ["movies"] = new Dictionary<string, string[]> { ["title"] = ["chronicle.plugin.trakt"] },
        };
        var putResp = await client.PutAsJsonAsync("/api/v1/settings/metadata-assignment", new { assignments = next });
        putResp.StatusCode.Should().Be(HttpStatusCode.OK, "a save built from the filtered GET response must not resurrect the stale field");

        // And the stored config is now permanently clean — no migration step needed.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var stored = await db.AppSettings.FirstAsync(s => s.Key == "metadata_assignment.config");
            stored.Value.Should().NotContain("directors");
        }
    }
}
