using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Live;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Tests.Unit.Services;

public class LibraryChangeFeedTests
{
    private readonly LibraryChangeFeed _feed = new();

    private static (int, string, int?) Changed(int id, int? user = null) => (id, ChangeKinds.Changed, user);

    [Fact]
    public void TheFirstCall_OnlyReportsWhereThingsStand()
    {
        _feed.Publish([Changed(1)]);

        var set = _feed.GetSince(null, null, 7);

        set.Reset.Should().BeFalse();
        set.Changes.Should().BeEmpty("there is no baseline to compare with yet");
        set.Revision.Should().Be(1);
        set.Epoch.Should().Be(_feed.Epoch);
    }

    [Fact]
    public void AClientIsToldOnlyWhatChangedAfterItsRevision()
    {
        _feed.Publish([Changed(1), Changed(2)]);
        var baseline = _feed.Revision;
        _feed.Publish([Changed(3)]);

        var set = _feed.GetSince(baseline, _feed.Epoch, 7);

        set.Changes.Select(c => c.ItemId).Should().Equal(3);
        set.Revision.Should().Be(baseline + 1);
        set.Reset.Should().BeFalse();
    }

    [Fact]
    public void AnItemThatChangedManyTimes_IsReportedOnce_WithTheLatestKind()
    {
        var baseline = _feed.Revision;
        _feed.Publish([Changed(5), Changed(5), (5, ChangeKinds.Deleted, null)]);

        var changes = _feed.GetSince(baseline, _feed.Epoch, 7).Changes;

        changes.Should().ContainSingle().Which.Should().Be(new ChangedItem(5, ChangeKinds.Deleted));
    }

    [Fact]
    public void NothingNew_MeansAnEmptyAnswer()
    {
        _feed.Publish([Changed(1)]);

        _feed.GetSince(_feed.Revision, _feed.Epoch, 7).Changes.Should().BeEmpty();
    }

    [Fact]
    public void APersonsOwnLibraryChange_IsShownOnlyToThem()
    {
        var baseline = _feed.Revision;
        _feed.Publish([Changed(10, user: 1), Changed(11)]);

        _feed.GetSince(baseline, _feed.Epoch, 1).Changes.Select(c => c.ItemId).Should().BeEquivalentTo([10, 11]);
        _feed.GetSince(baseline, _feed.Epoch, 2).Changes.Select(c => c.ItemId).Should().Equal(11);
    }

    [Fact]
    public void ADifferentEpoch_MeansTheServerRestarted_SoTheClientMustRefetch()
    {
        _feed.Publish([Changed(1)]);

        var set = _feed.GetSince(0, Guid.NewGuid(), 7);

        set.Reset.Should().BeTrue();
        set.Changes.Should().BeEmpty();
        set.Epoch.Should().Be(_feed.Epoch, "it is told the new epoch to continue from");
    }

    [Fact]
    public void AClientThatWaitedTooLong_IsToldToRefetch_NotGivenAHoleyList()
    {
        var old = _feed.Revision;
        for (var i = 0; i < LibraryChangeFeed.Capacity + 10; i++) _feed.Publish([Changed(i)]);

        _feed.GetSince(old, _feed.Epoch, 7).Reset.Should().BeTrue();
        _feed.GetSince(_feed.Revision - 5, _feed.Epoch, 7).Reset.Should().BeFalse("a recent client is still fine");
    }

    [Fact]
    public void TheBufferIsBounded()
    {
        for (var i = 0; i < LibraryChangeFeed.Capacity * 2; i++) _feed.Publish([Changed(i)]);

        _feed.GetSince(_feed.Revision - 100, _feed.Epoch, 7).Changes.Should().HaveCount(100);
    }

    [Fact]
    public void ABulkChange_ForcesEveryClientToRefetchOnce()
    {
        _feed.Publish([Changed(1)]);
        var before = _feed.Revision;

        _feed.MarkAllChanged();

        _feed.GetSince(before, _feed.Epoch, 7).Reset.Should().BeTrue();
        var after = _feed.GetSince(before, _feed.Epoch, 7).Revision;
        _feed.GetSince(after, _feed.Epoch, 7).Reset.Should().BeFalse("a client that has refetched and caught up carries on normally");
    }

    [Fact]
    public async Task ManyThreadsPublishingAndReading_NeverCorruptsIt()
    {
        var writers = Enumerable.Range(0, 8).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++) _feed.Publish([Changed(w * 10000 + i)]);
        }));
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++) _feed.GetSince(_feed.Revision - 50, _feed.Epoch, 1);
        });

        await Task.WhenAll(writers.Append(reader));

        _feed.Revision.Should().Be(16000);
    }
}

public class LibraryChangeInterceptorTests : IDisposable
{
    private readonly LibraryChangeFeed _feed = new();
    private readonly ChronicleDbContext _db;
    private readonly long _start;

    public LibraryChangeInterceptorTests()
    {
        _db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(new LibraryChangeInterceptor(_feed)).Options);
        _db.MediaTypes.Add(new MediaType { Id = 1, Name = "movies", DisplayName = "Movies", CreatedAt = DateTime.UtcNow });
        _db.Users.Add(new User { Id = 1, Username = "a", PasswordHash = "x", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        _db.SaveChanges();
        _start = _feed.Revision;
    }

    public void Dispose() => _db.Dispose();

    private static MediaItem NewItem(string name) => new() { MediaTypeId = 1, Name = name, HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };

    private IReadOnlyList<ChangedItem> Since(long rev, int user = 99) => _feed.GetSince(rev, _feed.Epoch, user).Changes;

    [Fact]
    public async Task ANewItem_IsReportedWithTheIdTheDatabaseAssigned()
    {
        var item = NewItem("Fresh");
        _db.MediaItems.Add(item);

        await _db.SaveChangesAsync();

        item.Id.Should().BeGreaterThan(0);
        Since(_start).Should().ContainSingle().Which.Should().Be(new ChangedItem(item.Id, ChangeKinds.Changed));
    }

    [Fact]
    public async Task AnEditedItem_IsReported()
    {
        var item = NewItem("Before");
        _db.MediaItems.Add(item);
        await _db.SaveChangesAsync();
        var rev = _feed.Revision;

        item.PosterUrl = "https://img/p.jpg";
        await _db.SaveChangesAsync();

        Since(rev).Should().ContainSingle().Which.ItemId.Should().Be(item.Id);
    }

    [Fact]
    public async Task ADeletedItem_IsReportedAsDeleted()
    {
        var item = NewItem("Doomed");
        _db.MediaItems.Add(item);
        await _db.SaveChangesAsync();
        var rev = _feed.Revision;

        _db.MediaItems.Remove(item);
        await _db.SaveChangesAsync();

        Since(rev).Should().ContainSingle().Which.Should().Be(new ChangedItem(item.Id, ChangeKinds.Deleted));
    }

    [Fact]
    public async Task ALibraryEntryChange_IsReportedForTheItem_ToThatPersonOnly()
    {
        var item = NewItem("Mine");
        _db.MediaItems.Add(item);
        await _db.SaveChangesAsync();
        var rev = _feed.Revision;

        _db.UserLibraries.Add(new UserLibrary { UserId = 1, MediaItemId = item.Id, Status = LibraryStatus.Watching, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        Since(rev, user: 1).Should().ContainSingle().Which.ItemId.Should().Be(item.Id);
        Since(rev, user: 2).Should().BeEmpty("it is person 1's entry");
    }

    [Fact]
    public async Task ChangesToOtherThings_AreNotReported()
    {
        _db.AppSettings.Add(new AppSetting { Key = "x", Value = "y" });
        _db.Notifications.Add(new Notification { UserId = 1, Kind = NotificationKinds.TaskFailed, Title = "t", CreatedAt = DateTime.UtcNow });

        await _db.SaveChangesAsync();

        _feed.Revision.Should().Be(_start);
    }

    [Fact]
    public async Task ASaveThatChangesNothing_ReportsNothing()
    {
        await _db.SaveChangesAsync();

        _feed.Revision.Should().Be(_start);
    }

    [Fact]
    public async Task SeveralItemsInOneSave_AreAllReported()
    {
        _db.MediaItems.AddRange(NewItem("A"), NewItem("B"), NewItem("C"));

        await _db.SaveChangesAsync();

        Since(_start).Should().HaveCount(3);
    }

    [Fact]
    public void TheSynchronousSave_IsCoveredToo()
    {
        var item = NewItem("Sync");
        _db.MediaItems.Add(item);

        _db.SaveChanges();

        Since(_start).Should().ContainSingle().Which.ItemId.Should().Be(item.Id);
    }
}
