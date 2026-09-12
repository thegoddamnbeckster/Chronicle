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
/// Root-caused live (2026-09-12): Kodi's "getartwork" action for TV shows passes back the
/// show's own default uniqueid (imdb, since Chronicle's own NFOs always mark it default="true")
/// as a bare string like "tt27497393" -- neither a Chronicle internal id nor the addon's own
/// opaque lookup-string format "find"/"getdetails" exchange instead. Every getartwork call
/// failed to resolve anything as a result, so Kodi's "Choose Art" picker showed zero options
/// for every TV show, always. This endpoint is the missing lookup the addon needs.
/// </summary>
public class ScraperResolveByExternalIdTests : IClassFixture<ChronicleApiFactory>
{
    private readonly ChronicleApiFactory _factory;

    public ScraperResolveByExternalIdTests(ChronicleApiFactory factory)
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

    /// Seeds a show with a REAL persisted MediaExternalId row -- the shape tmdb/tvmaze/
    /// fanarttv/simkl/wikipedia actually use (Chronicle records "this item came from provider X
    /// with id Y" directly).
    private int SeedShowWithExternalId(int mediaTypeId, string name, int? year, string source, string externalId,
        int hierarchyLevel = 0, int? parentId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var item = new MediaItem
        {
            MediaTypeId = mediaTypeId, Name = name, Year = year, ParentId = parentId,
            HierarchyLevel = hierarchyLevel,
            NormalizedName = MediaItemNormalizer.NormalizeName(name),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.MediaItems.Add(item);
        db.SaveChanges();
        db.MediaExternalIds.Add(new MediaExternalId
        {
            MediaItemId = item.Id, Source = source, ExternalId = externalId,
        });
        db.SaveChanges();
        return item.Id;
    }

    /// Seeds a show with an imdb/tvdb/trakt id embedded the way it actually appears in
    /// production: CollectExternalIds derives these three from a provider's own extendedData at
    /// read time -- see that method's own doc -- they are never persisted as their own
    /// MediaExternalId row, unlike tmdb/tvmaze/fanarttv/simkl/wikipedia (confirmed live,
    /// 2026-09-12: Stuart Fails to Save the Universe's own media_external_ids rows cover those
    /// five sources but have no "imdb" row at all, despite /tv/details correctly reporting one).
    private int SeedShowWithEmbeddedExternalId(int mediaTypeId, string name, int? year,
        string embeddedIdKey, string externalId, int hierarchyLevel = 0, int? parentId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
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
        var item = new MediaItem
        {
            MediaTypeId = mediaTypeId, Name = name, Year = year, ParentId = parentId,
            HierarchyLevel = hierarchyLevel, MetadataJson = metadataJson,
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

    [Fact]
    public async Task ResolveByExternalId_ImdbEmbeddedInProviderExtendedData_ReturnsItsChronicleId()
    {
        // The realistic shape: imdb is never its own MediaExternalId row (see
        // SeedShowWithEmbeddedExternalId's own doc) -- this is the exact case that 404'd on
        // every show, including ones Chronicle fully knew, before the MetadataJson fallback.
        var tvTypeId = EnsureTvType();
        var showId = SeedShowWithEmbeddedExternalId(tvTypeId, "Resolve Probe Show", 2026, "imdb", "tt27497393");

        var client = await AuthClientAsync();
        var resp = await client.GetAsync("/api/v1/scraper/tv/resolve-by-external-id?source=imdb&externalId=tt27497393");

        resp.EnsureSuccessStatusCode();
        (await resp.Content.ReadAsStringAsync()).Should().Contain($"\"id\":{showId}");
    }

    [Fact]
    public async Task ResolveByExternalId_TmdbPersistedAsItsOwnRow_StillResolvesViaTheFastPath()
    {
        // tmdb (and tvmaze/fanarttv/simkl/wikipedia) ARE persisted as their own MediaExternalId
        // row -- confirms the direct-table lookup still works and isn't shadowed by the
        // MetadataJson fallback added for imdb/tvdb/trakt.
        var tvTypeId = EnsureTvType();
        var showId = SeedShowWithExternalId(tvTypeId, "Resolve Probe Tmdb", 2026, "tmdb", "287620");

        var client = await AuthClientAsync();
        var resp = await client.GetAsync("/api/v1/scraper/tv/resolve-by-external-id?source=tmdb&externalId=287620");

        resp.EnsureSuccessStatusCode();
        (await resp.Content.ReadAsStringAsync()).Should().Contain($"\"id\":{showId}");
    }

    [Fact]
    public async Task ResolveByExternalId_SourceIsCaseInsensitive()
    {
        var tvTypeId = EnsureTvType();
        var showId = SeedShowWithEmbeddedExternalId(tvTypeId, "Resolve Probe Case", 2026, "imdb", "tt00000001");

        var client = await AuthClientAsync();
        var resp = await client.GetAsync("/api/v1/scraper/tv/resolve-by-external-id?source=IMDB&externalId=tt00000001");

        resp.EnsureSuccessStatusCode();
        (await resp.Content.ReadAsStringAsync()).Should().Contain($"\"id\":{showId}");
    }

    [Fact]
    public async Task ResolveByExternalId_NoMatchingExternalId_ReturnsNotFound()
    {
        EnsureTvType();
        var client = await AuthClientAsync();

        var resp = await client.GetAsync("/api/v1/scraper/tv/resolve-by-external-id?source=imdb&externalId=tt99999999");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ResolveByExternalId_ExternalIdBelongsToAnEpisodeNotAShow_ReturnsNotFound()
    {
        // Only the show-level item is a valid getartwork target -- an external id that happens
        // to be embedded on an episode (HierarchyLevel 2) must not resolve as if it were the show.
        var tvTypeId = EnsureTvType();
        SeedShowWithEmbeddedExternalId(tvTypeId, "Resolve Probe Episode", 2026, "tvdb", "555555", hierarchyLevel: 2);

        var client = await AuthClientAsync();
        var resp = await client.GetAsync("/api/v1/scraper/tv/resolve-by-external-id?source=tvdb&externalId=555555");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("", "tt1234567")]
    [InlineData("imdb", "")]
    public async Task ResolveByExternalId_MissingSourceOrExternalId_ReturnsBadRequest(string source, string externalId)
    {
        EnsureTvType();
        var client = await AuthClientAsync();

        var resp = await client.GetAsync(
            $"/api/v1/scraper/tv/resolve-by-external-id?source={Uri.EscapeDataString(source)}&externalId={Uri.EscapeDataString(externalId)}");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
