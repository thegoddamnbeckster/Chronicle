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
        // Real writers (FileScanService.SyncKnownFileNamesAsync) keep this indexed table in
        // sync with fileScanner.filePaths automatically -- this direct-seed helper bypasses
        // that writer entirely, so it must populate the same row itself, or SearchMovies's own
        // filename fast-path (which now reads this table, not a MetadataJson scan -- see that
        // endpoint's own 2026-09-19 doc) would never find these seeded items at all.
        db.MediaItemKnownFileNames.Add(new MediaItemKnownFileName { MediaItemId = item.Id, FileName = fileName });
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

    /// <summary>
    /// Root-caused live (2026-09-19): the filename fast-path was rewritten from a
    /// `LIKE '%filename%'` scan over MetadataJson (a ~590ms floor on EVERY search, scanning
    /// ~389MB of JSON with no possible index) to an exact, indexed lookup against
    /// MediaItemKnownFileNames instead. Every item scraped BEFORE this table existed has
    /// fileScanner.filePaths in its MetadataJson but no corresponding row there yet --
    /// IFileScanService.BackfillKnownFileNamesAsync is what catches those up. Pins that the
    /// fast path actually depends on the new table (not a fallback to the old scan) and that
    /// the backfill is what makes a pre-existing item findable through it.
    /// </summary>
    [Fact]
    public async Task MovieSearch_ItemHasFilePathsButNoKnownFileNameRowYet_BackfillMakesItFindable()
    {
        var movieTypeId = EnsureMovieType();
        const string title = "Scraper Backfill Probe";
        const string fileName = "Scraper Backfill Probe (2010).mkv";
        int itemId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var metadata = new JsonObject
            {
                ["fileScanner"] = new JsonObject
                {
                    ["filePaths"] = new JsonArray($"F:\\Videos\\Movies\\{title}\\{fileName}"),
                },
            };
            var item = new MediaItem
            {
                MediaTypeId = movieTypeId, Name = title, Year = 2010,
                HierarchyLevel = 0,
                NormalizedName = MediaItemNormalizer.NormalizeName(title),
                MetadataJson = metadata.ToJsonString(),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.MediaItems.Add(item);
            db.SaveChanges();
            itemId = item.Id;
            // Deliberately NOT seeding MediaItemKnownFileNames here -- simulates data that
            // predates this table.
        }

        var client = await AuthClientAsync();

        // Before the backfill runs, the fast path has nothing to find this item by.
        var beforeResp = await client.GetAsync(
            $"/api/v1/scraper/movies/search?title={Uri.EscapeDataString(title)}&year=2010&fileName={Uri.EscapeDataString(fileName)}");
        beforeResp.EnsureSuccessStatusCode();
        (await beforeResp.Content.ReadAsStringAsync()).Should().NotContain($"\"id\":{itemId}",
            "nothing has populated MediaItemKnownFileNames for this item yet");

        using (var scope = _factory.Services.CreateScope())
        {
            var fileScanService = scope.ServiceProvider.GetRequiredService<Chronicle.Services.IFileScanService>();
            await fileScanService.BackfillKnownFileNamesAsync();
        }

        var afterResp = await client.GetAsync(
            $"/api/v1/scraper/movies/search?title={Uri.EscapeDataString(title)}&year=2010&fileName={Uri.EscapeDataString(fileName)}");
        afterResp.EnsureSuccessStatusCode();
        (await afterResp.Content.ReadAsStringAsync()).Should().Contain($"\"id\":{itemId}",
            "the backfill should have populated MediaItemKnownFileNames from the item's existing fileScanner.filePaths");
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
