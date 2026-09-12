using System.Net.Http.Json;
using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Tests.Integration;

/// <summary>
/// GET /api/v1/scraper/movies/collections -- every real collection Chronicle knows about, as
/// the same ScraperCollectionDto shape an individual movie's /movies/details response embeds.
/// Added (2026-09-12) so the movie addon can run its own periodic collection-art task instead
/// of only ever refreshing a collection's art as a side effect of some member movie happening
/// to get rescraped -- see GetAllCollectionsForArtSync's own doc. Replaces Chronicle_Scrobbler's
/// retired sync_engine.py.
/// </summary>
public class ScraperAllCollectionsTests : IClassFixture<ChronicleApiFactory>
{
    private readonly ChronicleApiFactory _factory;

    public ScraperAllCollectionsTests(ChronicleApiFactory factory)
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

    private int SeedCollection(string name, string? posterUrl = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var item = new MediaItem
        {
            MediaTypeId = EnsureMovieTypeId(), Name = name, HierarchyLevel = 0,
            PosterUrl = posterUrl,
            NormalizedName = MediaItemNormalizer.NormalizeName(name),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.MediaItems.Add(item);
        db.SaveChanges();
        db.MediaExternalIds.Add(new MediaExternalId
        {
            MediaItemId = item.Id, Source = "chronicle", ExternalId = $"collection:{item.Id}",
        });
        db.SaveChanges();
        return item.Id;
    }

    private int SeedPlainMovie(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var item = new MediaItem
        {
            MediaTypeId = EnsureMovieTypeId(), Name = name, HierarchyLevel = 0,
            NormalizedName = MediaItemNormalizer.NormalizeName(name),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.MediaItems.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    private async Task<HttpClient> AuthClientAsync()
    {
        var client = _factory.CreateClient();
        var username = $"scraper_allcoll_{Guid.NewGuid():N}";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { username, password = "Password123!" });
        var token = System.Text.Json.JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task GetAllCollections_ReturnsOnlyRealCollectionContainers()
    {
        var collectionId = SeedCollection("Alien Collection", posterUrl: "https://example.com/alien.jpg");
        SeedPlainMovie("Not A Collection");

        var client = await AuthClientAsync();
        var resp = await client.GetAsync("/api/v1/scraper/movies/collections");

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain($"\"id\":{collectionId}");
        body.Should().Contain("Alien Collection");
        body.Should().NotContain("Not A Collection");
    }

    [Fact]
    public async Task GetAllCollections_APlainMovieIsNeverIncluded()
    {
        // ChronicleApiFactory's DB is shared across this whole test class (IClassFixture), so
        // this can't assert a globally empty list -- only that THIS seeded plain movie (never
        // tagged with a "collection:" external id) never appears in the response, however many
        // real collections other tests in this class have already added.
        var movieId = SeedPlainMovie("Just A Movie, Never A Collection");
        var client = await AuthClientAsync();

        var resp = await client.GetAsync("/api/v1/scraper/movies/collections");

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().NotContain($"\"id\":{movieId},");
        body.Should().NotContain("Just A Movie, Never A Collection");
    }

    [Fact]
    public async Task GetAllCollections_CollectionWithNoPosterOfItsOwn_StillReturned()
    {
        // No fallback-to-member-poster expected here since this endpoint deliberately lists
        // collections independent of any specific member movie -- confirms the endpoint doesn't
        // throw or silently drop a poster-less collection.
        var collectionId = SeedCollection("Posterless Collection");

        var client = await AuthClientAsync();
        var resp = await client.GetAsync("/api/v1/scraper/movies/collections");

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain($"\"id\":{collectionId}");
    }
}
