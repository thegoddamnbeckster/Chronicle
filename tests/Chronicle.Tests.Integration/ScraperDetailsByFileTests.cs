using System.Net;
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
/// /movies/details-by-file and /tv/episode-details-by-file -- read-only resolution of a Kodi
/// video file's exact basename to its Chronicle item, backing Chronicle_Scraper's
/// full_sync_check.py (added 2026-09-22, root-caused by a "Ghostbusters (2016)" file that had
/// been scraped with the wrong movie's data and never corrected since -- nothing had ever
/// re-compared already-scraped Kodi data against Chronicle's current state). Unlike
/// /movies/search, these never create anything and never fall back to title/year matching --
/// a caller walking Kodi's own full inventory has nothing safer to do on an ambiguous or
/// missing match than skip that file.
/// </summary>
public class ScraperDetailsByFileTests : IClassFixture<ChronicleApiFactory>
{
    private readonly ChronicleApiFactory _factory;

    public ScraperDetailsByFileTests(ChronicleApiFactory factory)
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

    private int SeedMovieWithScannedFile(int mediaTypeId, string name, int? year, string fileName)
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
        // sync automatically -- this direct-seed helper bypasses that writer, so it must
        // populate the same row itself (see ScraperMovieFilenameYearMismatchTests' own copy of
        // this exact note).
        db.MediaItemKnownFileNames.Add(new MediaItemKnownFileName { MediaItemId = item.Id, FileName = fileName });
        db.SaveChanges();
        return item.Id;
    }

    private int SeedEpisodeWithScannedFile(
        int mediaTypeId, string showName, int seasonNumber, int episodeNumber,
        string episodeTitle, string fileName)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

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

        var metadata = new JsonObject
        {
            ["fileScanner"] = new JsonObject
            {
                ["filePaths"] = new JsonArray($"F:\\Videos\\TV\\{showName}\\Season {seasonNumber}\\{fileName}"),
            },
        };
        var episode = new MediaItem
        {
            MediaTypeId = mediaTypeId, Name = episodeTitle, ParentId = season.Id,
            HierarchyLevel = 2, Number = episodeNumber,
            NormalizedName = MediaItemNormalizer.NormalizeName(episodeTitle),
            MetadataJson = metadata.ToJsonString(),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.MediaItems.Add(episode);
        db.SaveChanges();
        db.MediaItemKnownFileNames.Add(new MediaItemKnownFileName { MediaItemId = episode.Id, FileName = fileName });
        db.SaveChanges();
        return episode.Id;
    }

    private async Task<HttpClient> AuthClientAsync()
    {
        var client = _factory.CreateClient();
        var username = $"scraper_bfile_{Guid.NewGuid():N}";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { username, password = "Password123!" });
        var token = System.Text.Json.JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task MoviesDetailsByFile_KnownFile_ReturnsThatItemsCurrentDetails()
    {
        var movieTypeId = EnsureMovieType();
        const string fileName = "Ghostbusters By File Probe (2016).mkv";
        var itemId = SeedMovieWithScannedFile(movieTypeId, "Ghostbusters By File Probe", 2016, fileName);

        var client = await AuthClientAsync();
        var resp = await client.GetAsync(
            $"/api/v1/scraper/movies/details-by-file?fileName={Uri.EscapeDataString(fileName)}");

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("\"title\":\"Ghostbusters By File Probe\"");
        body.Should().Contain("\"year\":2016");
    }

    [Fact]
    public async Task MoviesDetailsByFile_UnknownFile_Returns404AndNeverCreatesAnything()
    {
        EnsureMovieType();
        var client = await AuthClientAsync();

        var resp = await client.GetAsync(
            "/api/v1/scraper/movies/details-by-file?fileName=" +
            Uri.EscapeDataString("No Such Movie File Probe (2016).mkv"));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "unlike /movies/search, this endpoint must never create a stub for an unresolved file");
    }

    [Fact]
    public async Task MoviesDetailsByFile_MissingFileNameParam_ReturnsBadRequest()
    {
        var client = await AuthClientAsync();
        var resp = await client.GetAsync("/api/v1/scraper/movies/details-by-file");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task MoviesDetailsByFile_AmbiguousExactFilenameAcrossTwoItems_Returns404RatherThanGuessing()
    {
        // Two real different movies sharing the exact same basename in different folders is an
        // expected, documented case for MediaItemKnownFileNames (see that model's own doc) --
        // with no year/title hint here to disambiguate the way /movies/search can, this must
        // never guess.
        var movieTypeId = EnsureMovieType();
        const string fileName = "Ambiguous By File Probe.mkv";
        SeedMovieWithScannedFile(movieTypeId, "Ambiguous By File Probe One", 2001, fileName);
        SeedMovieWithScannedFile(movieTypeId, "Ambiguous By File Probe Two", 2002, fileName);

        var client = await AuthClientAsync();
        var resp = await client.GetAsync(
            $"/api/v1/scraper/movies/details-by-file?fileName={Uri.EscapeDataString(fileName)}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task EpisodeDetailsByFile_KnownFile_ReturnsThatEpisodesCurrentDetails()
    {
        var tvTypeId = EnsureTvType();
        const string fileName = "By File Probe Show S02E05.mkv";
        SeedEpisodeWithScannedFile(
            tvTypeId, "By File Probe Show", 2, 5, "The Long Way Home", fileName);

        var client = await AuthClientAsync();
        var resp = await client.GetAsync(
            $"/api/v1/scraper/tv/episode-details-by-file?fileName={Uri.EscapeDataString(fileName)}");

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("\"title\":\"The Long Way Home\"");
        body.Should().Contain("\"showTitle\":\"By File Probe Show\"");
        // Library Repair needs the episode's own id to reset exactly one episode's watch status.
        body.Should().MatchRegex(@"""mediaItemId"":\d+");
        body.Should().Contain("\"season\":2");
        body.Should().Contain("\"episode\":5");
    }

    [Fact]
    public async Task EpisodeDetailsByFile_UnknownFile_Returns404AndNeverCreatesAnything()
    {
        EnsureTvType();
        var client = await AuthClientAsync();

        var resp = await client.GetAsync(
            "/api/v1/scraper/tv/episode-details-by-file?fileName=" +
            Uri.EscapeDataString("No Such Episode File Probe S01E01.mkv"));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task EpisodeDetailsByFile_MovieFileName_DoesNotMatchAcrossMediaTypes()
    {
        // A movie's known filename must never resolve through the EPISODE endpoint -- confirms
        // the HierarchyLevel==2 + show-like-type filter actually scopes the lookup.
        var movieTypeId = EnsureMovieType();
        EnsureTvType();
        const string fileName = "Cross Type By File Probe (2016).mkv";
        SeedMovieWithScannedFile(movieTypeId, "Cross Type By File Probe", 2016, fileName);

        var client = await AuthClientAsync();
        var resp = await client.GetAsync(
            $"/api/v1/scraper/tv/episode-details-by-file?fileName={Uri.EscapeDataString(fileName)}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Caught in review (2026-09-22): this endpoint originally had no cross-check at all against
    /// a stale MediaItemKnownFileNames record -- the exact class of bug it exists to help fix
    /// (see ScraperMovieFilenameYearMismatchTests' own doc for the original incident). Since
    /// this endpoint's result feeds an UNSUPERVISED correction (full_sync_check.py), trusting a
    /// stale record outright would actively corrupt a correctly-scraped Kodi movie instead of
    /// fixing anything.
    /// </summary>
    [Fact]
    public async Task MoviesDetailsByFile_FilenameMatchesButYearContradicts_TreatedAsUnresolved()
    {
        var movieTypeId = EnsureMovieType();
        const string fileName = "By File Year Mismatch Probe (2016).mkv";
        // Simulates a stale record: this item's OWN year (1984) disagrees with the year the
        // caller (Kodi, via full_sync_check.py) actually has for this exact file (2016).
        var wrongYearItemId = SeedMovieWithScannedFile(movieTypeId, "By File Year Mismatch Probe", 1984, fileName);

        var client = await AuthClientAsync();
        var resp = await client.GetAsync(
            $"/api/v1/scraper/movies/details-by-file?fileName={Uri.EscapeDataString(fileName)}&year=2016");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a filename match whose own Year contradicts the caller's year must never be trusted");
    }

    [Fact]
    public async Task MoviesDetailsByFile_FilenameMatchesAndYearAgrees_StillResolves()
    {
        // Regression guard: the legitimate case (a real, correctly-scraped file) must keep
        // working -- this fix must not make the endpoint stop resolving when years genuinely agree.
        var movieTypeId = EnsureMovieType();
        const string fileName = "By File Year Match Probe (2016).mkv";
        SeedMovieWithScannedFile(movieTypeId, "By File Year Match Probe", 2016, fileName);

        var client = await AuthClientAsync();
        var resp = await client.GetAsync(
            $"/api/v1/scraper/movies/details-by-file?fileName={Uri.EscapeDataString(fileName)}&year=2016");

        resp.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task MoviesDetailsByFile_NoYearSupplied_StillResolvesWithoutTheGuard()
    {
        // year is optional -- omitting it (a caller that genuinely doesn't have one) must not
        // block an otherwise-unambiguous match.
        var movieTypeId = EnsureMovieType();
        const string fileName = "By File No Year Probe (2016).mkv";
        SeedMovieWithScannedFile(movieTypeId, "By File No Year Probe", 2016, fileName);

        var client = await AuthClientAsync();
        var resp = await client.GetAsync(
            $"/api/v1/scraper/movies/details-by-file?fileName={Uri.EscapeDataString(fileName)}");

        resp.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task EpisodeDetailsByFile_EpisodeNumberContradicts_TreatedAsUnresolved()
    {
        var tvTypeId = EnsureTvType();
        const string fileName = "By File Episode Mismatch Probe S02E05.mkv";
        // Simulates a stale record: this item's OWN episode number (9) disagrees with what the
        // caller actually has for this exact file (5).
        SeedEpisodeWithScannedFile(tvTypeId, "By File Episode Mismatch Show", 2, 9, "Wrong Episode", fileName);

        var client = await AuthClientAsync();
        var resp = await client.GetAsync(
            $"/api/v1/scraper/tv/episode-details-by-file?fileName={Uri.EscapeDataString(fileName)}&season=2&episode=5");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task EpisodeDetailsByFile_SeasonNumberContradicts_TreatedAsUnresolved()
    {
        var tvTypeId = EnsureTvType();
        const string fileName = "By File Season Mismatch Probe S02E05.mkv";
        SeedEpisodeWithScannedFile(tvTypeId, "By File Season Mismatch Show", 3, 5, "Wrong Season", fileName);

        var client = await AuthClientAsync();
        var resp = await client.GetAsync(
            $"/api/v1/scraper/tv/episode-details-by-file?fileName={Uri.EscapeDataString(fileName)}&season=2&episode=5");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task EpisodeDetailsByFile_SeasonAndEpisodeAgree_StillResolves()
    {
        var tvTypeId = EnsureTvType();
        const string fileName = "By File Season Episode Match Probe S02E05.mkv";
        SeedEpisodeWithScannedFile(tvTypeId, "By File Season Episode Match Show", 2, 5, "Correct Episode", fileName);

        var client = await AuthClientAsync();
        var resp = await client.GetAsync(
            $"/api/v1/scraper/tv/episode-details-by-file?fileName={Uri.EscapeDataString(fileName)}&season=2&episode=5");

        resp.EnsureSuccessStatusCode();
    }
}
