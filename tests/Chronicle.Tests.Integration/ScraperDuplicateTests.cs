using System.Net.Http.Json;
using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Tests.Integration;

/// <summary>
/// Kodi scrapes fan edits and anime movies through the SAME /scraper/movies/search endpoint it
/// uses for ordinary movies (they're all just movie files on disk), and anime series through
/// /scraper/tv/search. Before this was fixed, those lookups only searched their own primary
/// media type, so an item the user already had filed as a fan edit was never found — and a
/// duplicate "movies" copy was minted on every scrape. Confirmed in the wild 2026-08-07.
/// </summary>
public class ScraperDuplicateTests : IClassFixture<ChronicleApiFactory>
{
    private readonly ChronicleApiFactory _factory;

    public ScraperDuplicateTests(ChronicleApiFactory factory)
    {
        factory.SeedDatabase();
        _factory = factory;
    }

    private (int MovieTypeId, int FanEditTypeId, int AnimeMovieTypeId, int TvTypeId, int AnimeTypeId) EnsureTypes()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        int Ensure(string name, string display, int levels)
        {
            var existing = db.MediaTypes.FirstOrDefault(t => t.Name == name);
            if (existing is not null) return existing.Id;
            var mt = new MediaType
            {
                Name = name, DisplayName = display, HierarchyLevels = levels,
                InteractionVerb = "watched", ProgressUnit = "minutes",
                IsBuiltIn = false, IsActive = true, CreatedAt = DateTime.UtcNow,
            };
            db.MediaTypes.Add(mt);
            db.SaveChanges();
            return mt.Id;
        }

        return (
            Ensure("movies", "Movies", 1),
            Ensure("fanedits", "Fan Edits", 1),
            Ensure("anime_movies", "Anime Movies", 1),
            Ensure("tv", "TV Shows", 3),
            Ensure("anime", "Anime", 3));
    }

    private int SeedItem(int mediaTypeId, string name, int? year, int hierarchyLevel = 0)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var item = new MediaItem
        {
            MediaTypeId = mediaTypeId, Name = name, Year = year,
            HierarchyLevel = hierarchyLevel,
            NormalizedName = MediaItemNormalizer.NormalizeName(name),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.MediaItems.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    /// Scraper endpoints sit behind the default authorize policy (Kodi calls them with an API
    /// key; a JWT works equally well since the policy accepts either scheme).
    private async Task<HttpClient> AuthClientAsync()
    {
        var client = _factory.CreateClient();
        var username = $"scraper_{Guid.NewGuid():N}";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { username, password = "Password123!" });
        var token = System.Text.Json.JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private int CountByName(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        return db.MediaItems.Count(m => m.Name == name);
    }

    [Fact]
    public async Task MovieSearch_TitleAlreadyExistsAsFanEdit_ReturnsItInsteadOfCreatingADuplicate()
    {
        var types = EnsureTypes();
        const string title = "Scraper Dup FanEdit Probe";
        var fanEditId = SeedItem(types.FanEditTypeId, title, 1999);

        var client = await AuthClientAsync();
        var resp = await client.GetAsync($"/api/v1/scraper/movies/search?title={Uri.EscapeDataString(title)}&year=1999");

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain($"\"id\":{fanEditId}",
            "the existing fan edit must be returned, not a fresh movies-type copy");
        CountByName(title).Should().Be(1, "no duplicate may be created");
    }

    [Fact]
    public async Task MovieSearch_TitleAlreadyExistsAsAnimeMovie_DoesNotCreateADuplicate()
    {
        var types = EnsureTypes();
        const string title = "Scraper Dup AnimeMovie Probe";
        var animeMovieId = SeedItem(types.AnimeMovieTypeId, title, 2015);

        var client = await AuthClientAsync();
        var resp = await client.GetAsync($"/api/v1/scraper/movies/search?title={Uri.EscapeDataString(title)}&year=2015");

        resp.EnsureSuccessStatusCode();
        (await resp.Content.ReadAsStringAsync()).Should().Contain($"\"id\":{animeMovieId}");
        CountByName(title).Should().Be(1);
    }

    [Fact]
    public async Task ShowSearch_TitleAlreadyExistsAsAnime_DoesNotCreateADuplicate()
    {
        var types = EnsureTypes();
        const string title = "Scraper Dup AnimeShow Probe";
        var animeId = SeedItem(types.AnimeTypeId, title, 2011);

        var client = await AuthClientAsync();
        var resp = await client.GetAsync($"/api/v1/scraper/tv/search?title={Uri.EscapeDataString(title)}&year=2011");

        resp.EnsureSuccessStatusCode();
        (await resp.Content.ReadAsStringAsync()).Should().Contain($"\"id\":{animeId}");
        CountByName(title).Should().Be(1);
    }

    [Fact]
    public async Task MovieSearch_RepeatedScrapesOfTheSameTitle_StayAtOneItem()
    {
        // The reported symptom was duplicates accumulating over repeated scrapes, not just one.
        var types = EnsureTypes();
        const string title = "Scraper Dup Repeat Probe";
        SeedItem(types.FanEditTypeId, title, 2007);

        var client = await AuthClientAsync();
        for (var i = 0; i < 3; i++)
        {
            var resp = await client.GetAsync($"/api/v1/scraper/movies/search?title={Uri.EscapeDataString(title)}&year=2007");
            resp.EnsureSuccessStatusCode();
        }

        CountByName(title).Should().Be(1, "repeat scrapes must be idempotent");
    }
}
