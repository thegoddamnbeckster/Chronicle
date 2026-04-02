using Chronicle.Core.Models;
using Chronicle.Core.Models.Scan;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Chronicle.Tests.Unit.Services;

public class ScheduledScanServiceTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ChronicleDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ChronicleDbContext(opts);
    }

    /// <summary>
    /// Builds an IServiceScopeFactory backed by a real ServiceCollection so that
    /// CreateScope() works correctly — mirrors the pattern used in MetadataRefreshServiceTests.
    /// </summary>
    private static IServiceScopeFactory MakeScopeFactory(
        ChronicleDbContext db,
        IFileScanService fileScanSvc,
        IScanFolderService scanFolderSvc)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(fileScanSvc);
        services.AddSingleton(scanFolderSvc);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_WithNoEnabledFolders_SkipsImport()
    {
        // Arrange
        var db = MakeDb();

        var mockFileScanSvc = new Mock<IFileScanService>();
        var mockScanFolderSvc = new Mock<IScanFolderService>();

        // Return an empty folder list
        mockScanFolderSvc
            .Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ScanFolder>());

        var scopeFactory = MakeScopeFactory(db, mockFileScanSvc.Object, mockScanFolderSvc.Object);
        var service = new ScheduledScanService(scopeFactory, new ImportProgressService());

        // Act
        await service.ExecuteAsync(CancellationToken.None);

        // Assert — PreviewGroupedAsync must never be called when there are no folders
        mockFileScanSvc.Verify(
            s => s.PreviewGroupedAsync(It.IsAny<ScanPreviewRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WithDisabledFoldersOnly_SkipsImport()
    {
        // Arrange
        var db = MakeDb();

        var mockFileScanSvc = new Mock<IFileScanService>();
        var mockScanFolderSvc = new Mock<IScanFolderService>();

        // Return only a disabled folder
        mockScanFolderSvc
            .Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ScanFolder>
            {
                new() { Id = 1, Path = "/tmp/movies", MediaTypeId = 1, IsEnabled = false }
            });

        var scopeFactory = MakeScopeFactory(db, mockFileScanSvc.Object, mockScanFolderSvc.Object);
        var service = new ScheduledScanService(scopeFactory, new ImportProgressService());

        // Act
        await service.ExecuteAsync(CancellationToken.None);

        // Assert
        mockFileScanSvc.Verify(
            s => s.PreviewGroupedAsync(It.IsAny<ScanPreviewRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WithEnabledFolderAndNoUsers_StillRunsScan()
    {
        // The scheduled scan no longer requires an admin user — it always proceeds.
        // UserLibrary rows are created lazily by GetForUserAsync; the scan just creates MediaItems.
        var db = MakeDb();

        var mockFileScanSvc = new Mock<IFileScanService>();
        var mockScanFolderSvc = new Mock<IScanFolderService>();

        mockScanFolderSvc
            .Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ScanFolder>
            {
                new() { Id = 1, Path = "/tmp/movies", MediaTypeId = 1, IsEnabled = true }
            });

        mockFileScanSvc
            .Setup(s => s.GetConfidenceThresholdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(80);

        mockFileScanSvc
            .Setup(s => s.PreviewGroupedAsync(It.IsAny<ScanPreviewRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanGroupResult { Groups = [], Ungrouped = [], TotalFiles = 0 });

        var scopeFactory = MakeScopeFactory(db, mockFileScanSvc.Object, mockScanFolderSvc.Object);
        var service = new ScheduledScanService(scopeFactory, new ImportProgressService());

        // Act
        await service.ExecuteAsync(CancellationToken.None);

        // Assert — scan proceeds even with no users
        mockFileScanSvc.Verify(
            s => s.PreviewGroupedAsync(It.IsAny<ScanPreviewRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WithEnabledFolderAndAdminUser_CallsPreviewGrouped()
    {
        // Arrange
        var db = MakeDb();

        // Seed an admin user
        var adminUser = new User
        {
            Username    = "admin",
            Email       = "admin@example.com",
            PasswordHash = "hash",
            IsAdmin     = true,
            CreatedAt   = DateTime.UtcNow,
            UpdatedAt   = DateTime.UtcNow
        };
        db.Users.Add(adminUser);
        await db.SaveChangesAsync();

        var mockFileScanSvc = new Mock<IFileScanService>();
        var mockScanFolderSvc = new Mock<IScanFolderService>();

        mockScanFolderSvc
            .Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ScanFolder>
            {
                new() { Id = 1, Path = "/tmp/movies", MediaTypeId = 1, IsEnabled = true }
            });

        mockFileScanSvc
            .Setup(s => s.GetConfidenceThresholdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(80);

        // Return empty scan result — no groups to import
        mockFileScanSvc
            .Setup(s => s.PreviewGroupedAsync(It.IsAny<ScanPreviewRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanGroupResult { Groups = [], Ungrouped = [], TotalFiles = 0 });

        var scopeFactory = MakeScopeFactory(db, mockFileScanSvc.Object, mockScanFolderSvc.Object);
        var service = new ScheduledScanService(scopeFactory, new ImportProgressService());

        // Act
        await service.ExecuteAsync(CancellationToken.None);

        // Assert — preview was called once for the single enabled folder
        mockFileScanSvc.Verify(
            s => s.PreviewGroupedAsync(
                It.Is<ScanPreviewRequest>(r => r.Path == "/tmp/movies" && r.MediaTypeId == 1),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Import must NOT be called when there are no qualifying groups
        mockFileScanSvc.Verify(
            s => s.ImportGroupsAsync(
                It.IsAny<ImportGroupsRequest>(),
                It.IsAny<IReadOnlyList<int>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<int>(),
                It.IsAny<bool>()),
            Times.Never);
    }
}
