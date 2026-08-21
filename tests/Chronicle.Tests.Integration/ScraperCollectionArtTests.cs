using System.Net.Http.Json;
using System.Text.Json;
using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Tests.Integration;

/// <summary>
/// Kodi never scrapes a movie set — there is no getdetails hook for one — so everything a
/// collection's artwork needs to reach Kodi has to travel on the parent movie's details
/// response. These tests pin that contract: all art types present, and the user's explicit
/// pins flagged so the addon knows which local files it's allowed to overwrite.
/// </summary>
public class ScraperCollectionArtTests : IClassFixture<ChronicleApiFactory>
{
    private readonly ChronicleApiFactory _factory;

    public ScraperCollectionArtTests(ChronicleApiFactory factory)
    {
        factory.SeedDatabase();
        _factory = factory;
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

    /// <summary>Creates a collection parent with the given MetadataJson and one member movie,
    /// returning the member's id (the only id Kodi ever asks about).</summary>
    private int SeedCollectionWithMember(string collectionName, string metadataJson)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var typeId = MoviesTypeId();

        var parent = new MediaItem
        {
            MediaTypeId = typeId, Name = collectionName, HierarchyLevel = 0,
            NormalizedName = MediaItemNormalizer.NormalizeName(collectionName),
            MetadataJson = metadataJson,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.MediaItems.Add(parent);
        db.SaveChanges();

        var member = new MediaItem
        {
            MediaTypeId = typeId, Name = collectionName + " Part One", Year = 1988,
            HierarchyLevel = 1, ParentId = parent.Id,
            NormalizedName = MediaItemNormalizer.NormalizeName(collectionName + " Part One"),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.MediaItems.Add(member);
        db.SaveChanges();
        return member.Id;
    }

    private async Task<HttpClient> AuthClientAsync()
    {
        var client = _factory.CreateClient();
        var username = $"colart_{Guid.NewGuid():N}";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { username, password = "Password123!" });
        var token = JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<JsonElement> GetCollectionAsync(int memberId)
    {
        var client = await AuthClientAsync();
        var resp = await client.GetAsync($"/api/v1/scraper/movies/details?id={memberId}");
        resp.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        return body.GetProperty("data").GetProperty("collection");
    }

    private const string AllArtResolved = """
        {
          "_resolved": {
            "title": "Full Art Collection",
            "posterUrl":   "https://img/poster.jpg",
            "backdropUrl": "https://img/backdrop.jpg",
            "logoUrl":     "https://img/logo.png",
            "bannerUrl":   "https://img/banner.jpg",
            "clearartUrl": "https://img/clearart.png",
            "discUrl":     "https://img/disc.png",
            "thumbUrl":    "https://img/thumb.jpg"
          }
        }
        """;

    [Fact]
    public async Task CollectionPayload_CarriesEveryArtTypeKodiSupports()
    {
        // Anything missing here simply cannot reach Kodi — a set has no other channel.
        var memberId = SeedCollectionWithMember("Full Art Collection", AllArtResolved);

        var collection = await GetCollectionAsync(memberId);

        collection.GetProperty("posterUrl").GetString().Should().Be("https://img/poster.jpg");
        collection.GetProperty("backdropUrl").GetString().Should().Be("https://img/backdrop.jpg");
        collection.GetProperty("logoUrl").GetString().Should().Be("https://img/logo.png");
        collection.GetProperty("bannerUrl").GetString().Should().Be("https://img/banner.jpg");
        collection.GetProperty("clearartUrl").GetString().Should().Be("https://img/clearart.png");
        collection.GetProperty("discUrl").GetString().Should().Be("https://img/disc.png");
        collection.GetProperty("thumbUrl").GetString().Should().Be("https://img/thumb.jpg");
    }

    [Fact]
    public async Task CollectionPayload_ReportsPinnedSlots()
    {
        // The addon is fill-only for automatic art so it never clobbers hand-curated files.
        // A pin is the user's own choice, so it has to be distinguishable — otherwise the
        // poster chosen in Chronicle's UI can never reach a set that already has one.
        const string pinnedJson = """
            {
              "_resolved": { "posterUrl": "https://img/pinned-poster.jpg",
                             "discUrl":   "https://img/pinned-disc.png" },
              "_overrides": {
                "poster_url": { "url": "https://img/pinned-poster.jpg", "sourcePluginId": "chronicle.plugin.tmdb" },
                "disc_url":   { "url": "https://img/pinned-disc.png",   "sourcePluginId": "chronicle.plugin.tmdb" }
              }
            }
            """;
        var memberId = SeedCollectionWithMember("Pinned Collection", pinnedJson);

        var collection = await GetCollectionAsync(memberId);

        var pinned = collection.GetProperty("pinnedSlots").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        pinned.Should().BeEquivalentTo(["poster_url", "disc_url"]);
    }

    [Fact]
    public async Task CollectionPayload_NoOverrides_ReportsEmptyPinnedSlots()
    {
        var memberId = SeedCollectionWithMember("Unpinned Collection", AllArtResolved);

        var collection = await GetCollectionAsync(memberId);

        collection.GetProperty("pinnedSlots").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task CollectionPayload_MalformedMetadata_DoesNotBreakTheResponse()
    {
        // A collection with unparsable metadata must still return its name and members rather
        // than 500 the movie's whole getdetails call.
        var memberId = SeedCollectionWithMember("Broken Metadata Collection", "{ not valid json");

        var collection = await GetCollectionAsync(memberId);

        collection.GetProperty("name").GetString().Should().Be("Broken Metadata Collection");
        collection.GetProperty("pinnedSlots").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task CollectionPayload_OverridesKeyIsNotLeakedAsAnArtUrl()
    {
        // _overrides sits beside _resolved in the same blob; the art fields must come from
        // _resolved only, never from the override records themselves.
        const string json = """
            {
              "_resolved":  { "posterUrl": "https://img/resolved.jpg" },
              "_overrides": { "poster_url": { "url": "https://img/resolved.jpg" } }
            }
            """;
        var memberId = SeedCollectionWithMember("Override Shape Collection", json);

        var collection = await GetCollectionAsync(memberId);

        collection.GetProperty("posterUrl").GetString().Should().Be("https://img/resolved.jpg");
        collection.GetProperty("logoUrl").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
