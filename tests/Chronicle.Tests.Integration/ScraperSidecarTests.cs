using System.Net;
using System.Net.Http.Json;
using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Tests.Integration;

/// <summary>
/// ScraperController's sidecar-building endpoints (movies/sidecar, tv/sidecar,
/// tv/episode-sidecar) -- the Chronicle-side half of
/// docs/plans/2026-09-02-kodi-nfo-plugin-design.md's phased-rollout step 3. No
/// ISidecarFormatPlugin DLL is built into this repo's test output (the real implementation,
/// Chronicle.Plugin.Kodi.NFO, lives in a separate repo with its own build/tests), so
/// IPluginRegistry.GetSidecarFormatPlugins() is always empty in this integration test host --
/// these tests cover exactly that: routing, auth, and the graceful "no sidecar plugin
/// installed" response, end to end through the real ASP.NET Core pipeline. The
/// resolved-data-mapping and byte-building logic itself is covered by the plugin repo's own
/// KodiNfoBuilderTests.cs.
/// </summary>
public class ScraperSidecarTests : IClassFixture<ChronicleApiFactory>
{
    private readonly ChronicleApiFactory _factory;

    public ScraperSidecarTests(ChronicleApiFactory factory)
    {
        factory.SeedDatabase();
        _factory = factory;
    }

    private int EnsureMovieTypeId()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var existing = db.MediaTypes.FirstOrDefault(t => t.Name == "movies");
        if (existing is not null) return existing.Id;
        var mt = new MediaType
        {
            Name = "movies", DisplayName = "Movies", HierarchyLevels = 1,
            InteractionVerb = "watched", ProgressUnit = "minutes",
            IsBuiltIn = false, IsActive = true, CreatedAt = DateTime.UtcNow,
        };
        db.MediaTypes.Add(mt);
        db.SaveChanges();
        return mt.Id;
    }

    private int SeedMovie(string name, int? year)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var item = new MediaItem
        {
            MediaTypeId = EnsureMovieTypeId(), Name = name, Year = year,
            HierarchyLevel = 0,
            NormalizedName = MediaItemNormalizer.NormalizeName(name),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.MediaItems.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    /// See ScraperDuplicateTests.AuthClientAsync's own doc -- same auth setup, duplicated
    /// here rather than shared because these are two independent, self-contained test
    /// classes (matching the existing convention in this test project).
    private async Task<HttpClient> AuthClientAsync()
    {
        var client = _factory.CreateClient();
        var username = $"scraper_sidecar_{Guid.NewGuid():N}";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { username, password = "Password123!" });
        var token = System.Text.Json.JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task MovieSidecar_NoSidecarPluginInstalled_ReturnsNotFoundWithCode()
    {
        var id = SeedMovie("Scraper Sidecar Movie Probe", 2019);
        var client = await AuthClientAsync();

        var resp = await client.GetAsync($"/api/v1/scraper/movies/sidecar?id={id}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("\"code\":\"NO_SIDECAR_PLUGIN\"");
    }

    [Fact]
    public async Task ShowSidecar_NoSidecarPluginInstalled_ReturnsNotFoundWithCode()
    {
        var client = await AuthClientAsync();

        // Item id doesn't need to exist -- the plugin check runs first (see
        // ScraperController.ResolveSidecarPlugin's call order), so this proves the route and
        // the no-plugin branch work without needing a seeded show.
        var resp = await client.GetAsync("/api/v1/scraper/tv/sidecar?id=999999");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("\"code\":\"NO_SIDECAR_PLUGIN\"");
    }

    [Fact]
    public async Task EpisodeSidecar_NoSidecarPluginInstalled_ReturnsNotFoundWithCode()
    {
        var client = await AuthClientAsync();

        var resp = await client.GetAsync("/api/v1/scraper/tv/episode-sidecar?id=999999");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("\"code\":\"NO_SIDECAR_PLUGIN\"");
    }
}
