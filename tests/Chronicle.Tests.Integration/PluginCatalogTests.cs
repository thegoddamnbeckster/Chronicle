using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Chronicle.Tests.Integration;

/// <summary>
/// The plugin catalog (Plugins page → "+ Install Plugin") is a hand-maintained static list in
/// PluginsController -- nothing keeps it in sync with the plugins actually shipped as sibling
/// Chronicle.Plugin.* repos, so it silently goes stale whenever a new one is added and nobody
/// remembers to add a matching catalog entry. Reported by the user: the catalog only showed
/// TMDB/MusicBrainz/File Scanner despite ~12 plugins existing. This test is a tripwire against
/// the same staleness recurring -- it doesn't prevent a forgotten entry, but it does force
/// whoever adds a plugin (or notices the count drift) to update this list too.
/// </summary>
public class PluginCatalogTests : IClassFixture<ChronicleApiFactory>
{
    private readonly ChronicleApiFactory _factory;

    private const string AdminUser = "catalog_admin_fixture";
    private const string AdminPass = "Password123!";

    // Every plugin_id declared by a sibling Chronicle.Plugin.* repo's manifest.json as of
    // 2026-08-22. If this list and the catalog drift apart again, update both together.
    private static readonly string[] ExpectedPluginIds =
    [
        "chronicle.plugin.tmdb",
        "chronicle.plugin.musicbrainz",
        "chronicle.plugin.filescanner",
        "chronicle.plugin.fanedit",
        "chronicle.plugin.fanarttv",
        "hardcover",
        "chronicle.plugin.moviesremastered",
        "chronicle.plugin.simkl",
        "chronicle.plugin.thetvdb",
        "chronicle.plugin.trakt",
        "chronicle.plugin.tvmaze",
        "chronicle.plugin.themes.default",
    ];

    public PluginCatalogTests(ChronicleApiFactory factory)
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
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Catalog_IncludesEveryKnownPlugin()
    {
        var client = await AdminClientAsync();

        var resp = await client.GetAsync("/api/v1/plugins/catalog");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var entries = JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").EnumerateArray().ToList();

        var catalogIds = entries.Select(e => e.GetProperty("pluginId").GetString()).ToList();
        catalogIds.Should().Contain(ExpectedPluginIds);
    }

    [Fact]
    public async Task Catalog_EveryEntryHasTheFieldsInstallNeeds()
    {
        var client = await AdminClientAsync();

        var resp = await client.GetAsync("/api/v1/plugins/catalog");
        var entries = JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").EnumerateArray().ToList();

        entries.Should().NotBeEmpty();
        foreach (var entry in entries)
        {
            entry.GetProperty("githubRepo").GetString().Should().NotBeNullOrWhiteSpace();
            entry.GetProperty("assetName").GetString().Should().NotBeNullOrWhiteSpace();
            entry.GetProperty("dllName").GetString().Should().NotBeNullOrWhiteSpace();
            entry.GetProperty("name").GetString().Should().NotBeNullOrWhiteSpace();
            entry.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
        }
    }
}
