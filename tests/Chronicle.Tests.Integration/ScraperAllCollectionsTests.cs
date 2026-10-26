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

    [Fact]
    public async Task ChangedCollections_WithoutSince_ListsEveryCollectionAndStampsAsOf()
    {
        var collectionId = SeedCollection("Changed Feed Collection");

        var client = await AuthClientAsync();
        var resp = await client.GetAsync("/api/v1/scraper/movies/collections/changed");

        resp.EnsureSuccessStatusCode();
        var data = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data");
        data.TryGetProperty("asOf", out _).Should().BeTrue();
        data.GetProperty("collections").ToString().Should().Contain($"\"id\":{collectionId}");
    }

    private async Task<HttpClient> ApiKeyClientAsync()
    {
        var bearer = await AuthClientAsync();
        var resp = await bearer.PostAsJsonAsync("/api/v1/tokens", new { name = "Kodi Scraper" });
        resp.EnsureSuccessStatusCode();
        var key = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("token").GetString()!;
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", key);
        return client;
    }

    private static async Task<(string asOf, string collections)> ChangedAsync(HttpClient client, string? since = null)
    {
        var url = "/api/v1/scraper/movies/collections/changed" + (since is null ? "" : "?since=" + Uri.EscapeDataString(since));
        var resp = await client.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var data = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data");
        return (data.GetProperty("asOf").GetString()!, data.GetProperty("collections").ToString());
    }

    [Fact]
    public async Task ChangedCollections_AreListedOnlyAfterTheTimeTheCallerSends_NothingIsKeptPerDevice()
    {
        var collectionId = SeedCollection("Since Collection");
        var device = await ApiKeyClientAsync();

        var (asOf, first) = await ChangedAsync(device);
        first.Should().Contain($"\"id\":{collectionId}");

        // The caller's own copy of the time decides: nothing changed after it, so nothing is listed...
        (await ChangedAsync(device, since: asOf)).collections.Should().NotContain($"\"id\":{collectionId}");
        // ...and another install holding an older time is still given it.
        (await ChangedAsync(device, since: DateTime.UtcNow.AddDays(-1).ToString("O"))).collections
            .Should().Contain($"\"id\":{collectionId}");

        await Task.Delay(1100); // timestamps are stored to the second on SQLite
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            db.MediaItems.First(m => m.Id == collectionId).Overview = "Changed";
            db.SaveChanges();
        }
        (await ChangedAsync(device, since: asOf)).collections.Should().Contain($"\"id\":{collectionId}");
    }

    [Fact]
    public async Task ChangedCollections_AMemberChangingMakesItsCollectionDueAgain()
    {
        var collectionId = SeedCollection("Member Change Collection");
        int memberId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var member = new MediaItem
            {
                MediaTypeId = EnsureMovieTypeId(), Name = "Member Movie", HierarchyLevel = 0, ParentId = collectionId,
                NormalizedName = MediaItemNormalizer.NormalizeName("Member Movie"),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.MediaItems.Add(member);
            db.SaveChanges();
            memberId = member.Id;
        }
        var device = await ApiKeyClientAsync();
        await Task.Delay(1100);
        var (asOf, _) = await ChangedAsync(device);
        (await ChangedAsync(device, since: asOf)).collections.Should().NotContain($"\"id\":{collectionId}");

        await Task.Delay(1100);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            db.MediaItems.First(m => m.Id == memberId).Overview = "New poster source";
            db.SaveChanges();
        }
        // A collection with no poster of its own shows a member's, so a member change counts.
        (await ChangedAsync(device, since: asOf)).collections.Should().Contain($"\"id\":{collectionId}");
    }

    [Fact]
    public async Task AScraperCall_LogsTheKodiInstall()
    {
        var device = await ApiKeyClientAsync();
        (await device.GetAsync("/api/v1/scraper/movies/collections/changed")).EnsureSuccessStatusCode();

        // The note is made as the request finishes, so it can land a moment after the reply.
        var seen = false;
        for (var i = 0; i < 30 && !seen; i++)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            seen = db.KodiDevices.Any(d => d.Name == "Kodi Scraper" && d.LastSeenAt > DateTime.UtcNow.AddMinutes(-2));
            if (!seen) await Task.Delay(100);
        }
        seen.Should().BeTrue();
    }

    [Fact]
    public async Task RegisteringADevice_NeedsOnlyAName_AndStillAcceptsTheOlderShape()
    {
        var device = await ApiKeyClientAsync();

        (await device.PostAsJsonAsync("/api/v1/scraper/devices/kodi/register", new { name = "Shield" }))
            .EnsureSuccessStatusCode();
        (await device.PostAsJsonAsync("/api/v1/scraper/devices/kodi/register",
            new { name = "Shield", host = "10.0.0.5", port = 8080, username = "kodi", password = "pw" }))
            .EnsureSuccessStatusCode();
    }
}
