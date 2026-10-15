using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// The Library page asks for section counts, then one sorted page of one section. These run on real
/// SQLite (not the in-memory provider) because the point is that the ordering is built as SQL --
/// the in-memory provider would happily run any expression and hide a query that cannot translate.
/// </summary>
public sealed class LibrarySectionPagingTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly ChronicleDbContext _db;
    private readonly LibraryService _svc;
    private readonly int _movies;
    private readonly int _books;
    private readonly string _movieSection;
    private readonly string _bookSection;

    public LibrarySectionPagingTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _svc = new LibraryService(_db, NullLogger<LibraryService>.Instance);

        // Untracked items get a library row auto-created for this user, so the user has to exist.
        _db.Users.Add(new User { Id = 1, Username = "tester", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        _db.SaveChanges();

        // The context seeds the standard media types; use two of them (and make sure they are trackable).
        var movies = _db.MediaTypes.Single(t => t.Name == "movies");
        var books = _db.MediaTypes.Single(t => t.Name == "music");
        movies.IsTrackable = books.IsTrackable = true;
        _db.SaveChanges();
        _movies = movies.Id;
        _books = books.Id;
        _movieSection = movies.DisplayName;
        _bookSection = books.DisplayName;
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private MediaItem Add(int typeId, string name, int? year = null, LibraryStatus? status = null, int? rating = null, int addedDays = 0)
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var item = new MediaItem { Name = name, MediaTypeId = typeId, Year = year, CreatedAt = now, UpdatedAt = now };
        _db.MediaItems.Add(item);
        _db.SaveChanges();
        if (status is not null || rating is not null)
        {
            _db.UserLibraries.Add(new UserLibrary
            {
                UserId = 1, MediaItemId = item.Id, Status = status ?? LibraryStatus.Unwatched, UserRating = rating,
                AddedAt = now.AddDays(addedDays), UpdatedAt = now,
            });
            _db.SaveChanges();
        }
        return item;
    }

    private async Task<List<string>> Names(int typeId, LibrarySort? sort, int page = 1, int perPage = 0, LibraryStatus? status = null) =>
        (await _svc.GetForUserAsync(1, status, page, perPage, rootOnly: true, mediaTypeId: typeId, sort: sort))
            .Select(e => e.MediaItem!.Name).ToList();

    [Fact]
    public async Task Sections_CountEachMediaTypeAndHonourTheStatusFilter()
    {
        Add(_movies, "A", status: LibraryStatus.Completed);
        Add(_movies, "B");
        Add(_books, "C");

        var all = await _svc.GetSectionsAsync(1, null, rootOnly: true, includeMoviesInCollections: false, includeStubs: true);
        Assert.Equal(new[] { (_bookSection, 1), (_movieSection, 2) }.OrderBy(x => x.Item1, StringComparer.CurrentCultureIgnoreCase),
            all.Select(s => (s.Name, s.Count)));

        var completed = await _svc.GetSectionsAsync(1, LibraryStatus.Completed, rootOnly: true, includeMoviesInCollections: false, includeStubs: true);
        Assert.Equal(new[] { (_movieSection, 1) }, completed.Select(s => (s.Name, s.Count)));
    }

    [Fact]
    public async Task OnlyTheRequestedSectionIsReturned()
    {
        Add(_movies, "Movie");
        Add(_books, "Book");

        Assert.Equal(new[] { "Book" }, await Names(_books, new LibrarySort(LibrarySortField.Name, false)));
    }

    [Fact]
    public async Task NameSort_IsCaseInsensitiveAndCanIgnoreLeadingArticles()
    {
        Add(_movies, "The Dark Knight");
        Add(_movies, "apple");
        Add(_movies, "Zebra");
        Add(_movies, "A Bug's Life");

        Assert.Equal(new[] { "A Bug's Life", "apple", "The Dark Knight", "Zebra" },
            await Names(_movies, new LibrarySort(LibrarySortField.Name, false)));
        Assert.Equal(new[] { "apple", "A Bug's Life", "The Dark Knight", "Zebra" },
            await Names(_movies, new LibrarySort(LibrarySortField.Name, false, new[] { "the", "a", "an" })));
        Assert.Equal(new[] { "Zebra", "The Dark Knight", "A Bug's Life", "apple" },
            await Names(_movies, new LibrarySort(LibrarySortField.Name, true, new[] { "the", "a", "an" })));
    }

    [Fact]
    public async Task Pages_AreContiguousSlicesOfTheSortedList()
    {
        for (var i = 0; i < 10; i++) Add(_movies, $"Film {i}");
        var sort = new LibrarySort(LibrarySortField.Name, false);

        var first = await Names(_movies, sort, page: 1, perPage: 4);
        var second = await Names(_movies, sort, page: 2, perPage: 4);
        var third = await Names(_movies, sort, page: 3, perPage: 4);

        Assert.Equal(new[] { "Film 0", "Film 1", "Film 2", "Film 3" }, first);
        Assert.Equal(new[] { "Film 4", "Film 5", "Film 6", "Film 7" }, second);
        Assert.Equal(new[] { "Film 8", "Film 9" }, third);
    }

    [Fact]
    public async Task EqualSortKeys_FallBackToIdSoPagesNeverOverlap()
    {
        for (var i = 0; i < 6; i++) Add(_movies, $"Same {i}", year: 2000);
        var sort = new LibrarySort(LibrarySortField.Year, false);

        var seen = (await Names(_movies, sort, 1, 3)).Concat(await Names(_movies, sort, 2, 3)).ToList();

        Assert.Equal(6, seen.Distinct().Count());
    }

    [Fact]
    public async Task YearRatingDateAddedAndStatus_SortInTheDatabase()
    {
        Add(_movies, "Old", year: 1990, status: LibraryStatus.Watching, rating: 3, addedDays: 5);
        Add(_movies, "New", year: 2020, status: LibraryStatus.Completed, rating: 9, addedDays: 1);
        Add(_movies, "Mid", year: 2005, status: LibraryStatus.Dropped, rating: 6, addedDays: 3);

        Assert.Equal(new[] { "Old", "Mid", "New" }, await Names(_movies, new LibrarySort(LibrarySortField.Year, false)));
        Assert.Equal(new[] { "New", "Mid", "Old" }, await Names(_movies, new LibrarySort(LibrarySortField.Rating, true)));
        Assert.Equal(new[] { "New", "Mid", "Old" }, await Names(_movies, new LibrarySort(LibrarySortField.DateAdded, false)));
        // alphabetical by status name: Completed, Dropped, Watching
        Assert.Equal(new[] { "New", "Mid", "Old" }, await Names(_movies, new LibrarySort(LibrarySortField.Status, false)));
    }

    [Fact]
    public async Task StatusFilter_AppliesBeforePaging()
    {
        Add(_movies, "A", status: LibraryStatus.Completed);
        Add(_movies, "B");
        Add(_movies, "C", status: LibraryStatus.Completed);

        Assert.Equal(new[] { "A", "C" },
            await Names(_movies, new LibrarySort(LibrarySortField.Name, false), 1, 5, LibraryStatus.Completed));
    }

    [Fact]
    public async Task NoSort_KeepsTheHistoricMostRecentlyTouchedFirstOrder()
    {
        var older = Add(_movies, "Older", status: LibraryStatus.Watching);
        var newer = Add(_movies, "Newer", status: LibraryStatus.Watching);
        _db.UserLibraries.Single(l => l.MediaItemId == newer.Id).UpdatedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        await _db.SaveChangesAsync();

        Assert.Equal(new[] { "Newer", "Older" }, await Names(_movies, null));
        Assert.NotEqual(older.Id, newer.Id);
    }
}
