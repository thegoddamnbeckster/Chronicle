using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Tests.Integration;

/// <summary>
/// GET /api/v1/scraper/arr-compare: asks the managing application (Sonarr here) about every show Chronicle holds and reports which ids
/// agree, which differ and which the application does not have, without creating or changing anything.
/// </summary>
public class ScraperArrCompareTests : IClassFixture<EpisodeProviderTestFactory>
{
    private readonly EpisodeProviderTestFactory _factory;

    public ScraperArrCompareTests(EpisodeProviderTestFactory factory)
    {
        factory.SeedDatabase();
        _factory = factory;
    }

    private sealed class FakeSonarr(Dictionary<string, LocatedMedia> byTitle) : IMediaLocator
    {
        public string PluginId => "chronicle.plugin.fakearr";
        public string Name => "Fake Arr";
        public string Version => "1.0.0";
        public PluginSettingsSchema GetSettingsSchema() => new();
        public void Configure(IReadOnlyDictionary<string, string> settings) { }
        public bool Handles(LocatorFamily family) => family == LocatorFamily.Tv;
        public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<LocatedMedia?> LocateAsync(LocatorFamily family, LocatorQuery query, CancellationToken ct = default) =>
            Task.FromResult(byTitle.GetValueOrDefault(query.Title));
    }

    private int SeedShow(string name, string tmdbId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var show = new MediaItem
        {
            MediaTypeId = 1, Name = name, HierarchyLevel = 0, NormalizedName = MediaItemNormalizer.NormalizeName(name),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.MediaItems.Add(show);
        db.SaveChanges();
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = show.Id, Source = "tmdb", ExternalId = tmdbId });
        db.SaveChanges();
        return show.Id;
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = _factory.CreateClient();
        var username = $"arr_compare_{Guid.NewGuid():N}";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register", new { username, password = "Password123!" });
        var token = JsonDocument.Parse(await reg.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("token").GetString()!;
        await _factory.SetAdminAsync(username);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task ReportsWhichShowsAgreeWithTheArrApp_WhichDiffer_AndWhichItDoesNotHave_AndChangesNothing()
    {
        SeedShow("Alpha", "tv:1");
        SeedShow("Beta", "tv:2");
        SeedShow("Gamma", "tv:3");
        _factory.Registry.Locators.Clear();
        _factory.Registry.Locators.Add(new FakeSonarr(new()
        {
            ["Alpha"] = new LocatedMedia("Alpha", null, new Dictionary<string, string> { ["tmdb"] = "tv:1" }),
            ["Beta"]  = new LocatedMedia("Beta",  null, new Dictionary<string, string> { ["tmdb"] = "tv:999" }),
        }));
        var client = await AdminClientAsync();

        var response = await client.GetAsync("/api/v1/scraper/arr-compare?family=tv");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("data");
        data.GetProperty("confirmed").GetInt32().Should().Be(1);
        data.GetProperty("differ").GetInt32().Should().Be(1);
        data.GetProperty("notInArr").GetInt32().Should().Be(1);
        data.GetProperty("conflicts")[0].GetProperty("name").GetString().Should().Be("Beta");
        data.GetProperty("missing")[0].GetProperty("name").GetString().Should().Be("Gamma");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        db.MediaItems.Count(m => m.HierarchyLevel == 0).Should().Be(3, "the comparison must never create a show");
        db.MediaExternalIds.Count(e => e.ExternalId == "tv:2").Should().Be(1, "Chronicle's own id must be left as it was");
    }

    [Fact]
    public async Task IsForAdministratorsOnly()
    {
        var client = _factory.CreateClient();
        var username = $"arr_compare_plain_{Guid.NewGuid():N}";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register", new { username, password = "Password123!" });
        var token = JsonDocument.Parse(await reg.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("token").GetString()!;
        await _factory.SetAdminAsync(username, admin: false);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        (await client.GetAsync("/api/v1/scraper/arr-compare?family=tv")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
