using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// Covers SyncOrchestrationService.UpsertRatingAsync -- confirmed real bug (2026-08-30, per-user
/// report "are these ratings being stored or dropped? they better be getting stored"): a Trakt/
/// Simkl rating for an item that IS matched in Chronicle's catalog but has no UserLibrary row
/// yet was silently dropped, unlike the sibling UpsertWatchEventAsync/UpsertWatchlistStatusAsync
/// methods, which both already create a missing row rather than giving up.
/// </summary>
public class SyncOrchestrationServiceTests : IDisposable
{
    private readonly ChronicleDbContext _db;

    public SyncOrchestrationServiceTests()
    {
        var opts = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new ChronicleDbContext(opts);
    }

    private static ImportedRating MakeRating(string externalId, int rating) => new(
        ExternalId: externalId,
        AdditionalIds: new Dictionary<string, string>(),
        MediaType: "movie",
        Title: "Some Movie",
        Year: 2020,
        Rating: rating,
        RatedAt: DateTimeOffset.UtcNow);

    [Fact]
    public async Task UpsertRatingAsync_NoExistingUserLibraryRow_CreatesOneInsteadOfDropping()
    {
        var movieType = new MediaType { Id = 1, Name = "movies", DisplayName = "Movies", CreatedAt = DateTime.UtcNow };
        _db.MediaTypes.Add(movieType);
        var movie = new MediaItem
        {
            Id = 500, MediaTypeId = 1, Name = "Some Movie", HierarchyLevel = 0,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.MediaItems.Add(movie);
        _db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = movie.Id, Source = "trakt", ExternalId = "trakt:999" });
        await _db.SaveChangesAsync();

        // No UserLibrary row exists yet for (userId=1, movie.Id) -- the exact scenario that
        // used to silently drop the rating.
        await SyncOrchestrationService.UpsertRatingAsync(
            _db, MakeRating("trakt:999", 8), "chronicle.plugin.trakt", userId: 1, default);

        var lib = await _db.UserLibraries.SingleOrDefaultAsync(l => l.UserId == 1 && l.MediaItemId == movie.Id);
        lib.Should().NotBeNull();
        lib!.UserRating.Should().Be(8);
        lib.Status.Should().Be(LibraryStatus.Completed);
    }

    [Fact]
    public async Task UpsertRatingAsync_ExistingUserLibraryRow_UpdatesRatingWithoutChangingStatus()
    {
        var movieType = new MediaType { Id = 1, Name = "movies", DisplayName = "Movies", CreatedAt = DateTime.UtcNow };
        _db.MediaTypes.Add(movieType);
        var movie = new MediaItem
        {
            Id = 501, MediaTypeId = 1, Name = "Some Movie", HierarchyLevel = 0,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.MediaItems.Add(movie);
        _db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = movie.Id, Source = "trakt", ExternalId = "trakt:1000" });
        _db.UserLibraries.Add(new UserLibrary
        {
            UserId = 1, MediaItemId = movie.Id, Status = LibraryStatus.Watching,
            AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        await SyncOrchestrationService.UpsertRatingAsync(
            _db, MakeRating("trakt:1000", 9), "chronicle.plugin.trakt", userId: 1, default);

        var lib = await _db.UserLibraries.SingleAsync(l => l.UserId == 1 && l.MediaItemId == movie.Id);
        lib.UserRating.Should().Be(9);
        lib.Status.Should().Be(LibraryStatus.Watching); // untouched -- only the rating changes
    }

    [Fact]
    public async Task UpsertRatingAsync_NoMatchingMediaItem_NoOp()
    {
        await SyncOrchestrationService.UpsertRatingAsync(
            _db, MakeRating("trakt:doesnotexist", 5), "chronicle.plugin.trakt", userId: 1, default);

        (await _db.UserLibraries.CountAsync()).Should().Be(0);
    }

    public void Dispose() => _db.Dispose();
}
