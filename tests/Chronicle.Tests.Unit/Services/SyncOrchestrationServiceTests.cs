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

    private static ImportedRating MakeRating(string externalId, int rating, DateTimeOffset? ratedAt = null) => new(
        ExternalId: externalId,
        AdditionalIds: new Dictionary<string, string>(),
        MediaType: "movie",
        Title: "Some Movie",
        Year: 2020,
        Rating: rating,
        RatedAt: ratedAt ?? DateTimeOffset.UtcNow);

    private static ImportedPlaybackProgress MakeProgress(string externalId, double percent, DateTimeOffset? updatedAt = null) => new(
        ExternalId: externalId,
        AdditionalIds: new Dictionary<string, string>(),
        MediaType: "movie",
        Title: "Some Movie",
        Year: 2020,
        ProgressPercent: percent,
        UpdatedAt: updatedAt ?? DateTimeOffset.UtcNow);

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

    // ── "most recent wins" (2026-08-30 per-user request) ────────────────────────

    [Fact]
    public async Task UpsertRatingAsync_IncomingOlderThanStored_DoesNotOverwrite()
    {
        var movie = SeedMovie(502, "trakt", "trakt:1002");
        var newer = DateTimeOffset.UtcNow;
        _db.UserLibraries.Add(new UserLibrary
        {
            UserId = 1, MediaItemId = movie.Id, Status = LibraryStatus.Completed,
            UserRating = 10, UserRatingUpdatedAt = newer.UtcDateTime,
            AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        // A stale Trakt sync trying to apply a rating from before the current one was set.
        await SyncOrchestrationService.UpsertRatingAsync(
            _db, MakeRating("trakt:1002", 3, newer.AddDays(-1)), "chronicle.plugin.trakt", userId: 1, default);

        var lib = await _db.UserLibraries.SingleAsync(l => l.MediaItemId == movie.Id);
        lib.UserRating.Should().Be(10); // untouched
    }

    [Fact]
    public async Task UpsertRatingAsync_IncomingNewerThanStored_Overwrites()
    {
        var movie = SeedMovie(503, "trakt", "trakt:1003");
        var older = DateTimeOffset.UtcNow.AddDays(-5);
        _db.UserLibraries.Add(new UserLibrary
        {
            UserId = 1, MediaItemId = movie.Id, Status = LibraryStatus.Completed,
            UserRating = 5, UserRatingUpdatedAt = older.UtcDateTime,
            AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        await SyncOrchestrationService.UpsertRatingAsync(
            _db, MakeRating("trakt:1003", 9, DateTimeOffset.UtcNow), "chronicle.plugin.trakt", userId: 1, default);

        var lib = await _db.UserLibraries.SingleAsync(l => l.MediaItemId == movie.Id);
        lib.UserRating.Should().Be(9);
    }

    [Fact]
    public async Task UpsertRatingAsync_NoStoredTimestampYet_AlwaysApplies()
    {
        // A rating set before UserRatingUpdatedAt existed (or by a source with no real
        // timestamp of its own) must not be able to permanently block every future sync.
        var movie = SeedMovie(504, "trakt", "trakt:1004");
        _db.UserLibraries.Add(new UserLibrary
        {
            UserId = 1, MediaItemId = movie.Id, Status = LibraryStatus.Completed,
            UserRating = 5, UserRatingUpdatedAt = null,
            AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        await SyncOrchestrationService.UpsertRatingAsync(
            _db, MakeRating("trakt:1004", 7, DateTimeOffset.MinValue), "chronicle.plugin.trakt", userId: 1, default);

        var lib = await _db.UserLibraries.SingleAsync(l => l.MediaItemId == movie.Id);
        lib.UserRating.Should().Be(7);
    }

    // ── UpsertPlaybackProgressAsync ───────────────────────────────────────────────

    [Fact]
    public async Task UpsertPlaybackProgressAsync_NoExistingRow_CreatesWatchingWithPosition()
    {
        var movie = SeedMovie(505, "trakt", "trakt:1005");

        var applied = await SyncOrchestrationService.UpsertPlaybackProgressAsync(
            _db, MakeProgress("trakt:1005", 42.5), "chronicle.plugin.trakt", userId: 1, default);

        applied.Should().BeTrue();
        var lib = await _db.UserLibraries.SingleAsync(l => l.MediaItemId == movie.Id);
        lib.Status.Should().Be(LibraryStatus.Watching);
        lib.ResumePositionPercent.Should().Be(42.5);
        lib.ResumeUpdatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task UpsertPlaybackProgressAsync_OlderThanStored_SkipsAndReturnsFalse()
    {
        var movie = SeedMovie(506, "trakt", "trakt:1006");
        var newer = DateTimeOffset.UtcNow;
        _db.UserLibraries.Add(new UserLibrary
        {
            UserId = 1, MediaItemId = movie.Id, Status = LibraryStatus.Watching,
            ResumePositionPercent = 80.0, ResumeUpdatedAt = newer.UtcDateTime,
            AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var applied = await SyncOrchestrationService.UpsertPlaybackProgressAsync(
            _db, MakeProgress("trakt:1006", 10.0, newer.AddHours(-1)), "chronicle.plugin.trakt", userId: 1, default);

        applied.Should().BeFalse();
        var lib = await _db.UserLibraries.SingleAsync(l => l.MediaItemId == movie.Id);
        lib.ResumePositionPercent.Should().Be(80.0); // untouched -- a live scrobble is ahead of this stale sync
    }

    [Fact]
    public async Task UpsertPlaybackProgressAsync_CompletedStatus_NeverDowngraded()
    {
        var movie = SeedMovie(507, "trakt", "trakt:1007");
        _db.UserLibraries.Add(new UserLibrary
        {
            UserId = 1, MediaItemId = movie.Id, Status = LibraryStatus.Completed,
            AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        await SyncOrchestrationService.UpsertPlaybackProgressAsync(
            _db, MakeProgress("trakt:1007", 15.0), "chronicle.plugin.trakt", userId: 1, default);

        var lib = await _db.UserLibraries.SingleAsync(l => l.MediaItemId == movie.Id);
        lib.Status.Should().Be(LibraryStatus.Completed); // untouched
        lib.ResumePositionPercent.Should().Be(15.0);      // position itself still applies
    }

    [Fact]
    public async Task UpsertPlaybackProgressAsync_NoMatchingMediaItem_ReturnsFalse()
    {
        var applied = await SyncOrchestrationService.UpsertPlaybackProgressAsync(
            _db, MakeProgress("trakt:doesnotexist", 50.0), "chronicle.plugin.trakt", userId: 1, default);

        applied.Should().BeFalse();
        (await _db.UserLibraries.CountAsync()).Should().Be(0);
    }

    private MediaItem SeedMovie(int id, string source, string externalId)
    {
        if (!_db.MediaTypes.Any(t => t.Id == 1))
            _db.MediaTypes.Add(new MediaType { Id = 1, Name = "movies", DisplayName = "Movies", CreatedAt = DateTime.UtcNow });
        var movie = new MediaItem
        {
            Id = id, MediaTypeId = 1, Name = "Some Movie", HierarchyLevel = 0,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.MediaItems.Add(movie);
        _db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = movie.Id, Source = source, ExternalId = externalId });
        _db.SaveChanges();
        return movie;
    }

    public void Dispose() => _db.Dispose();
}
