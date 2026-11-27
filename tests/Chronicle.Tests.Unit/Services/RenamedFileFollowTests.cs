using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// A file is scanned under one name and later renamed or moved (Sonarr/Radarr importing it into the library, a manual
/// tidy-up). The item must follow the file instead of a duplicate being created and the old item left pointing at a
/// path that no longer exists.
/// </summary>
public sealed class RenamedFileFollowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chronicle-rename-" + Guid.NewGuid().ToString("N"));

    public RenamedFileFollowTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* scratch */ } }

    private static ChronicleDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ChronicleDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static FileScanService Service(ChronicleDbContext db)
    {
        var registry = new Mock<IPluginRegistry>();
        registry.Setup(r => r.GetMetadataProviderEntries()).Returns([]);
        return new FileScanService(db, registry.Object, null!, null!, new ImportProgressService(), null!);
    }

    private string Touch(string relative, int bytes = 1000, DateTime? modified = null)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, modified ?? new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        return path;
    }

    private static async Task<ChronicleDbContext> WithTvTypeAsync()
    {
        var db = NewContext();
        db.MediaTypes.Add(new MediaType { Id = 1, Name = "tv", DisplayName = "TV", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow });
        db.MediaTypes.Add(new MediaType { Id = 2, Name = "movies", DisplayName = "Movies", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return db;
    }

    private static ImportGroupsRequest Episode(string showFolder, int season, int number, string name, string file) => new(
    [
        new ScanGroupImport("Show Name", null, null,
        [
            new ScanGroupImport($"Season {season}", null, null,
            [
                new ScanGroupImport(name, null, null, [], [file], null, number),
            ], [], Path.Combine(showFolder, $"Season {season}"), season),
        ], [], showFolder),
    ], 1);

    [Fact]
    public async Task ARenamedEpisode_KeepsItsItem_AndTheItemPointsAtTheNewFile()
    {
        await using var db = await WithTvTypeAsync();
        var show = Path.Combine(_dir, "Show Name");
        var oldFile = Touch("Show Name/Season 1/Show.Name.S01E01.720p.HDTV.x264-GRP.mkv");
        await Service(db).ImportGroupsAsync(Episode(show, 1, 1, "Show.Name.S01E01.720p.HDTV.x264-GRP", oldFile), [1], manageProgress: false);
        var itemId = (await db.MediaItems.SingleAsync(m => m.HierarchyLevel == 2)).Id;

        // Sonarr renames it after the first scan.
        var newFile = Path.Combine(_dir, "Show Name", "Season 1", "Show Name - S01E01 - Pilot.mkv");
        File.Move(oldFile, newFile);
        await Service(db).ImportGroupsAsync(Episode(show, 1, 1, "Show Name - S01E01 - Pilot", newFile), [1], manageProgress: false);

        var episodes = await db.MediaItems.Where(m => m.HierarchyLevel == 2).ToListAsync();
        Assert.Single(episodes);                                   // no duplicate
        Assert.Equal(itemId, episodes[0].Id);                      // the same item, with its history
        Assert.Contains(newFile.Replace('\\', '/'), episodes[0].MetadataJson!.Replace("\\\\", "/"));
        Assert.DoesNotContain("HDTV", episodes[0].MetadataJson!);  // the stale path is gone
    }

    [Fact]
    public async Task TwoDifferentEpisodesSharingANumber_AreNotMerged_WhileTheirFilesExist()
    {
        await using var db = await WithTvTypeAsync();
        var show = Path.Combine(_dir, "Show Name");
        var a = Touch("Show Name/Season 1/clip a.mkv");
        var b = Touch("Show Name/Season 1/clip b.mkv", bytes: 2000);
        await Service(db).ImportGroupsAsync(Episode(show, 1, 5, "Clip A", a), [1], manageProgress: false);

        await Service(db).ImportGroupsAsync(Episode(show, 1, 5, "Clip B", b), [1], manageProgress: false);

        Assert.Equal(2, await db.MediaItems.CountAsync(m => m.HierarchyLevel == 2));
    }

    [Fact]
    public async Task AMovedLooseMovie_IsRecognisedBySizeAndTime_NotDuplicated()
    {
        await using var db = await WithTvTypeAsync();
        var oldFile = Touch("Incoming/Movie.Name.2019.1080p.BluRay.mkv", bytes: 5000);
        var request1 = new ImportGroupsRequest([new ScanGroupImport("Movie Name (2019)", 2019, null, [], [oldFile])], 2);
        await Service(db).ImportGroupsAsync(request1, [1], manageProgress: false);
        var itemId = (await db.MediaItems.SingleAsync(m => m.HierarchyLevel == 0)).Id;

        var newFile = Path.Combine(_dir, "Movies", "Movie Name (2019)", "Movie Name (2019).mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(newFile)!);
        File.Move(oldFile, newFile);
        var request2 = new ImportGroupsRequest([new ScanGroupImport("Movie Name (2019) Renamed", 2019, null, [], [newFile])], 2);
        await Service(db).ImportGroupsAsync(request2, [1], manageProgress: false);

        var movies = await db.MediaItems.Where(m => m.HierarchyLevel == 0 && m.MediaTypeId == 2).ToListAsync();
        Assert.Single(movies);
        Assert.Equal(itemId, movies[0].Id);
    }

    [Fact]
    public async Task ADifferentFileWithTheSameSize_IsNotTakenForAMove_WhileTheOldOneStillExists()
    {
        await using var db = await WithTvTypeAsync();
        var first = Touch("Movies/First (2019)/First (2019).mkv", bytes: 5000);
        var second = Touch("Movies/Second (2019)/Second (2019).mkv", bytes: 5000);
        await Service(db).ImportGroupsAsync(new ImportGroupsRequest([new ScanGroupImport("First (2019)", 2019, null, [], [first])], 2), [1], manageProgress: false);

        await Service(db).ImportGroupsAsync(new ImportGroupsRequest([new ScanGroupImport("Second (2019)", 2019, null, [], [second])], 2), [1], manageProgress: false);

        Assert.Equal(2, await db.MediaItems.CountAsync(m => m.HierarchyLevel == 0 && m.MediaTypeId == 2));
    }
}

/// <summary>
/// An episode is always filed under the show whose folder holds its file (2026-11-26: The Voice's "Season 29" held Worst Cooks in
/// America's season folder and episode files, so every scan filed Worst Cooks episodes under The Voice).
/// </summary>
public sealed class EpisodeFiledUnderItsOwnShowTests
{
    private static ChronicleDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ChronicleDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static FileScanService Service(ChronicleDbContext db)
    {
        var registry = new Mock<IPluginRegistry>();
        registry.Setup(r => r.GetMetadataProviderEntries()).Returns([]);
        return new FileScanService(db, registry.Object, null!, null!, new ImportProgressService(), null!);
    }

    private const string Voice = "D:/TV/The Voice (2011)";
    private const string Cooks = "D:/TV/Worst Cooks in America (2010)";
    private const string CooksFile = Cooks + "/Season 29/Worst Cooks in America - S29E03 - Da Vinci.mkv";

    private static ScanGroupImport Show(string name, string folder, string episodeName, string file) =>
        new(name, null, null,
        [
            new ScanGroupImport("Season 29", null, null, [new ScanGroupImport(episodeName, null, null, [], [file], null, 3)], [], folder + "/Season 29", 29),
        ], [], folder);

    /// <summary>The Voice's season 29 holding Worst Cooks' season folder, and a Worst Cooks episode filed under it with its file.</summary>
    private static async Task<ChronicleDbContext> TheMixUpAsync()
    {
        var db = NewContext();
        var now = DateTime.UtcNow;
        db.MediaTypes.Add(new MediaType { Id = 1, Name = "tv", DisplayName = "TV", HierarchyLevels = 3, CreatedAt = now });
        db.MediaItems.AddRange(
            new MediaItem { Id = 1, MediaTypeId = 1, Name = "The Voice", HierarchyLevel = 0, CreatedAt = now, UpdatedAt = now,
                MetadataJson = "{\"fileScanner\":{\"folderPath\":\"" + Voice + "\"}}" },
            new MediaItem { Id = 2, MediaTypeId = 1, ParentId = 1, Name = "Season 29", Number = 29, HierarchyLevel = 1, CreatedAt = now, UpdatedAt = now,
                MetadataJson = "{\"fileScanner\":{\"folderPath\":\"" + Cooks + "/Season 29\"}}" },
            new MediaItem { Id = 3, MediaTypeId = 1, ParentId = 2, Name = "Worst Cooks in America - S29E03 - Da Vinci", Number = 3, HierarchyLevel = 2,
                CreatedAt = now, UpdatedAt = now, MetadataJson = "{\"fileScanner\":{\"filePaths\":[\"" + CooksFile + "\"]}}" },
            new MediaItem { Id = 10, MediaTypeId = 1, Name = "Worst Cooks in America", HierarchyLevel = 0, CreatedAt = now, UpdatedAt = now,
                MetadataJson = "{\"fileScanner\":{\"folderPath\":\"" + Cooks + "\"}}" },
            new MediaItem { Id = 11, MediaTypeId = 1, ParentId = 10, Name = "Season 29", Number = 29, HierarchyLevel = 1, CreatedAt = now, UpdatedAt = now },
            new MediaItem { Id = 12, MediaTypeId = 1, ParentId = 11, Name = "The Da Vinci Cook", Number = 3, HierarchyLevel = 2, CreatedAt = now, UpdatedAt = now });
        db.MediaItemKnownFileNames.Add(new MediaItemKnownFileName { MediaItemId = 3, FileName = "Worst Cooks in America - S29E03 - Da Vinci.mkv" });
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task AnotherShowsSeasonHoldingThisSeasonsFolder_IsNotWhereItsEpisodesGo()
    {
        await using var db = await TheMixUpAsync();

        await Service(db).ImportGroupsAsync(
            new ImportGroupsRequest([Show("Worst Cooks in America", Cooks, "Worst Cooks in America - S29E03 - Da Vinci", CooksFile)], 1),
            [1], manageProgress: false);

        var real = await db.MediaItems.SingleAsync(m => m.Id == 12);
        Assert.Contains("Da Vinci.mkv", real.MetadataJson!);                         // the show's own episode now has its file
        var stale = await db.MediaItems.SingleAsync(m => m.Id == 3);
        Assert.DoesNotContain("Da Vinci.mkv", stale.MetadataJson!);                  // and The Voice's entry no longer claims it
        Assert.Equal(2, stale.ParentId);                                              // left where it was, for a merge to settle
        Assert.False(await db.MediaItemKnownFileNames.AnyAsync(k => k.MediaItemId == 3));
        Assert.Equal(0, await db.MediaItems.CountAsync(m => m.ParentId == 2 && m.Id != 3)); // nothing new filed under The Voice
    }

    [Fact]
    public async Task TheOtherShowsOwnScan_StillFindsItsOwnSeason()
    {
        await using var db = await TheMixUpAsync();
        const string voiceFile = Voice + "/Season 29/The Voice - S29E03 - The Blinds (3).mkv";

        await Service(db).ImportGroupsAsync(new ImportGroupsRequest([Show("The Voice", Voice, "The Blinds (3)", voiceFile)], 1),
            [1], manageProgress: false);

        Assert.Equal(1, await db.MediaItems.CountAsync(m => m.ParentId == 1 && m.HierarchyLevel == 1)); // no second season 29
        Assert.True(await db.MediaItems.AnyAsync(m => m.ParentId == 2 && m.MetadataJson!.Contains("The Blinds (3).mkv")));
    }

    [Fact]
    public async Task AFileMovedBetweenSeasonsOfTheSameShow_StaysWithItsItem()
    {
        await using var db = await TheMixUpAsync();
        db.MediaItems.Add(new MediaItem { Id = 13, MediaTypeId = 1, ParentId = 10, Name = "Season 30", Number = 30, HierarchyLevel = 1,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        var item = await db.MediaItems.SingleAsync(m => m.Id == 12);
        const string file = Cooks + "/Season 30/Worst Cooks in America - S30E03 - Moved.mkv";
        item.MetadataJson = "{\"fileScanner\":{\"filePaths\":[\"" + file + "\"]}}";
        await db.SaveChangesAsync();
        var group = new ScanGroupImport("Worst Cooks in America", null, null,
        [
            new ScanGroupImport("Season 30", null, null, [new ScanGroupImport("Moved", null, null, [], [file], null, 3)], [], Cooks + "/Season 30", 30),
        ], [], Cooks);

        await Service(db).ImportGroupsAsync(new ImportGroupsRequest([group], 1), [1], manageProgress: false);

        Assert.Contains("Moved.mkv", (await db.MediaItems.SingleAsync(m => m.Id == 12)).MetadataJson!); // same show: still its item
    }
}

/// <summary>Two films with the same title and different years in different folders must stay two items.</summary>
public sealed class SameTitleDifferentYearTests
{
    private static ChronicleDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ChronicleDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static FileScanService Service(ChronicleDbContext db)
    {
        var registry = new Mock<IPluginRegistry>();
        registry.Setup(r => r.GetMetadataProviderEntries()).Returns([]);
        return new FileScanService(db, registry.Object, null!, null!, new ImportProgressService(), null!);
    }

    [Fact]
    public async Task TheFlatGroupsFromTwoYearFolders_CarryTheirYear()
    {
        var svc = new Chronicle.Services.Scan.ScanGroupingService(new Chronicle.Services.Scan.FolderSignalExtractor(), new Chronicle.Services.Scan.TagSignalExtractor());

        var groups = svc.Group(["H:/Movies/The Exorcist (1973)/The Exorcist (1973).mkv", "H:/Movies/The Exorcist (2023)/The Exorcist - Believer (2023).mkv", "H:/Movies/No Year/No Year.mkv"], "H:/Movies", 1).Groups;

        Assert.Equal(1973, groups.Single(g => g.Name == "The Exorcist (1973)").Year);
        Assert.Equal(2023, groups.Single(g => g.Name == "The Exorcist (2023)").Year);
        Assert.Null(groups.Single(g => g.Name == "No Year").Year);
    }

    [Fact]
    public async Task ScanningTheRemake_DoesNotTakeOverTheOriginalsFile()
    {
        await using var db = NewContext();
        db.MediaTypes.Add(new MediaType { Id = 2, Name = "movies", DisplayName = "Movies", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var original = new ScanGroupImport("The Exorcist (1973)", 1973, null, [], ["H:/Movies/The Exorcist (1973)/The Exorcist (1973).mkv"], "H:/Movies/The Exorcist (1973)");
        var remake = new ScanGroupImport("The Exorcist (2023)", 2023, null, [], ["H:/Movies/The Exorcist (2023)/The Exorcist - Believer (2023).mkv"], "H:/Movies/The Exorcist (2023)");

        // Run it twice, in both orders, as nightly scans do.
        await Service(db).ImportGroupsAsync(new ImportGroupsRequest([original, remake], 2), [1], manageProgress: false);
        await Service(db).ImportGroupsAsync(new ImportGroupsRequest([remake, original], 2), [1], manageProgress: false);

        var items = await db.MediaItems.Where(m => m.HierarchyLevel == 0 && m.MediaTypeId == 2).ToListAsync();
        Assert.Equal(2, items.Count);
        Assert.Contains(items, m => m.Year == 1973 && m.MetadataJson!.Contains("(1973)") && !m.MetadataJson.Contains("(2023)"));
        Assert.Contains(items, m => m.Year == 2023 && m.MetadataJson!.Contains("(2023)") && !m.MetadataJson.Contains("(1973)"));
    }

    [Fact]
    public async Task AnItemWithAWrongFileFromAnEarlierScan_IsHealedByTheNextScan()
    {
        await using var db = NewContext();
        db.MediaTypes.Add(new MediaType { Id = 2, Name = "movies", DisplayName = "Movies", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow });
        db.MediaItems.Add(new MediaItem
        {
            Id = 10, MediaTypeId = 2, Name = "How to Train Your Dragon", Year = 2010, HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            MetadataJson = "{\"fileScanner\":{\"filePaths\":[\"M:/Movies/How to Train Your Dragon (2025)/How to Train Your Dragon (2025).mkv\"],\"folderPath\":\"M:/Movies/How to Train Your Dragon (2025)\"}}",
        });
        await db.SaveChangesAsync();
        var correct = new ScanGroupImport("How to Train Your Dragon (2010)", 2010, null, [], ["H:/Movies/How to Train Your Dragon (2010)/How to Train Your Dragon (2010).mkv"], "H:/Movies/How to Train Your Dragon (2010)");
        var twentyFive = new ScanGroupImport("How to Train Your Dragon (2025)", 2025, null, [], ["M:/Movies/How to Train Your Dragon (2025)/How to Train Your Dragon (2025).mkv"], "M:/Movies/How to Train Your Dragon (2025)");

        await Service(db).ImportGroupsAsync(new ImportGroupsRequest([correct, twentyFive], 2), [1], manageProgress: false);

        var item2010 = await db.MediaItems.SingleAsync(m => m.Year == 2010 && m.HierarchyLevel == 0);
        Assert.Contains("(2010)", item2010.MetadataJson!);
        Assert.DoesNotContain("(2025)", item2010.MetadataJson!);
        var item2025 = await db.MediaItems.SingleAsync(m => m.Year == 2025 && m.HierarchyLevel == 0);
        Assert.NotEqual(item2010.Id, item2025.Id);
    }

    [Theory]
    [InlineData(2009, 2010)]
    [InlineData(2011, 2010)]
    [InlineData(2010, 2010)]
    public async Task AFolderYearOneOffFromTheItemsYear_IsStillTheSameFilm_NotADuplicate(int folderYear, int itemYear)
    {
        await using var db = NewContext();
        db.MediaTypes.Add(new MediaType { Id = 2, Name = "movies", DisplayName = "Movies", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow });
        db.MediaItems.Add(new MediaItem { Id = 10, MediaTypeId = 2, Name = "Some Film", Year = itemYear, HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var group = new ScanGroupImport($"Some Film ({folderYear})", folderYear, null, [], [$"H:/Movies/Some Film ({folderYear})/Some Film ({folderYear}).mkv"], $"H:/Movies/Some Film ({folderYear})");

        await Service(db).ImportGroupsAsync(new ImportGroupsRequest([group], 2), [1], manageProgress: false);

        Assert.Equal(1, await db.MediaItems.CountAsync(m => m.HierarchyLevel == 0 && m.MediaTypeId == 2));
    }
}
