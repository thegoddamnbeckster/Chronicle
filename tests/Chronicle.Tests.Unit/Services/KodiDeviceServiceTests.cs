using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Tests.Unit.Services
{
    public class KodiDeviceServiceTests : IDisposable
    {
        private readonly ChronicleDbContext _context;
        private readonly KodiDeviceService _service;

        public KodiDeviceServiceTests()
        {
            var options = new DbContextOptionsBuilder<ChronicleDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            _context = new ChronicleDbContext(options);
            _service = new KodiDeviceService(_context);

            _context.Users.Add(new User
            {
                Id = 1, Username = "testuser", PasswordHash = "h",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            _context.ApiTokens.Add(new ApiToken
            {
                Id = 1, UserId = 1, Name = "device", Token = "hashed",
                CreatedAt = DateTime.UtcNow, IsActive = true,
            });
            _context.MediaTypes.Add(new MediaType { Id = 1, Name = "movies", DisplayName = "Movies", IsActive = true });
            _context.MediaItems.Add(new MediaItem
            {
                Id = 100, Name = "Test Movie", MediaTypeId = 1, HierarchyLevel = 0,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            _context.SaveChanges();
        }

        public void Dispose() => _context.Dispose();

        [Fact]
        public async Task RegisterAsync_CreatesOneDevicePerApiToken()
        {
            await _service.RegisterAsync(userId: 1, apiTokenId: 1, name: "Shield", host: "10.0.0.10", port: 8080,
                username: null, password: null);

            var devices = await _context.KodiDevices.ToListAsync();
            devices.Should().HaveCount(1);
            devices[0].Host.Should().Be("10.0.0.10");
            devices[0].Port.Should().Be(8080);
        }

        [Fact]
        public async Task RegisterAsync_CalledTwice_UpsertsTheSameRowInsteadOfDuplicating()
        {
            await _service.RegisterAsync(1, 1, "Shield", "10.0.0.10", 8080, null, null);
            // Simulates a periodic re-registration heartbeat picking up a DHCP-renewed IP.
            await _service.RegisterAsync(1, 1, "Shield", "10.0.0.99", 8080, "kodi", "secret");

            var devices = await _context.KodiDevices.ToListAsync();
            devices.Should().HaveCount(1, "re-registering the same ApiTokenId must update, not duplicate");
            devices[0].Host.Should().Be("10.0.0.99");
            devices[0].Username.Should().Be("kodi");
        }

        [Fact]
        public async Task RecordKodiIdAsync_WithNoRegisteredDevice_IsANoOp()
        {
            // Remote control off on this Kodi instance -- report_kodi_id fires anyway on every
            // ordinary scan; there's simply nothing to map it to yet.
            var act = async () => await _service.RecordKodiIdAsync(apiTokenId: 999, mediaItemId: 100, "movie", 42);

            await act.Should().NotThrowAsync();
            (await _context.KodiLibraryIds.ToListAsync()).Should().BeEmpty();
        }

        [Fact]
        public async Task RecordKodiIdAsync_CalledTwiceForSameItem_UpsertsInsteadOfDuplicating()
        {
            await _service.RegisterAsync(1, 1, "Shield", "10.0.0.10", 8080, null, null);

            await _service.RecordKodiIdAsync(1, mediaItemId: 100, "movie", kodiId: 42);
            await _service.RecordKodiIdAsync(1, mediaItemId: 100, "movie", kodiId: 43); // Kodi reimported, new id

            var mappings = await _context.KodiLibraryIds.ToListAsync();
            mappings.Should().HaveCount(1);
            mappings[0].KodiId.Should().Be(43);
        }

        // ── New-content scan signal ──────────────────────────────────────────
        // Deliberately exercised WITHOUT ever calling RegisterAsync/creating a KodiDevice --
        // that's the whole point: KodiDevice only exists once "Allow remote control via HTTP"
        // is on (see KodiDevice's own doc), and this feature must keep working for API token 1
        // (seeded in the constructor above) with no such registration. A real KodiDevice would
        // just be incidental noise in these tests.

        [Fact]
        public async Task IsScanNeededAsync_BeforeAnySignal_ReturnsFalse()
        {
            (await _service.IsScanNeededAsync(apiTokenId: 1)).Should().BeFalse();
        }

        [Fact]
        public async Task IsScanNeededAsync_WorksForAnApiTokenThatNeverRegisteredAKodiDevice()
        {
            // Regression test for the real bug this replaced: the first version of this
            // resolved the caller via KodiDevice (through GetKodiDeviceIdAsync), which only
            // exists once "Allow remote control via HTTP" is on -- so on a vanilla Kodi install
            // (that setting is off by default) the signal silently never fired at all. No
            // RegisterAsync call anywhere in this test -- confirms the fix doesn't depend on it.
            await _service.SignalNewContentAsync("movies");

            (await _service.IsScanNeededAsync(apiTokenId: 1)).Should().BeTrue();
        }

        [Fact]
        public async Task SignalNewContentAsync_ForNonVideoLibraryType_IsANoOp()
        {
            // Music/book/etc. imports have nothing for Kodi's video library to discover --
            // signalling for one of these must not make a caller think it's due for a scan.
            await _service.SignalNewContentAsync("music");

            (await _service.IsScanNeededAsync(apiTokenId: 1)).Should().BeFalse();
        }

        [Fact]
        public async Task AcknowledgeScanAsync_ClearsScanNeededUntilTheNextSignal()
        {
            await _service.SignalNewContentAsync("movies");

            await _service.AcknowledgeScanAsync(apiTokenId: 1);

            (await _service.IsScanNeededAsync(apiTokenId: 1)).Should().BeFalse();

            // A later signal must make it due again, not stay silenced forever by the earlier ack.
            await _service.SignalNewContentAsync("movies");
            (await _service.IsScanNeededAsync(apiTokenId: 1)).Should().BeTrue();
        }

        [Fact]
        public async Task SignalNewContentAsync_AffectsCallersSeenBeforeAndAfterTheSignal()
        {
            // One global timestamp, not per-caller state seeded on first contact -- a caller
            // that first polls AFTER the signal was raised must still see it as due, exactly
            // like one that had already polled (and acknowledged an earlier signal) before.
            await _service.AcknowledgeScanAsync(apiTokenId: 1); // "already polled once, nothing due yet"
            await _service.SignalNewContentAsync("tv");

            (await _service.IsScanNeededAsync(apiTokenId: 1)).Should().BeTrue();
            // apiTokenId 2 has never been seen at all before this signal -- still due.
            (await _service.IsScanNeededAsync(apiTokenId: 2)).Should().BeTrue();
        }

        [Fact]
        public async Task AcknowledgeScanAsync_CalledTwice_UpsertsInsteadOfDuplicating()
        {
            await _service.AcknowledgeScanAsync(apiTokenId: 1);
            await _service.AcknowledgeScanAsync(apiTokenId: 1);

            (await _context.KodiScanAcks.CountAsync(a => a.ApiTokenId == 1)).Should().Be(1);
        }

        // ── Scan-active heartbeat ────────────────────────────────────────────────
        // Root-caused live (2026-09-12): NfoGenerationService's own 2-minute scheduled sweep and
        // an active library scan's live, per-item NFO pushes had no coordination -- both
        // routinely rebuilt and wrote the same freshly-discovered item's NFO within seconds of
        // each other. This flag is what NfoGenerationService checks to pause its own sweep for
        // as long as some Kodi device keeps renewing it.

        [Fact]
        public async Task IsScanActiveAsync_BeforeAnyHeartbeat_ReturnsFalse()
        {
            (await _service.IsScanActiveAsync()).Should().BeFalse();
        }

        [Fact]
        public async Task ReportScanActivityAsync_ThenIsScanActiveAsync_ReturnsTrue()
        {
            await _service.ReportScanActivityAsync();

            (await _service.IsScanActiveAsync()).Should().BeTrue();
        }

        [Fact]
        public async Task IsScanActiveAsync_PastDeadline_ReturnsFalse()
        {
            // Simulates the flag's own TTL having lapsed with no further heartbeat -- rather
            // than waiting out the real TTL, write an already-past deadline directly (same
            // storage shape ReportScanActivityAsync itself writes: an ISO-8601 "until" instant).
            _context.AppSettings.Add(new AppSetting
            {
                Key = "kodi.scan_active_until",
                Value = DateTime.UtcNow.AddMinutes(-1).ToString("O"),
            });
            await _context.SaveChangesAsync();

            (await _service.IsScanActiveAsync()).Should().BeFalse();
        }

        [Fact]
        public async Task ReportScanActivityAsync_CalledTwice_RenewsRatherThanDuplicating()
        {
            await _service.ReportScanActivityAsync();
            await _service.ReportScanActivityAsync();

            (await _context.AppSettings.CountAsync(s => s.Key == "kodi.scan_active_until")).Should().Be(1);
        }

        // ── Per-item refresh-push signal ──────────────────────────────────────────
        // See IKodiDeviceService.GetItemsNeedingRefreshAsync's own doc. Deliberately never fakes
        // timestamps directly -- ChronicleDbContext's own SaveChanges hook stamps UpdatedAt on
        // every Modified MediaItem, so "device already caught up" vs. "due for a refresh" is
        // expressed by ORDERING real calls (RecordKodiIdAsync then a later item edit, or the
        // reverse), the same way it happens for real.

        [Fact]
        public async Task GetItemsNeedingRefreshAsync_WithNoRegisteredDevice_ReturnsEmpty()
        {
            var due = await _service.GetItemsNeedingRefreshAsync(apiTokenId: 999, ["movie"]);

            due.Should().BeEmpty();
        }

        [Fact]
        public async Task GetItemsNeedingRefreshAsync_ItemNeverScrapedByThisDevice_IsNotIncluded()
        {
            await _service.RegisterAsync(1, 1, "Shield", "10.0.0.10", 8080, null, null);
            // No RecordKodiIdAsync call -- this device has no kodi_library_ids row for item 100
            // at all, so there's nothing to compare its own UpdatedAt against.

            var due = await _service.GetItemsNeedingRefreshAsync(apiTokenId: 1, ["movie"]);

            due.Should().BeEmpty();
        }

        [Fact]
        public async Task GetItemsNeedingRefreshAsync_ItemUnchangedSinceLastScrape_IsNotIncluded()
        {
            await _service.RegisterAsync(1, 1, "Shield", "10.0.0.10", 8080, null, null);
            await _service.RecordKodiIdAsync(1, mediaItemId: 100, "movie", kodiId: 42);

            var due = await _service.GetItemsNeedingRefreshAsync(apiTokenId: 1, ["movie"]);

            due.Should().BeEmpty("the device's own kodi_library_ids row is already newer than the item's own last change");
        }

        [Fact]
        public async Task GetItemsNeedingRefreshAsync_ItemChangedSinceLastScrape_IsIncluded()
        {
            await _service.RegisterAsync(1, 1, "Shield", "10.0.0.10", 8080, null, null);
            await _service.RecordKodiIdAsync(1, mediaItemId: 100, "movie", kodiId: 42);

            // A real metadata change after the device last scraped it -- e.g. re-enrichment or
            // an admin edit. Any Modified save stamps a fresh UpdatedAt via the DbContext's own
            // hook, so this alone is enough to simulate "Chronicle's data changed since."
            var item = await _context.MediaItems.FindAsync(100);
            item!.Overview = "A newly re-enriched overview.";
            await _context.SaveChangesAsync();

            var due = await _service.GetItemsNeedingRefreshAsync(apiTokenId: 1, ["movie"]);

            due.Should().ContainSingle();
            due[0].KodiId.Should().Be(42);
            due[0].Kind.Should().Be("movie");
        }

        [Fact]
        public async Task GetItemsNeedingRefreshAsync_FiltersByRequestedKindsOnly()
        {
            _context.MediaItems.Add(new MediaItem
            {
                Id = 101, Name = "Test Episode", MediaTypeId = 1, HierarchyLevel = 2,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await _context.SaveChangesAsync();

            await _service.RegisterAsync(1, 1, "Shield", "10.0.0.10", 8080, null, null);
            await _service.RecordKodiIdAsync(1, mediaItemId: 100, "movie", kodiId: 42);
            await _service.RecordKodiIdAsync(1, mediaItemId: 101, "episode", kodiId: 77);

            var movie = await _context.MediaItems.FindAsync(100);
            movie!.Overview = "Changed";
            var episode = await _context.MediaItems.FindAsync(101);
            episode!.Overview = "Also changed";
            await _context.SaveChangesAsync();

            // The Movies addon only ever polls for "movie" -- it must never be handed an
            // episode's own kodi id to (wrongly) call VideoLibrary.RefreshMovie against.
            var due = await _service.GetItemsNeedingRefreshAsync(apiTokenId: 1, ["movie"]);

            due.Should().ContainSingle();
            due[0].Kind.Should().Be("movie");
        }

        [Fact]
        public async Task GetItemsNeedingRefreshAsync_MultipleRequestedKinds_ReturnsBoth()
        {
            _context.MediaItems.Add(new MediaItem
            {
                Id = 101, Name = "Test Episode", MediaTypeId = 1, HierarchyLevel = 2,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await _context.SaveChangesAsync();

            await _service.RegisterAsync(1, 1, "Shield", "10.0.0.10", 8080, null, null);
            await _service.RecordKodiIdAsync(1, mediaItemId: 100, "movie", kodiId: 42);
            await _service.RecordKodiIdAsync(1, mediaItemId: 101, "episode", kodiId: 77);

            var movie = await _context.MediaItems.FindAsync(100);
            movie!.Overview = "Changed";
            var episode = await _context.MediaItems.FindAsync(101);
            episode!.Overview = "Also changed";
            await _context.SaveChangesAsync();

            // The TV addon polls for BOTH its own kinds in one call.
            var due = await _service.GetItemsNeedingRefreshAsync(apiTokenId: 1, ["episode", "tvshow"]);

            due.Should().ContainSingle();
            due[0].Kind.Should().Be("episode");
        }
    }
}
