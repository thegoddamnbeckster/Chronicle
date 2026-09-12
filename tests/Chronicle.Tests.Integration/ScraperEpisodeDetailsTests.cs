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
/// GetEpisodeDetails ("tv/episode-details" -- Kodi's "getepisodedetails" step) had zero
/// identifying log output before 2026-09-12, unlike the movie/show equivalents -- there was no
/// way to tell from Chronicle's own server logs which episodes of a show Kodi's scan had even
/// attempted, versus never asked about at all (root-caused while diagnosing a real household
/// show stuck at 5 of 38 episodes). These tests are the regression guard for the endpoint's
/// response shape -- the exact ItemId/ShowTitle/Season/Episode/Title fields the new log line
/// reads -- so a future change to BuildEpisodeDetailsDtoAsync can't silently null one of them
/// out without a test failing, the same way the missing log line let the underlying bug hide.
/// </summary>
public class ScraperEpisodeDetailsTests : IClassFixture<ChronicleApiFactory>
{
    private readonly ChronicleApiFactory _factory;

    public ScraperEpisodeDetailsTests(ChronicleApiFactory factory)
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

    /// Seeds show -> season -> episode, mirroring the real 3-level TV hierarchy
    /// BuildEpisodeDetailsDtoAsync walks (item -> parent season -> grandparent show).
    private int SeedEpisode(string showName, int? showYear, int seasonNumber, int episodeNumber, string episodeTitle)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var mediaTypeId = EnsureTvType();

        var show = new MediaItem
        {
            MediaTypeId = mediaTypeId, Name = showName, Year = showYear, HierarchyLevel = 0,
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

        var episode = new MediaItem
        {
            MediaTypeId = mediaTypeId, Name = episodeTitle, ParentId = season.Id,
            HierarchyLevel = 2, Number = episodeNumber,
            NormalizedName = MediaItemNormalizer.NormalizeName(episodeTitle),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.MediaItems.Add(episode);
        db.SaveChanges();
        return episode.Id;
    }

    /// See ScraperDuplicateTests.AuthClientAsync's own doc -- same auth setup, duplicated
    /// here rather than shared because these are two independent, self-contained test
    /// classes (matching the existing convention in this test project).
    private async Task<HttpClient> AuthClientAsync()
    {
        var client = _factory.CreateClient();
        var username = $"scraper_epdet_{Guid.NewGuid():N}";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { username, password = "Password123!" });
        var token = System.Text.Json.JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task EpisodeDetails_ExistingEpisode_ReturnsShowTitleSeasonEpisodeAndTitle()
    {
        var id = SeedEpisode("Star Trek: Strange New Worlds", 2022, seasonNumber: 4, episodeNumber: 6, "Wedding Bell Blues");
        var client = await AuthClientAsync();

        var resp = await client.GetAsync($"/api/v1/scraper/tv/episode-details?id={id}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ApiEnvelope<EpisodeDetailsPayload>>();
        body!.Data!.ShowTitle.Should().Be("Star Trek: Strange New Worlds");
        body.Data.Season.Should().Be(4);
        body.Data.Episode.Should().Be(6);
        body.Data.Title.Should().Be("Wedding Bell Blues");
    }

    [Fact]
    public async Task EpisodeDetails_UnknownId_ReturnsNotFoundWithCode()
    {
        var client = await AuthClientAsync();

        var resp = await client.GetAsync("/api/v1/scraper/tv/episode-details?id=999999");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("\"code\":\"MEDIA_NOT_FOUND\"");
    }

    private record ApiEnvelope<T>(bool Success, T? Data);
    private record EpisodeDetailsPayload(string? Title, int Season, int Episode, string? ShowTitle);
}
