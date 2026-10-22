using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

public class LibraryChildFlagsTests
{
    private const string WithFile = "{\"fileScanner\":{\"filePaths\":[\"E:\\\\Music\\\\a.flac\"]}}";
    private const string EmptyFiles = "{\"fileScanner\":{\"filePaths\":[]}}";
    private const string KodiFile = "{\"scraperResolvedFile\":{\"fileName\":\"x.mkv\"}}";

    private static ChronicleDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ChronicleDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static MediaItem Item(string name, int level, int? parentId, string? json = null) => new()
    {
        Name = name, MediaTypeId = 1, HierarchyLevel = level, ParentId = parentId, MetadataJson = json,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    /// <summary>An artist with one album, whose tracks are whatever <paramref name="tracks"/> makes from the album id.</summary>
    private static async Task<(ChronicleDbContext Db, int RootId)> SeedAsync(Func<int, List<MediaItem>> tracks)
    {
        var db = NewDb();
        db.MediaTypes.Add(new MediaType { Id = 1, Name = "music", DisplayName = "Music", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow });
        var root = Item("Artist", 0, null);
        db.MediaItems.Add(root);
        await db.SaveChangesAsync();
        var album = Item("Album", 1, root.Id);
        db.MediaItems.Add(album);
        await db.SaveChangesAsync();
        db.MediaItems.AddRange(tracks(album.Id));
        await db.SaveChangesAsync();
        return (db, root.Id);
    }

    [Fact]
    public async Task ATrackWithAFile_MeansTheRootHasOne()
    {
        var (db, root) = await SeedAsync(album => [Item("t1", 2, album), Item("t2", 2, album, WithFile)]);
        await using var _ = db;

        var flags = await LibraryChildFlags.LoadAsync(db, [root]);

        Assert.True(flags[root].HasChildren);
        Assert.True(flags[root].ChildrenHaveFile);
    }

    [Fact]
    public async Task TracksWithNoFile_OrAnEmptyFileList_MeanNone()
    {
        var (db, root) = await SeedAsync(album => [Item("t1", 2, album), Item("t2", 2, album, EmptyFiles)]);
        await using var _ = db;

        var flags = await LibraryChildFlags.LoadAsync(db, [root]);

        Assert.True(flags[root].HasChildren);
        Assert.False(flags[root].ChildrenHaveFile);
    }

    [Fact]
    public async Task AFileKodiResolved_Counts()
    {
        var (db, root) = await SeedAsync(album => [Item("t1", 2, album, KodiFile)]);
        await using var _ = db;

        Assert.True((await LibraryChildFlags.LoadAsync(db, [root]))[root].ChildrenHaveFile);
    }

    [Fact]
    public async Task ManyDecoyTracksBeforeTheRealOne_AreStillSkippedPast()
    {
        // 60 rows that mention "filePaths" but hold none, then one that does: found across the 25-row chunks.
        var (db, root) = await SeedAsync(album =>
            [.. Enumerable.Range(0, 60).Select(i => Item($"d{i}", 2, album, EmptyFiles)), Item("real", 2, album, WithFile)]);
        await using var _ = db;

        Assert.True((await LibraryChildFlags.LoadAsync(db, [root]))[root].ChildrenHaveFile);
    }

    [Fact]
    public async Task ARootWithNoChildren_HasNoEntry()
    {
        var (db, _) = await SeedAsync(_ => []);
        await using var _2 = db;
        var lone = Item("Lone", 0, null);
        db.MediaItems.Add(lone);
        await db.SaveChangesAsync();

        Assert.False((await LibraryChildFlags.LoadAsync(db, [lone.Id])).ContainsKey(lone.Id));
    }

    [Fact]
    public async Task WithoutGrandchildren_TheDirectChildrenAreTheLeaves()
    {
        var db = NewDb();
        await using var _ = db;
        db.MediaTypes.Add(new MediaType { Id = 1, Name = "movies", DisplayName = "Movies", HierarchyLevels = 2, CreatedAt = DateTime.UtcNow });
        var coll = Item("Collection", 0, null);
        db.MediaItems.Add(coll);
        await db.SaveChangesAsync();
        db.MediaItems.Add(Item("Film", 1, coll.Id, WithFile));
        await db.SaveChangesAsync();

        var flags = await LibraryChildFlags.LoadAsync(db, [coll.Id]);

        Assert.True(flags[coll.Id].HasChildren);
        Assert.True(flags[coll.Id].ChildrenHaveFile);
    }
}
