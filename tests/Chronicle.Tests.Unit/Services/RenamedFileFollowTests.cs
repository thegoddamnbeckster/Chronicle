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
