using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Tests.Integration;

/// <summary>
/// scraper/movies/search's filename fast-path (ScraperController.SearchMovies) trusts an
/// existing item's own recorded filename (fileScanner.filePaths or scraperResolvedFile.fileName)
/// as a high-confidence match and skips title matching entirely. Confirmed live (2026-09-18): a
/// stale fileScanner.filePaths entry on the WRONG item (a Chronicle FileScanService import
/// mismatch, unrelated to this endpoint) let this fast-path silently attach a real 2016 remake's
/// file to an existing, already-correct 1984 original's item -- on every future scrape, forever
/// -- because the matched filename alone was trusted even though the candidate's own Year flatly
/// contradicted the search's year.
/// </summary>
public class ScraperMovieFilenameYearMismatchTests : IClassFixture<ChronicleApiFactory>
{
    private readonly ChronicleApiFactory _factory;

    public ScraperMovieFilenameYearMismatchTests(ChronicleApiFactory factory)
    {
        factory.SeedDatabase();
        _factory = factory;
    }

    private int EnsureMovieType()
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

    private int SeedItemWithScannedFile(int mediaTypeId, string name, int? year, string fileName)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var metadata = new JsonObject
        {
            ["fileScanner"] = new JsonObject
            {
                ["filePaths"] = new JsonArray($"F:\\Videos\\Movies\\{name}\\{fileName}"),
            },
        };
        var item = new MediaItem
        {
            MediaTypeId = mediaTypeId, Name = name, Year = year,
            HierarchyLevel = 0,
            NormalizedName = MediaItemNormalizer.NormalizeName(name),
            MetadataJson = metadata.ToJsonString(),
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
    public async Task MovieSearch_FilenameMatchesButYearContradicts_DoesNotReturnTheWrongItem()
    {
        var movieTypeId = EnsureMovieType();
        const string title = "Scraper Filename Year Mismatch Probe";
        const string fileName = "Scraper Filename Year Mismatch Probe (2016).mkv";
        // Simulates item 420944's real state: an existing 1984-year item carrying a STALE
        // fileScanner record for the 2016 remake's actual filename.
        var wrongYearItemId = SeedItemWithScannedFile(movieTypeId, title, 1984, fileName);

        var client = await AuthClientAsync();
        var resp = await client.GetAsync(
            $"/api/v1/scraper/movies/search?title={Uri.EscapeDataString(title)}&year=2016&fileName={Uri.EscapeDataString(fileName)}");

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().NotContain($"\"id\":{wrongYearItemId}",
            "a same-basename match on an item whose own Year contradicts the search year must not be trusted");
    }

    [Fact]
    public async Task MovieSearch_FilenameMatchesAndYearAgrees_StillUsesTheFastPath()
    {
        // Regression guard: the legitimate case (a real re-scrape of the same file) must keep
        // working -- this fix must not make the filename fast-path stop matching when the years
        // genuinely agree.
        var movieTypeId = EnsureMovieType();
        const string title = "Scraper Filename Year Match Probe";
        const string fileName = "Scraper Filename Year Match Probe (2005).mkv";
        var itemId = SeedItemWithScannedFile(movieTypeId, title, 2005, fileName);

        var client = await AuthClientAsync();
        var resp = await client.GetAsync(
            $"/api/v1/scraper/movies/search?title={Uri.EscapeDataString(title)}&year=2005&fileName={Uri.EscapeDataString(fileName)}");

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain($"\"id\":{itemId}");
    }

    [Fact]
    public async Task MovieSearch_FilenameMatchesAndCandidateHasNoYear_StillUsesTheFastPath()
    {
        // A candidate with no Year at all (a bare stub) has nothing to contradict -- the fast
        // path must still trust the filename match in that case.
        var movieTypeId = EnsureMovieType();
        const string title = "Scraper Filename No Year Probe";
        const string fileName = "Scraper Filename No Year Probe.mkv";
        var itemId = SeedItemWithScannedFile(movieTypeId, title, year: null, fileName);

        var client = await AuthClientAsync();
        var resp = await client.GetAsync(
            $"/api/v1/scraper/movies/search?title={Uri.EscapeDataString(title)}&year=2012&fileName={Uri.EscapeDataString(fileName)}");

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain($"\"id\":{itemId}");
    }
}
