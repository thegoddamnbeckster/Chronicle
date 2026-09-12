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
/// Root-caused live (2026-09-12): a real household show (Star Trek: Strange New Worlds) was
/// stuck at exactly 5 of 38 episodes no matter how many times it was rescanned, restarted, or
/// removed-and-rescanned from Kodi's library. Once Chronicle's own write_nfo feature had
/// written a sidecar .nfo next to EVERY episode file, Kodi began firing its episode-level
/// "NfoUrl" action for each one -- and unlike a failed SHOW-level NfoUrl (which Kodi tolerates,
/// falling back to its own normal find/getepisodelist flow), a failed EPISODE-level NfoUrl call
/// made Kodi abandon the REST of that show's episode scan entirely, confirmed live via kodi.log
/// (the scan never proceeded past the first episode's failed NfoUrl to a normal
/// getepisodelist/getepisodedetails pass for that show at all). This endpoint is the fix: lets
/// the addon resolve an episode directly from its own NFO's external id instead of giving up.
///
/// Unlike ResolveShowByExternalId, episode-level ids are not known to reliably land in
/// MediaExternalIds at all (episodes created via ScraperController.EnsureEpisodesResolvedAsync's
/// lightweight stub-creation path never get a row there -- see StampProviderPartition), so every
/// source here falls back to the MetadataJson text search, not just imdb/tvdb/trakt the way the
/// show endpoint does.
/// </summary>
public class ScraperResolveEpisodeByExternalIdTests : IClassFixture<ChronicleApiFactory>
{
    private readonly ChronicleApiFactory _factory;

    public ScraperResolveEpisodeByExternalIdTests(ChronicleApiFactory factory)
    {
        factory.SeedDatabase();
        _factory = factory;
    }

    private int EnsureTvType()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var existing = db.MediaTypes.FirstOrDefault(t => t.Name == "tv");
        if (existing is not null) return existing.Id;
        var mt = new MediaType
        {
            Name = "tv", DisplayName = "TV Shows", HierarchyLevels = 3,
            InteractionVerb = "watched", ProgressUnit = "minutes",
            IsBuiltIn = false, IsActive = true, CreatedAt = DateTime.UtcNow,
        };
        db.MediaTypes.Add(mt);
        db.SaveChanges();
        return mt.Id;
    }

    /// Seeds show -> season -> episode with the episode's external id embedded in a provider
    /// partition's extendedData -- the realistic shape (see StampProviderPartition), never a
    /// real MediaExternalId row.
    private int SeedEpisodeWithEmbeddedExternalId(
        string showName, int seasonNumber, int episodeNumber, string episodeTitle,
        string embeddedIdKey, string externalId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var mediaTypeId = EnsureTvType();

        var show = new MediaItem
        {
            MediaTypeId = mediaTypeId, Name = showName, HierarchyLevel = 0,
            NormalizedName = MediaItemNormalizer.NormalizeName(showName),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.MediaItems.Add(show);
        db.SaveChanges();

        var season = new MediaItem
        {
            MediaTypeId = mediaTypeId, Name = $"Season {seasonNumber}", ParentId = show.Id,
            HierarchyLevel = 1, Number = seasonNumber,
            NormalizedName = MediaItemNormalizer.NormalizeName($"Season {seasonNumber}"),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.MediaItems.Add(season);
        db.SaveChanges();

        var metadataJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["chronicle.plugin.tmdb"] = new Dictionary<string, object>
            {
                ["extendedData"] = new Dictionary<string, object>
                {
                    ["ids"] = new Dictionary<string, string> { [embeddedIdKey] = externalId },
                },
            },
        });
        var episode = new MediaItem
        {
            MediaTypeId = mediaTypeId, Name = episodeTitle, ParentId = season.Id,
            HierarchyLevel = 2, Number = episodeNumber, MetadataJson = metadataJson,
            NormalizedName = MediaItemNormalizer.NormalizeName(episodeTitle),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.MediaItems.Add(episode);
        db.SaveChanges();
        return episode.Id;
    }

    private async Task<HttpClient> AuthClientAsync()
    {
        var client = _factory.CreateClient();
        var username = $"scraper_epext_{Guid.NewGuid():N}";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { username, password = "Password123!" });
        var token = System.Text.Json.JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task ResolveEpisode_TmdbEmbeddedInProviderExtendedData_ReturnsItsChronicleId()
    {
        var episodeId = SeedEpisodeWithEmbeddedExternalId(
            "Resolve Episode Probe Show", seasonNumber: 4, episodeNumber: 6, "Off-Hour",
            "tmdb", "9999001");

        var client = await AuthClientAsync();
        var resp = await client.GetAsync(
            "/api/v1/scraper/tv/resolve-episode-by-external-id?source=tmdb&externalId=9999001");

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain($"\"id\":{episodeId}");
        body.Should().Contain("\"season\":4");
        body.Should().Contain("\"episode\":6");
    }

    [Fact]
    public async Task ResolveEpisode_SourceIsCaseInsensitive()
    {
        var episodeId = SeedEpisodeWithEmbeddedExternalId(
            "Resolve Episode Probe Case", seasonNumber: 1, episodeNumber: 1, "Pilot",
            "tmdb", "9999002");

        var client = await AuthClientAsync();
        var resp = await client.GetAsync(
            "/api/v1/scraper/tv/resolve-episode-by-external-id?source=TMDB&externalId=9999002");

        resp.EnsureSuccessStatusCode();
        (await resp.Content.ReadAsStringAsync()).Should().Contain($"\"id\":{episodeId}");
    }

    [Fact]
    public async Task ResolveEpisode_NoMatchingExternalId_ReturnsNotFound()
    {
        EnsureTvType();
        var client = await AuthClientAsync();

        var resp = await client.GetAsync(
            "/api/v1/scraper/tv/resolve-episode-by-external-id?source=tmdb&externalId=nonexistent");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ResolveEpisode_MatchIsAShowNotAnEpisode_ReturnsNotFound()
    {
        // A show-level item whose MetadataJson happens to contain the same substring (e.g. a
        // shared tmdb series id) must never be returned in place of an actual episode -- the
        // HierarchyLevel == 2 check is what enforces that.
        var tvTypeId = EnsureTvType();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var metadataJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["chronicle.plugin.tmdb"] = new Dictionary<string, object>
                {
                    ["extendedData"] = new Dictionary<string, object>
                    {
                        ["ids"] = new Dictionary<string, string> { ["tmdb"] = "9999003" },
                    },
                },
            });
            db.MediaItems.Add(new MediaItem
            {
                MediaTypeId = tvTypeId, Name = "Show Not Episode", HierarchyLevel = 0,
                MetadataJson = metadataJson,
                NormalizedName = MediaItemNormalizer.NormalizeName("Show Not Episode"),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }

        var client = await AuthClientAsync();
        var resp = await client.GetAsync(
            "/api/v1/scraper/tv/resolve-episode-by-external-id?source=tmdb&externalId=9999003");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ResolveEpisode_MissingQueryParams_ReturnsBadRequest()
    {
        var client = await AuthClientAsync();

        var resp = await client.GetAsync("/api/v1/scraper/tv/resolve-episode-by-external-id?source=tmdb");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
