using Chronicle.Core.Models;
using Chronicle.Core.Models.Scan;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Services;
using Chronicle.Services.Database;
using Chronicle.Services.Notifications;
using Chronicle.Services.Plugins;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.InMemory.Infrastructure.Internal;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Chronicle.Tests.Unit.Services;

/// <summary>The things that should make the bell ring: a failed task, a scan that found something, a plugin update, a big database.</summary>
public class NotificationProducersTests : IDisposable
{
    private readonly ChronicleDbContext _db = new(new DbContextOptionsBuilder<ChronicleDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public NotificationProducersTests()
    {
        _db.Users.Add(new User { Id = 1, Username = "admin", IsAdmin = true, IsActive = true, PasswordHash = "x", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        _db.Users.Add(new User { Id = 2, Username = "bob", IsAdmin = false, IsActive = true, PasswordHash = "x", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private string StoreName()
    {
#pragma warning disable EF1001
        return _db.GetService<IDbContextOptions>().FindExtension<InMemoryOptionsExtension>()!.StoreName;
#pragma warning restore EF1001
    }

    private IServiceScopeFactory Scopes(Action<IServiceCollection>? more = null)
    {
        var store = StoreName();
        var services = new ServiceCollection();
        services.AddDbContext<ChronicleDbContext>(o => o.UseInMemoryDatabase(store));
        services.AddScoped<INotificationService, NotificationService>();
        more?.Invoke(services);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private List<Notification> Notices(string kind) =>
        _db.Notifications.AsNoTracking().Where(n => n.Kind == kind).OrderBy(n => n.Id).ToList();

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    private static Mock<IScheduledTask> FailingTask(string id, string display, string error)
    {
        var task = new Mock<IScheduledTask>();
        task.Setup(t => t.TaskId).Returns(id);
        task.Setup(t => t.DisplayName).Returns(display);
        task.Setup(t => t.Description).Returns("d");
        task.Setup(t => t.DefaultCron).Returns("0 2 * * *");
        task.Setup(t => t.ExecuteAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException(error));
        return task;
    }

    private async Task AddTaskRowAsync(string id, string display, string? pluginId = null)
    {
        _db.BackgroundTasks.Add(new BackgroundTask { TaskId = id, DisplayName = display, Description = "d", CronExpression = "0 2 * * *", IsEnabled = true, PluginId = pluginId });
        await _db.SaveChangesAsync();
    }

    // ══ a task failed ═════════════════════════════════════════════════════════

    [Fact]
    public async Task AFailedTask_TellsTheAdministrators_WithTheReason_AndALinkToTheTasksPage()
    {
        await AddTaskRowAsync("backup", "Database Backup");
        var svc = new TaskSchedulerService([FailingTask("backup", "Database Backup", "Disk is full.").Object], Scopes());

        await svc.TriggerNowAsync("backup");
        await WaitForAsync(() => Notices(NotificationKinds.TaskFailed).Count > 0);

        var n = Notices(NotificationKinds.TaskFailed).Single();
        n.UserId.Should().Be(1, "only the administrator is told");
        n.Title.Should().Be("Database Backup failed");
        n.Body.Should().Be("Disk is full.");
        n.Link.Should().Be("/settings/background-tasks");
    }

    private static IPluginTaskRunner PluginRunnerThrowing(Exception ex)
    {
        var runner = new Mock<IPluginTaskRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(ex);
        return runner.Object;
    }

    private static Mock<IScheduledTask> ThrowingTask(string id, string display, Exception ex)
    {
        var task = new Mock<IScheduledTask>();
        task.Setup(t => t.TaskId).Returns(id);
        task.Setup(t => t.DisplayName).Returns(display);
        task.Setup(t => t.Description).Returns("d");
        task.Setup(t => t.DefaultCron).Returns("0 2 * * *");
        task.Setup(t => t.ExecuteAsync(It.IsAny<CancellationToken>())).ThrowsAsync(ex);
        return task;
    }

    [Fact]
    public async Task AnInternalError_IsKeptOutOfTheBell_ButStillRecordedOnTheTask()
    {
        await AddTaskRowAsync("sync", "Delta Sync");
        var svc = new TaskSchedulerService([ThrowingTask("sync", "Delta Sync", new NullReferenceException("Object reference not set")).Object], Scopes());

        await svc.TriggerNowAsync("sync");
        await WaitForAsync(() => !svc.IsRunning("sync"));

        Notices(NotificationKinds.TaskFailed).Should().BeEmpty();
        (await _db.BackgroundTasks.AsNoTracking().SingleAsync(t => t.TaskId == "sync")).LastErrorMessage.Should().Contain("Object reference");
    }

    [Fact]
    public async Task APluginThatDoesNotFitThisChronicle_IsKeptOutOfTheBell_WhenThereIsNoUpdateToInstall()
    {
        await AddTaskRowAsync("hc:delta", "Delta Sync", pluginId: "hardcover");
        _db.Plugins.Add(new Plugin { PluginId = "hardcover", Name = "Hardcover", Version = "1.3.3", Author = "a", DllPath = "x", InstalledAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();
        var svc = new TaskSchedulerService([], Scopes(sv => sv.AddScoped(_ => PluginRunnerThrowing(new MissingMethodException("Method not found: 'Void X..ctor()'")))));

        await svc.TriggerNowAsync("hc:delta");
        await WaitForAsync(() => !svc.IsRunning("hc:delta"));

        Notices(NotificationKinds.TaskFailed).Should().BeEmpty();
    }

    [Fact]
    public async Task APluginThatDoesNotFitThisChronicle_SaysToUpdateIt_WhenAnUpdateExists()
    {
        await AddTaskRowAsync("hc:delta", "Delta Sync", pluginId: "hardcover");
        _db.Plugins.Add(new Plugin { PluginId = "hardcover", Name = "Hardcover", Version = "1.3.3", LatestVersionAvailable = "1.4.0", Author = "a", DllPath = "x", InstalledAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();
        var svc = new TaskSchedulerService([], Scopes(sv => sv.AddScoped(_ => PluginRunnerThrowing(new MissingMethodException("Method not found")))));

        await svc.TriggerNowAsync("hc:delta");
        await WaitForAsync(() => Notices(NotificationKinds.TaskFailed).Count > 0);

        var n = Notices(NotificationKinds.TaskFailed).Single();
        n.Title.Should().Be("Hardcover needs an update");
        n.Body.Should().Contain("v1.4.0").And.NotContain("Method not found");
        n.Link.Should().Be("/plugins");
    }

    [Fact]
    public async Task ANetworkOrAccountProblem_StillReachesTheBell()
    {
        await AddTaskRowAsync("sync", "Delta Sync");
        var svc = new TaskSchedulerService([ThrowingTask("sync", "Delta Sync", new HttpRequestException("401 Unauthorized")).Object], Scopes());

        await svc.TriggerNowAsync("sync");
        await WaitForAsync(() => Notices(NotificationKinds.TaskFailed).Count > 0);

        Notices(NotificationKinds.TaskFailed).Single().Body.Should().Be("401 Unauthorized");
    }

    [Fact]
    public async Task ATaskFailingAgainAndAgain_DoesNotPileUpUnreadNotices()
    {
        await AddTaskRowAsync("backup", "Database Backup");
        var svc = new TaskSchedulerService([FailingTask("backup", "Database Backup", "Disk is full.").Object], Scopes());

        for (var i = 0; i < 3; i++)
        {
            await svc.TriggerNowAsync("backup");
            await WaitForAsync(() => !svc.IsRunning("backup"));
        }

        Notices(NotificationKinds.TaskFailed).Should().HaveCount(1);
    }

    [Fact]
    public async Task ASuccessfulTask_SaysNothing()
    {
        await AddTaskRowAsync("ok", "Fine");
        var task = new Mock<IScheduledTask>();
        task.Setup(t => t.TaskId).Returns("ok");
        task.Setup(t => t.DisplayName).Returns("Fine");
        task.Setup(t => t.Description).Returns("d");
        task.Setup(t => t.DefaultCron).Returns("0 2 * * *");
        task.Setup(t => t.ExecuteAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var svc = new TaskSchedulerService([task.Object], Scopes());

        await svc.TriggerNowAsync("ok");
        await WaitForAsync(() => !svc.IsRunning("ok"));

        _db.Notifications.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedTask_StillRecordsTheFailure_WhenNotificationsAreNotAvailable()
    {
        await AddTaskRowAsync("backup", "Backup");
        var store = StoreName();
        var bare = new ServiceCollection().AddDbContext<ChronicleDbContext>(o => o.UseInMemoryDatabase(store))
            .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var svc = new TaskSchedulerService([FailingTask("backup", "Backup", "x").Object], bare);

        await svc.TriggerNowAsync("backup");
        await WaitForAsync(() => !svc.IsRunning("backup"));

        (await _db.BackgroundTasks.AsNoTracking().SingleAsync()).LastRunSucceeded.Should().BeFalse();
    }

    // ══ a plugin update ═══════════════════════════════════════════════════════

    [Fact]
    public async Task APluginUpdate_IsAnnouncedOncePerVersion()
    {
        var plugins = new List<Plugin> { new() { PluginId = "tmdb", Name = "TMDB", Version = "1.0.0", LatestVersionAvailable = "1.1.0" } };
        using var scope = Scopes().CreateScope();
        var notifier = scope.ServiceProvider.GetRequiredService<INotificationService>();

        await PluginUpdateCheckService.AnnounceUpdatesAsync(notifier, plugins, default);
        await PluginUpdateCheckService.AnnounceUpdatesAsync(notifier, plugins, default);   // the next nightly check

        Notices(NotificationKinds.PluginUpdate).Should().ContainSingle().Which.Title.Should().Be("TMDB 1.1.0 is available");

        plugins[0].LatestVersionAvailable = "1.2.0";
        await PluginUpdateCheckService.AnnounceUpdatesAsync(notifier, plugins, default);
        Notices(NotificationKinds.PluginUpdate).Should().HaveCount(2, "a newer version is new news");
    }

    [Fact]
    public async Task UpToDatePlugins_AreNotMentioned()
    {
        using var scope = Scopes().CreateScope();

        await PluginUpdateCheckService.AnnounceUpdatesAsync(scope.ServiceProvider.GetRequiredService<INotificationService>(),
            [new Plugin { PluginId = "x", Name = "X", Version = "1.0.0", LatestVersionAvailable = null }], default);

        _db.Notifications.Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutANotificationService_TheUpdateCheckStillWorks()
    {
        await PluginUpdateCheckService.AnnounceUpdatesAsync(null, [new Plugin { PluginId = "x", Name = "X", Version = "1", LatestVersionAvailable = "2" }], default);
    }

    // ══ the database is getting big ═══════════════════════════════════════════

    private static DatabaseStatus Status(bool over) =>
        new(true, "Sqlite", null, "d.db", 6_000_000_000, 0, 4096, 1, 0, 0, 1, "m", "b", 1, 10, null, 5_000_000_000, over, []);

    [Fact]
    public async Task ANightlyBackup_RaisesTheSizeWarning_OnlyWhenOverTheThreshold_AndNotEveryNight()
    {
        var admin = new Mock<IDatabaseAdminService>();
        admin.Setup(a => a.CreateBackupAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackupInfo("b.zip", "scheduled", 1, DateTime.UtcNow, null, null, null));
        var task = new DatabaseBackupTask(admin.Object, Scopes());

        admin.Setup(a => a.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Status(over: false));
        await task.ExecuteAsync(default);
        _db.Notifications.Should().BeEmpty();

        admin.Setup(a => a.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Status(over: true));
        await task.ExecuteAsync(default);
        await task.ExecuteAsync(default);

        var n = Notices(NotificationKinds.DatabaseSize).Should().ContainSingle().Subject;
        n.Title.Should().Contain("5.6 GB");
        n.Link.Should().Be("/settings/database");
    }

    [Fact]
    public async Task AnUnsupportedDatabase_SkipsTheBackupAndTheSizeCheck()
    {
        var admin = new Mock<IDatabaseAdminService>();
        admin.Setup(a => a.CreateBackupAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new DatabaseAdminException("UNSUPPORTED", "no"));

        await new DatabaseBackupTask(admin.Object, Scopes()).ExecuteAsync(default);

        admin.Verify(a => a.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ══ a scheduled scan found something ══════════════════════════════════════

    [Fact]
    public async Task AScheduledScan_ThatImportsItems_TellsTheAdministrators()
    {
        var folder = new ScanFolder
        {
            Id = 1, Path = "/media/movies", MediaTypeId = 1, IsEnabled = true,
            MediaType = new MediaType { Id = 1, Name = "movies", DisplayName = "Movies" },
        };
        _db.ScanFolders.Add(new ScanFolder { Id = 1, Path = "/media/movies", MediaTypeId = 1, IsEnabled = true });
        await _db.SaveChangesAsync();

        var fileScan = new Mock<IFileScanService>();
        fileScan.Setup(s => s.GetConfidenceThresholdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(70);
        fileScan.Setup(s => s.PreviewGroupedAsync(It.IsAny<ScanPreviewRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanGroupResult
            {
                Groups = [new ScanGroup { GroupKey = "a", Name = "A Film", ConfidenceScore = 0.95, Files = ["/media/movies/a.mkv"] }],
                Ungrouped = [], TotalFiles = 1,
            });
        fileScan.Setup(s => s.ImportGroupsAsync(It.IsAny<ImportGroupsRequest>(), It.IsAny<IReadOnlyList<int>>(),
                It.IsAny<CancellationToken>(), It.IsAny<int>(), It.IsAny<bool>()))
            .ReturnsAsync(new ImportApprovedSummary(3, 1, ["x"]));
        var folders = new Mock<IScanFolderService>();
        folders.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([folder]);
        var registry = new Mock<IPluginRegistry>();
        registry.Setup(r => r.GetFileScannerPlugins()).Returns([]);

        var scopes = Scopes(s =>
        {
            s.AddSingleton(fileScan.Object);
            s.AddSingleton(folders.Object);
            s.AddSingleton(Mock.Of<IKodiDeviceService>());
            s.AddSingleton(Mock.Of<IMetadataEnrichmentService>());
        });
        var service = new ScheduledScanService(scopes, new ImportProgressService(), registry.Object);

        await service.ExecuteAsync(default);

        var n = Notices(NotificationKinds.ScanImported).Should().ContainSingle().Subject;
        n.UserId.Should().Be(1);
        n.Title.Should().Be("3 new items found in /media/movies");
        n.Body.Should().Be("1 could not be imported.");
        n.Link.Should().Be("/library");
    }
}
