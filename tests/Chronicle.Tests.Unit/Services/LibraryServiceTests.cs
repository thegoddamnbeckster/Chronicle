using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

public class LibraryServiceTests
{
    private static ChronicleDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ChronicleDbContext(opts);
    }

    // ── ClearAllAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ClearAllAsync_EmptyLibrary_ReturnsZero()
    {
        var db = MakeDb();
        var svc = new LibraryService(db);

        var removed = await svc.ClearAllAsync(userId: 1);

        Assert.Equal(0, removed);
    }

    [Fact]
    public async Task ClearAllAsync_ExclusiveItems_DeletesLibraryEntriesAndMediaItems()
    {
        var db = MakeDb();

        // Arrange: media type + 2 exclusive media items for user 1
        var mt = new MediaType { Name = "Movies", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow };
        db.MediaTypes.Add(mt);
        await db.SaveChangesAsync();

        var item1 = new MediaItem { Name = "A", MediaTypeId = mt.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var item2 = new MediaItem { Name = "B", MediaTypeId = mt.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MediaItems.AddRange(item1, item2);
        await db.SaveChangesAsync();

        db.UserLibraries.AddRange(
            new UserLibrary { UserId = 1, MediaItemId = item1.Id, Status = LibraryStatus.Completed, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new UserLibrary { UserId = 1, MediaItemId = item2.Id, Status = LibraryStatus.Completed, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var svc = new LibraryService(db);

        // Act
        var removed = await svc.ClearAllAsync(userId: 1);

        // Assert
        Assert.Equal(2, removed);
        Assert.Empty(db.UserLibraries.Where(l => l.UserId == 1));
        Assert.Empty(db.MediaItems); // both items deleted because they're exclusive to user 1
    }

    [Fact]
    public async Task ClearAllAsync_SharedItem_PreservesMediaItemButRemovesLibraryEntry()
    {
        var db = MakeDb();

        var mt = new MediaType { Name = "Movies", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow };
        db.MediaTypes.Add(mt);
        await db.SaveChangesAsync();

        var sharedItem = new MediaItem { Name = "Shared", MediaTypeId = mt.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MediaItems.Add(sharedItem);
        await db.SaveChangesAsync();

        // Both user 1 and user 2 have this item
        db.UserLibraries.AddRange(
            new UserLibrary { UserId = 1, MediaItemId = sharedItem.Id, Status = LibraryStatus.Completed, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new UserLibrary { UserId = 2, MediaItemId = sharedItem.Id, Status = LibraryStatus.Completed, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var svc = new LibraryService(db);

        // Act: user 1 clears their library
        var removed = await svc.ClearAllAsync(userId: 1);

        // Assert: user 1's library entry gone, MediaItem preserved, user 2's entry intact
        Assert.Equal(1, removed);
        Assert.Empty(db.UserLibraries.Where(l => l.UserId == 1));
        Assert.Single(db.UserLibraries.Where(l => l.UserId == 2));
        Assert.Single(db.MediaItems); // preserved because it's shared
    }

    [Fact]
    public async Task ClearAllAsync_HierarchicalItems_DeletesDescendants()
    {
        var db = MakeDb();

        var mt = new MediaType { Name = "TV", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow };
        db.MediaTypes.Add(mt);
        await db.SaveChangesAsync();

        // Show → Season → Episode (all exclusive to user 1)
        var show    = new MediaItem { Name = "Show",    MediaTypeId = mt.Id, HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MediaItems.Add(show);
        await db.SaveChangesAsync();

        var season  = new MediaItem { Name = "Season 1", MediaTypeId = mt.Id, HierarchyLevel = 1, ParentId = show.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MediaItems.Add(season);
        await db.SaveChangesAsync();

        var episode = new MediaItem { Name = "Ep 1",    MediaTypeId = mt.Id, HierarchyLevel = 2, ParentId = season.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MediaItems.Add(episode);
        await db.SaveChangesAsync();

        // Library entry only on the show (root)
        db.UserLibraries.Add(new UserLibrary { UserId = 1, MediaItemId = show.Id, Status = LibraryStatus.Completed, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var svc = new LibraryService(db);

        // Act
        var removed = await svc.ClearAllAsync(userId: 1);

        // Assert: all 3 items deleted
        Assert.Equal(1, removed); // 1 library entry removed
        Assert.Empty(db.MediaItems); // show + season + episode all gone
    }

    // ── GetForUserAsync rootOnly ───────────────────────────────────────────────

    [Fact]
    public async Task GetForUserAsync_RootOnly_ReturnsOnlyRootItems()
    {
        var db = MakeDb();

        var mt = new MediaType { Name = "TV", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow };
        db.MediaTypes.Add(mt);
        await db.SaveChangesAsync();

        var show    = new MediaItem { Name = "Show",     MediaTypeId = mt.Id, HierarchyLevel = 0, ParentId = null, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var season  = new MediaItem { Name = "Season 1", MediaTypeId = mt.Id, HierarchyLevel = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MediaItems.AddRange(show, season);
        await db.SaveChangesAsync();

        // Set up parent after save so we have show.Id
        season.ParentId = show.Id;
        await db.SaveChangesAsync();

        db.UserLibraries.AddRange(
            new UserLibrary { UserId = 1, MediaItemId = show.Id,   Status = LibraryStatus.Completed, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new UserLibrary { UserId = 1, MediaItemId = season.Id, Status = LibraryStatus.Completed, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var svc = new LibraryService(db);

        // Act
        var results = (await svc.GetForUserAsync(1, rootOnly: true)).ToList();

        // Assert: only the show (root) returned, not the season (child)
        Assert.Single(results);
        Assert.Equal(show.Id, results[0].MediaItemId);
    }
}
