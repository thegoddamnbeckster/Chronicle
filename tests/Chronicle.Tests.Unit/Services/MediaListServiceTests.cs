using Chronicle.Core.Exceptions;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Tests.Unit.Services
{
    public class MediaListServiceTests : IDisposable
    {
        private readonly ChronicleDbContext _context;
        private readonly MediaListService   _service;

        private const int UserId    = 1;
        private const int MediaId   = 1;
        private const int OtherUser = 2;

        public MediaListServiceTests()
        {
            var options = new DbContextOptionsBuilder<ChronicleDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            _context = new ChronicleDbContext(options);
            _service = new MediaListService(_context);

            // Seed required FK entities
            _context.Users.AddRange(
                new User { Id = UserId,    Username = "owner",  PasswordHash = "h", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
                new User { Id = OtherUser, Username = "other",  PasswordHash = "h", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }
            );
            _context.MediaTypes.Add(new MediaType
            {
                Id = 1, Name = "tv", DisplayName = "TV Shows",
                CreatedAt = DateTime.UtcNow
            });
            _context.MediaItems.Add(new MediaItem
            {
                Id = MediaId, MediaTypeId = 1, Name = "Test Show",
                HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            _context.SaveChanges();
        }

        public void Dispose() => _context.Dispose();

        // ── Helpers ───────────────────────────────────────────────────────────

        private async Task<MediaList> CreateListAsync(string name = "My List", bool isOrdered = false) =>
            await _service.CreateAsync(UserId, new CreateListRequest(name, null, isOrdered));

        // ── CreateAsync ───────────────────────────────────────────────────────

        [Fact]
        public async Task CreateAsync_ValidRequest_PersistsList()
        {
            var list = await _service.CreateAsync(UserId,
                new CreateListRequest("  Watchlist  ", "Some desc", true));

            list.Id.Should().BeGreaterThan(0);
            list.UserId.Should().Be(UserId);
            list.Name.Should().Be("Watchlist");    // trimmed
            list.IsOrdered.Should().BeTrue();

            var stored = await _context.MediaLists.FindAsync(list.Id);
            stored.Should().NotBeNull();
        }

        // ── GetAllForUserAsync ─────────────────────────────────────────────────

        [Fact]
        public async Task GetAllForUserAsync_NewUser_ReturnsEmpty()
        {
            var lists = await _service.GetAllForUserAsync(UserId);
            lists.Should().BeEmpty();
        }

        [Fact]
        public async Task GetAllForUserAsync_ReturnsOnlyOwnLists()
        {
            await _service.CreateAsync(UserId,    new CreateListRequest("Owner List",  null, false));
            await _service.CreateAsync(OtherUser, new CreateListRequest("Other List",  null, false));

            var lists = await _service.GetAllForUserAsync(UserId);
            lists.Should().HaveCount(1);
            lists.First().Name.Should().Be("Owner List");
        }

        [Fact]
        public async Task GetAllForUserAsync_ReturnsMultipleLists()
        {
            await CreateListAsync("A");
            await CreateListAsync("B");

            var lists = await _service.GetAllForUserAsync(UserId);
            lists.Should().HaveCount(2);
        }

        // ── GetByIdAsync ──────────────────────────────────────────────────────

        [Fact]
        public async Task GetByIdAsync_OwnList_ReturnsWithItems()
        {
            var created = await CreateListAsync();

            var fetched = await _service.GetByIdAsync(UserId, created.Id);
            fetched.Should().NotBeNull();
            fetched!.Id.Should().Be(created.Id);
        }

        [Fact]
        public async Task GetByIdAsync_OtherUsersListId_ReturnsNull()
        {
            var created = await _service.CreateAsync(OtherUser,
                new CreateListRequest("Private", null, false));

            var result = await _service.GetByIdAsync(UserId, created.Id);
            result.Should().BeNull();
        }

        [Fact]
        public async Task GetByIdAsync_NonExistentId_ReturnsNull()
        {
            var result = await _service.GetByIdAsync(UserId, 999999);
            result.Should().BeNull();
        }

        // ── UpdateAsync ───────────────────────────────────────────────────────

        [Fact]
        public async Task UpdateAsync_ChangeName_PersistsChange()
        {
            var list = await CreateListAsync("Original");

            var updated = await _service.UpdateAsync(UserId, list.Id,
                new UpdateListRequest("  Updated  ", null, null));

            updated.Name.Should().Be("Updated");   // trimmed
        }

        [Fact]
        public async Task UpdateAsync_ChangeIsOrdered_PersistsChange()
        {
            var list = await CreateListAsync(isOrdered: false);

            await _service.UpdateAsync(UserId, list.Id,
                new UpdateListRequest(null, null, true));

            var stored = await _context.MediaLists.FindAsync(list.Id);
            stored!.IsOrdered.Should().BeTrue();
        }

        [Fact]
        public async Task UpdateAsync_NonExistentId_ThrowsMediaListNotFoundException()
        {
            var act = async () => await _service.UpdateAsync(UserId, 999999,
                new UpdateListRequest("X", null, null));

            await act.Should().ThrowAsync<MediaListNotFoundException>();
        }

        [Fact]
        public async Task UpdateAsync_OtherUsersListId_ThrowsMediaListNotFoundException()
        {
            var other = await _service.CreateAsync(OtherUser,
                new CreateListRequest("Other", null, false));

            var act = async () => await _service.UpdateAsync(UserId, other.Id,
                new UpdateListRequest("Hijack", null, null));

            await act.Should().ThrowAsync<MediaListNotFoundException>();
        }

        // ── DeleteAsync ───────────────────────────────────────────────────────

        [Fact]
        public async Task DeleteAsync_OwnList_RemovesFromDatabase()
        {
            var list = await CreateListAsync();

            await _service.DeleteAsync(UserId, list.Id);

            var stored = await _context.MediaLists.FindAsync(list.Id);
            stored.Should().BeNull();
        }

        [Fact]
        public async Task DeleteAsync_NonExistentId_ThrowsMediaListNotFoundException()
        {
            var act = async () => await _service.DeleteAsync(UserId, 999999);
            await act.Should().ThrowAsync<MediaListNotFoundException>();
        }

        // ── AddItemAsync ──────────────────────────────────────────────────────

        [Fact]
        public async Task AddItemAsync_ValidItem_PersistsItem()
        {
            var list = await CreateListAsync();

            var item = await _service.AddItemAsync(UserId, list.Id,
                new AddItemToListRequest(MediaId, 0, null));

            item.Id.Should().BeGreaterThan(0);
            item.MediaItemId.Should().Be(MediaId);
        }

        [Fact]
        public async Task AddItemAsync_DuplicateItem_ThrowsDuplicateListItemException()
        {
            var list = await CreateListAsync();

            await _service.AddItemAsync(UserId, list.Id,
                new AddItemToListRequest(MediaId, 0, null));

            var act = async () => await _service.AddItemAsync(UserId, list.Id,
                new AddItemToListRequest(MediaId, 0, null));

            await act.Should().ThrowAsync<DuplicateListItemException>();
        }

        [Fact]
        public async Task AddItemAsync_OrderedListNoPosition_AutoAppends()
        {
            var list = await CreateListAsync(isOrdered: true);

            // Add a second media item to test sequential positioning
            _context.MediaItems.Add(new MediaItem
            {
                Id = 2, MediaTypeId = 1, Name = "Second Item",
                HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await _context.SaveChangesAsync();

            var item1 = await _service.AddItemAsync(UserId, list.Id,
                new AddItemToListRequest(MediaId, 0, null));
            var item2 = await _service.AddItemAsync(UserId, list.Id,
                new AddItemToListRequest(2, 0, null));

            item2.Position.Should().BeGreaterThan(item1.Position);
        }

        [Fact]
        public async Task AddItemAsync_NonExistentList_ThrowsMediaListNotFoundException()
        {
            var act = async () => await _service.AddItemAsync(UserId, 999999,
                new AddItemToListRequest(MediaId, 0, null));

            await act.Should().ThrowAsync<MediaListNotFoundException>();
        }

        // ── RemoveItemAsync ───────────────────────────────────────────────────

        [Fact]
        public async Task RemoveItemAsync_ExistingItem_RemovesFromDatabase()
        {
            var list = await CreateListAsync();
            var item = await _service.AddItemAsync(UserId, list.Id,
                new AddItemToListRequest(MediaId, 0, null));

            await _service.RemoveItemAsync(UserId, list.Id, item.Id);

            var stored = await _context.MediaListItems.FindAsync(item.Id);
            stored.Should().BeNull();
        }

        [Fact]
        public async Task RemoveItemAsync_NonExistentItem_ThrowsMediaListItemNotFoundException()
        {
            var list = await CreateListAsync();

            var act = async () => await _service.RemoveItemAsync(UserId, list.Id, 999999);
            await act.Should().ThrowAsync<MediaListItemNotFoundException>();
        }

        // ── ReorderAsync ──────────────────────────────────────────────────────

        [Fact]
        public async Task ReorderAsync_ValidPositions_UpdatesPositions()
        {
            var list = await CreateListAsync(isOrdered: true);

            _context.MediaItems.Add(new MediaItem
            {
                Id = 3, MediaTypeId = 1, Name = "Third Item",
                HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await _context.SaveChangesAsync();

            var item1 = await _service.AddItemAsync(UserId, list.Id,
                new AddItemToListRequest(MediaId, 1, null));
            var item2 = await _service.AddItemAsync(UserId, list.Id,
                new AddItemToListRequest(3, 2, null));

            // Swap positions
            await _service.ReorderAsync(UserId, list.Id, new[]
            {
                new ReorderItem(item1.Id, 2),
                new ReorderItem(item2.Id, 1)
            });

            var stored1 = await _context.MediaListItems.FindAsync(item1.Id);
            var stored2 = await _context.MediaListItems.FindAsync(item2.Id);
            stored1!.Position.Should().Be(2);
            stored2!.Position.Should().Be(1);
        }

        [Fact]
        public async Task ReorderAsync_NonExistentList_ThrowsMediaListNotFoundException()
        {
            var act = async () => await _service.ReorderAsync(UserId, 999999,
                new[] { new ReorderItem(1, 0) });

            await act.Should().ThrowAsync<MediaListNotFoundException>();
        }
    }
}
