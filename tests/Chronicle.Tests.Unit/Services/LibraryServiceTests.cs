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
        var svc = new LibraryService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryService>.Instance);

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

        var svc = new LibraryService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryService>.Instance);

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

        var svc = new LibraryService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryService>.Instance);

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

        var svc = new LibraryService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryService>.Instance);

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

        var svc = new LibraryService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryService>.Instance);

        // Act
        var results = (await svc.GetForUserAsync(1, rootOnly: true)).ToList();

        // Assert: only the show (root) returned, not the season (child)
        Assert.Single(results);
        Assert.Equal(show.Id, results[0].MediaItemId);
    }

    // ── IsTrackable ("people" and other reference/catalog types) ────────────────

    [Fact]
    public async Task GetForUserAsync_NonTrackableMediaType_NeitherReturnedNorAutoTracked()
    {
        var db = MakeDb();

        var trackable = new MediaType { Name = "movies", HierarchyLevels = 1, IsTrackable = true, CreatedAt = DateTime.UtcNow };
        var people    = new MediaType { Name = "people", HierarchyLevels = 1, IsTrackable = false, CreatedAt = DateTime.UtcNow };
        db.MediaTypes.AddRange(trackable, people);
        await db.SaveChangesAsync();

        var movie  = new MediaItem { Name = "A Movie",  MediaTypeId = trackable.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var person = new MediaItem { Name = "Some Actor", MediaTypeId = people.Id,  CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MediaItems.AddRange(movie, person);
        await db.SaveChangesAsync();

        var svc = new LibraryService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryService>.Instance);

        // Act: neither item has a UserLibrary row yet -- GetForUserAsync auto-creates
        // Unwatched rows for every root item it returns.
        var results = (await svc.GetForUserAsync(1, rootOnly: true)).ToList();

        // Assert: only the trackable movie comes back, and only it got an auto-created row.
        Assert.Single(results);
        Assert.Equal(movie.Id, results[0].MediaItemId);
        Assert.False(await db.UserLibraries.AnyAsync(l => l.MediaItemId == person.Id));
    }

    // ── GetForUserAsync performance rewrite (2026-09-07) ─────────────────────────
    // Root-caused live as the actual cause of "loading is still comparatively slow": the
    // previous implementation loaded every trackable MediaItem in the whole catalog before
    // sorting/filtering/paging in memory (13.8s for ~88K items on this user's real catalog).
    // The rewrite pushes the LEFT JOIN, status filter, sort, and Skip/Take to the database and
    // only auto-creates/materializes the current page's items -- these tests lock in the two
    // behaviors that change under the hood even though the public contract doesn't.

    [Fact]
    public async Task GetForUserAsync_OnlyAutoCreatesRowsForTheCurrentPage_NotTheWholeCatalog()
    {
        var db = MakeDb();

        var mt = new MediaType { Name = "movies", HierarchyLevels = 1, IsTrackable = true, CreatedAt = DateTime.UtcNow };
        db.MediaTypes.Add(mt);
        await db.SaveChangesAsync();

        // 5 untracked items, none with a UserLibrary row yet.
        var items = Enumerable.Range(1, 5)
            .Select(i => new MediaItem { Name = $"Movie {i}", MediaTypeId = mt.Id, CreatedAt = DateTime.UtcNow.AddMinutes(-i), UpdatedAt = DateTime.UtcNow })
            .ToList();
        db.MediaItems.AddRange(items);
        await db.SaveChangesAsync();

        var svc = new LibraryService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryService>.Instance);

        // Act: ask for just 1 item on page 1.
        var results = (await svc.GetForUserAsync(1, page: 1, perPage: 1)).ToList();

        // Assert: exactly one UserLibrary row now exists -- the previous implementation would
        // have auto-created all 5 up front regardless of perPage.
        Assert.Single(results);
        Assert.Equal(1, await db.UserLibraries.CountAsync(l => l.UserId == 1));
    }

    [Fact]
    public async Task GetForUserAsync_SortsTrackedAndUntrackedItemsTogetherByEffectiveUpdatedAt()
    {
        var db = MakeDb();

        var mt = new MediaType { Name = "movies", HierarchyLevels = 1, IsTrackable = true, CreatedAt = DateTime.UtcNow };
        db.MediaTypes.Add(mt);
        await db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        // Tracked item, updated recently -- should sort first.
        var recentlyTracked = new MediaItem { Name = "Recently Tracked", MediaTypeId = mt.Id, CreatedAt = now.AddDays(-10), UpdatedAt = now };
        // Untracked item, but created very recently -- falls back to MediaItem.CreatedAt for
        // sorting (never touched, so it's neither "just updated" nor arbitrarily first).
        var recentlyAddedUntracked = new MediaItem { Name = "Recently Added, Untracked", MediaTypeId = mt.Id, CreatedAt = now.AddMinutes(-1), UpdatedAt = now };
        // Tracked item, updated long ago -- should sort last.
        var staleTracked = new MediaItem { Name = "Stale Tracked", MediaTypeId = mt.Id, CreatedAt = now.AddDays(-30), UpdatedAt = now };
        db.MediaItems.AddRange(recentlyTracked, recentlyAddedUntracked, staleTracked);
        await db.SaveChangesAsync();

        db.UserLibraries.AddRange(
            new UserLibrary { UserId = 1, MediaItemId = recentlyTracked.Id, Status = LibraryStatus.Watching, AddedAt = now.AddDays(-10), UpdatedAt = now },
            new UserLibrary { UserId = 1, MediaItemId = staleTracked.Id, Status = LibraryStatus.Completed, AddedAt = now.AddDays(-30), UpdatedAt = now.AddDays(-30) });
        await db.SaveChangesAsync();

        var svc = new LibraryService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryService>.Instance);

        var results = (await svc.GetForUserAsync(1, perPage: 10)).ToList();

        Assert.Equal(
            new[] { recentlyTracked.Id, recentlyAddedUntracked.Id, staleTracked.Id },
            results.Select(r => r.MediaItemId));
    }

    [Fact]
    public async Task AddAsync_NonTrackableMediaType_ThrowsNotTrackableMediaException()
    {
        var db = MakeDb();

        var people = new MediaType { Name = "people", HierarchyLevels = 1, IsTrackable = false, CreatedAt = DateTime.UtcNow };
        db.MediaTypes.Add(people);
        await db.SaveChangesAsync();

        var person = new MediaItem { Name = "Some Actor", MediaTypeId = people.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MediaItems.Add(person);
        await db.SaveChangesAsync();

        var svc = new LibraryService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryService>.Instance);

        await Assert.ThrowsAsync<Chronicle.Core.Exceptions.NotTrackableMediaException>(
            () => svc.AddAsync(1, new AddToLibraryRequest(person.Id, LibraryStatus.Watching)));
    }
}
