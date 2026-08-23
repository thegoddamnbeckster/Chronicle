using System.Net.Http.Json;
using System.Text.Json;
using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Tests.Integration;

/// <summary>
/// Covers two code-review fixes to ScraperController's artwork/details assembly:
///   - a malformed MetadataJson blob on a movie, show, or season must degrade to an empty
///     result for that item rather than 500 the whole getdetails/getepisodelist response
///     (BuildMovieDetails/BuildShowDetails/BuildSeasonDetails all parse via the same
///     ParseMetadataOrEmpty helper now)
///   - CollectEpisodeArtwork's poster-&gt;thumb re-key must merge into any pre-existing
///     "thumb" candidate list rather than silently discard it
/// </summary>
public class ScraperArtworkTests : IClassFixture<ChronicleApiFactory>
{
    private readonly ChronicleApiFactory _factory;

    public ScraperArtworkTests(ChronicleApiFactory factory)
    {
        factory.SeedDatabase();
        _factory = factory;
    }

    private int TvTypeId()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        return db.MediaTypes.First(t => t.Name == "tv").Id;
    }

    private int MoviesTypeId()
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

    private async Task<HttpClient> AuthClientAsync()
    {
        var client = _factory.CreateClient();
        var username = $"artwork_{Guid.NewGuid():N}";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { username, password = "Password123!" });
        var token = JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // ── Malformed MetadataJson must not crash the response ─────────────────

    [Fact]
    public async Task MovieDetails_MalformedMetadata_DoesNotBreakTheResponse()
    {
        var typeId = MoviesTypeId();
        int movieId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var movie = new MediaItem
            {
                MediaTypeId = typeId, Name = "Broken Movie", HierarchyLevel = 0,
                NormalizedName = MediaItemNormalizer.NormalizeName("Broken Movie"),
                MetadataJson = "{ not valid json",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.MediaItems.Add(movie);
            db.SaveChanges();
            movieId = movie.Id;
        }

        var client = await AuthClientAsync();
        var resp = await client.GetAsync($"/api/v1/scraper/movies/details?id={movieId}");

        resp.EnsureSuccessStatusCode();
        var data = JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data");
        data.GetProperty("title").GetString().Should().Be("Broken Movie");
    }

    [Fact]
    public async Task ShowDetails_MalformedShowMetadata_DoesNotBreakTheResponse()
    {
        var typeId = TvTypeId();
        int showId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var show = new MediaItem
            {
                MediaTypeId = typeId, Name = "Broken Show", HierarchyLevel = 0,
                NormalizedName = MediaItemNormalizer.NormalizeName("Broken Show"),
                MetadataJson = "{ not valid json",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.MediaItems.Add(show);
            db.SaveChanges();
            showId = show.Id;
        }

        var client = await AuthClientAsync();
        var resp = await client.GetAsync($"/api/v1/scraper/tv/details?id={showId}");

        resp.EnsureSuccessStatusCode();
        var data = JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data");
        data.GetProperty("title").GetString().Should().Be("Broken Show");
    }

    [Fact]
    public async Task ShowDetails_MalformedSeasonMetadata_DoesNotBreakTheResponse()
    {
        // The exact code-review finding: one bad season's MetadataJson must not 500 the
        // whole tv/details call for an otherwise-healthy show.
        var typeId = TvTypeId();
        int showId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var show = new MediaItem
            {
                MediaTypeId = typeId, Name = "Show With Broken Season", HierarchyLevel = 0,
                NormalizedName = MediaItemNormalizer.NormalizeName("Show With Broken Season"),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.MediaItems.Add(show);
            db.SaveChanges();
            showId = show.Id;

            var season = new MediaItem
            {
                MediaTypeId = typeId, Name = "Season 1", Number = 1, HierarchyLevel = 1,
                ParentId = showId,
                NormalizedName = MediaItemNormalizer.NormalizeName("Season 1"),
                MetadataJson = "{ not valid json",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.MediaItems.Add(season);
            db.SaveChanges();
        }

        var client = await AuthClientAsync();
        var resp = await client.GetAsync($"/api/v1/scraper/tv/details?id={showId}");

        resp.EnsureSuccessStatusCode();
        var data = JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data");
        data.GetProperty("title").GetString().Should().Be("Show With Broken Season");
        var seasons = data.GetProperty("seasons").EnumerateArray().ToList();
        seasons.Should().HaveCount(1);
        seasons[0].GetProperty("number").GetInt32().Should().Be(1);
    }

    // ── Episode poster→thumb re-key must merge, not overwrite ──────────────

    [Fact]
    public async Task EpisodeDetails_PosterRekeyedToThumb_MergesWithExistingThumbCandidates()
    {
        // A provider can already supply real per-episode "thumb" candidates via
        // additionalImages; CollectEpisodeArtwork's re-key of the generic "poster" bucket
        // into "thumb" (Kodi's only recognised episode art type) must add to that list,
        // not silently replace it.
        var typeId = TvTypeId();
        int episodeId;
        const string metadataJson = """
            {
              "chronicle.plugin.tmdb": {
                "source": "tmdb",
                "posterUrl": "https://img/episode-poster.jpg",
                "additionalImages": [
                  { "type": "thumb", "url": "https://img/existing-thumb.jpg" }
                ]
              }
            }
            """;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var show = new MediaItem
            {
                MediaTypeId = typeId, Name = "Thumb Merge Show", HierarchyLevel = 0,
                NormalizedName = MediaItemNormalizer.NormalizeName("Thumb Merge Show"),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.MediaItems.Add(show);
            db.SaveChanges();

            var season = new MediaItem
            {
                MediaTypeId = typeId, Name = "Season 1", Number = 1, HierarchyLevel = 1,
                ParentId = show.Id,
                NormalizedName = MediaItemNormalizer.NormalizeName("Season 1"),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.MediaItems.Add(season);
            db.SaveChanges();

            var episode = new MediaItem
            {
                MediaTypeId = typeId, Name = "Episode 1", Number = 1, HierarchyLevel = 2,
                ParentId = season.Id,
                NormalizedName = MediaItemNormalizer.NormalizeName("Episode 1"),
                MetadataJson = metadataJson,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.MediaItems.Add(episode);
            db.SaveChanges();
            episodeId = episode.Id;
        }

        var client = await AuthClientAsync();
        var resp = await client.GetAsync($"/api/v1/scraper/tv/episode-details?id={episodeId}");

        resp.EnsureSuccessStatusCode();
        var data = JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data");

        var thumbs = data.GetProperty("artwork").GetProperty("thumb")
            .EnumerateArray().Select(e => e.GetProperty("url").GetString()).ToList();

        thumbs.Should().BeEquivalentTo(
            ["https://img/existing-thumb.jpg", "https://img/episode-poster.jpg"]);
        data.GetProperty("artwork").TryGetProperty("poster", out _).Should().BeFalse();
    }
}
