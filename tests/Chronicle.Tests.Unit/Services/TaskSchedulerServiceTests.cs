using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Chronicle.Tests.Unit.Services;

public class TaskSchedulerServiceTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ChronicleDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ChronicleDbContext(opts);
    }

    private static IServiceScopeFactory MakeScopeFactory(ChronicleDbContext db)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static Mock<IScheduledTask> MakeTask(
        string id = "test_task",
        string cron = "0 */4 * * *")
    {
        var mock = new Mock<IScheduledTask>();
        mock.Setup(t => t.TaskId).Returns(id);
        mock.Setup(t => t.DisplayName).Returns("Test Task");
        mock.Setup(t => t.Description).Returns("A test task.");
        mock.Setup(t => t.DefaultCron).Returns(cron);
        mock.Setup(t => t.ExecuteAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return mock;
    }

    // ── Seeding ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task SeedTasks_InsertsRowForNewTask()
    {
        var db = MakeDb();
        var scopeFactory = MakeScopeFactory(db);
        var task = MakeTask("metadata_refresh", "0 */4 * * *");
        var svc = new TaskSchedulerService(new[] { task.Object }, scopeFactory);

        await svc.SeedTasksAsync(CancellationToken.None);

        var row = await db.BackgroundTasks.FindAsync("metadata_refresh");
        row.Should().NotBeNull();
        row!.CronExpression.Should().Be("0 */4 * * *");
        row.IsEnabled.Should().BeTrue();
        row.NextRunAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SeedTasks_DoesNotOverwriteExistingRow()
    {
        var db = MakeDb();
        db.BackgroundTasks.Add(new BackgroundTask
        {
            TaskId = "metadata_refresh",
            DisplayName = "Metadata Refresh",
            Description = "desc",
            CronExpression = "0 2 * * *",
            IsEnabled = true,
            NextRunAt = DateTime.UtcNow.AddHours(2)
        });
        await db.SaveChangesAsync();

        var scopeFactory = MakeScopeFactory(db);
        var task = MakeTask("metadata_refresh", "0 */4 * * *");
        var svc = new TaskSchedulerService(new[] { task.Object }, scopeFactory);

        await svc.SeedTasksAsync(CancellationToken.None);

        var row = await db.BackgroundTasks.FindAsync("metadata_refresh");
        row!.CronExpression.Should().Be("0 2 * * *");
    }

    // ── Tick / run-due ────────────────────────────────────────────────────────

    [Fact]
    public async Task TickAsync_FiresDueTask()
    {
        var db = MakeDb();
        db.BackgroundTasks.Add(new BackgroundTask
        {
            TaskId = "test_task",
            DisplayName = "T", Description = "d",
            CronExpression = "* * * * *",
            IsEnabled = true,
            NextRunAt = DateTime.UtcNow.AddMinutes(-1)
        });
        await db.SaveChangesAsync();

        var scopeFactory = MakeScopeFactory(db);
        var task = MakeTask("test_task");
        var svc = new TaskSchedulerService(new[] { task.Object }, scopeFactory);

        await svc.TickAsync(CancellationToken.None);
        await Task.Delay(200);

        task.Verify(t => t.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TickAsync_SkipsDisabledTask()
    {
        var db = MakeDb();
        db.BackgroundTasks.Add(new BackgroundTask
        {
            TaskId = "test_task",
            DisplayName = "T", Description = "d",
            CronExpression = "* * * * *",
            IsEnabled = false,
            NextRunAt = DateTime.UtcNow.AddMinutes(-1)
        });
        await db.SaveChangesAsync();

        var scopeFactory = MakeScopeFactory(db);
        var task = MakeTask("test_task");
        var svc = new TaskSchedulerService(new[] { task.Object }, scopeFactory);

        await svc.TickAsync(CancellationToken.None);
        await Task.Delay(200);

        task.Verify(t => t.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TickAsync_SkipsAlreadyRunningTask()
    {
        var db = MakeDb();
        db.BackgroundTasks.Add(new BackgroundTask
        {
            TaskId = "test_task",
            DisplayName = "T", Description = "d",
            CronExpression = "* * * * *",
            IsEnabled = true,
            NextRunAt = DateTime.UtcNow.AddMinutes(-1)
        });
        await db.SaveChangesAsync();

        var tcs = new TaskCompletionSource();
        var task = new Mock<IScheduledTask>();
        task.Setup(t => t.TaskId).Returns("test_task");
        task.Setup(t => t.DisplayName).Returns("T");
        task.Setup(t => t.Description).Returns("d");
        task.Setup(t => t.DefaultCron).Returns("* * * * *");
        task.Setup(t => t.ExecuteAsync(It.IsAny<CancellationToken>()))
            .Returns(tcs.Task);

        var scopeFactory = MakeScopeFactory(db);
        var svc = new TaskSchedulerService(new[] { task.Object }, scopeFactory);

        await svc.TickAsync(CancellationToken.None);
        await Task.Delay(50);

        db.BackgroundTasks.First().NextRunAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        await svc.TickAsync(CancellationToken.None);
        await Task.Delay(100);

        task.Verify(t => t.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Once);
        tcs.SetResult();
    }

    // ── TriggerNow ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TriggerNow_UnknownTaskId_ReturnsNotFound()
    {
        var db = MakeDb();
        var svc = new TaskSchedulerService(
            Array.Empty<IScheduledTask>(),
            MakeScopeFactory(db));

        var result = await svc.TriggerNowAsync("no_such_task");
        result.Should().Be(TriggerResult.NotFound);
    }

    [Fact]
    public async Task TriggerNow_IdleTask_ReturnsStarted()
    {
        var db = MakeDb();
        db.BackgroundTasks.Add(new BackgroundTask
        {
            TaskId = "test_task", DisplayName = "T", Description = "d",
            CronExpression = "0 */4 * * *", IsEnabled = true
        });
        await db.SaveChangesAsync();

        var task = MakeTask("test_task");
        var svc = new TaskSchedulerService(new[] { task.Object }, MakeScopeFactory(db));

        var result = await svc.TriggerNowAsync("test_task");
        result.Should().Be(TriggerResult.Started);
    }

    [Fact]
    public async Task TriggerNow_WhileRunning_ReturnsAlreadyRunning()
    {
        var db = MakeDb();
        db.BackgroundTasks.Add(new BackgroundTask
        {
            TaskId = "test_task", DisplayName = "T", Description = "d",
            CronExpression = "0 */4 * * *", IsEnabled = true
        });
        await db.SaveChangesAsync();

        var tcs = new TaskCompletionSource();
        var task = new Mock<IScheduledTask>();
        task.Setup(t => t.TaskId).Returns("test_task");
        task.Setup(t => t.DisplayName).Returns("T");
        task.Setup(t => t.Description).Returns("d");
        task.Setup(t => t.DefaultCron).Returns("0 */4 * * *");
        task.Setup(t => t.ExecuteAsync(It.IsAny<CancellationToken>())).Returns(tcs.Task);

        var svc = new TaskSchedulerService(new[] { task.Object }, MakeScopeFactory(db));

        await svc.TriggerNowAsync("test_task");
        await Task.Delay(50);

        var second = await svc.TriggerNowAsync("test_task");
        second.Should().Be(TriggerResult.AlreadyRunning);
        tcs.SetResult();
    }

    // ── Plugin task routing ───────────────────────────────────────────────────

    [Fact]
    public async Task TickAsync_PluginTask_CallsRunner()
    {
        var db = MakeDb();
        db.BackgroundTasks.Add(new BackgroundTask
        {
            TaskId         = "chronicle.plugin.musicbrainz:fetch-missing-metadata",
            PluginId       = "chronicle.plugin.musicbrainz",
            DisplayName    = "Fetch Missing Metadata",
            Description    = "Looks up metadata for new items.",
            CronExpression = "0 4 * * *",
            IsEnabled      = true,
            NextRunAt      = DateTime.UtcNow.AddMinutes(-1),
        });
        await db.SaveChangesAsync();

        var runner = new Mock<IPluginTaskRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<IPluginTaskRunner>(runner.Object);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var svc = new TaskSchedulerService(Array.Empty<IScheduledTask>(), scopeFactory);

        await svc.TickAsync(CancellationToken.None);
        await Task.Delay(200);

        runner.Verify(r => r.RunAsync(
            "chronicle.plugin.musicbrainz",
            "fetch-missing-metadata",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TriggerNow_PluginTask_CallsRunner()
    {
        var db = MakeDb();
        db.BackgroundTasks.Add(new BackgroundTask
        {
            TaskId         = "chronicle.plugin.tmdb:resync-all-metadata",
            PluginId       = "chronicle.plugin.tmdb",
            DisplayName    = "Re-sync All Metadata",
            Description    = "Re-downloads metadata.",
            CronExpression = "0 3 * * *",
            IsEnabled      = true,
        });
        await db.SaveChangesAsync();

        var runner = new Mock<IPluginTaskRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<IPluginTaskRunner>(runner.Object);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var svc = new TaskSchedulerService(Array.Empty<IScheduledTask>(), scopeFactory);

        var result = await svc.TriggerNowAsync("chronicle.plugin.tmdb:resync-all-metadata");
        result.Should().Be(TriggerResult.Started);

        await Task.Delay(200);

        runner.Verify(r => r.RunAsync(
            "chronicle.plugin.tmdb",
            "resync-all-metadata",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Error isolation ────────────────────────────────────────────────────────

    [Fact]
    public async Task RunTask_ExceptionPersistsErrorAndDoesNotThrow()
    {
        var db = MakeDb();
        db.BackgroundTasks.Add(new BackgroundTask
        {
            TaskId = "failing_task", DisplayName = "T", Description = "d",
            CronExpression = "0 */4 * * *", IsEnabled = true
        });
        await db.SaveChangesAsync();

        var task = new Mock<IScheduledTask>();
        task.Setup(t => t.TaskId).Returns("failing_task");
        task.Setup(t => t.DisplayName).Returns("T");
        task.Setup(t => t.Description).Returns("d");
        task.Setup(t => t.DefaultCron).Returns("0 */4 * * *");
        task.Setup(t => t.ExecuteAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Something broke."));

        var svc = new TaskSchedulerService(new[] { task.Object }, MakeScopeFactory(db));

        await svc.TriggerNowAsync("failing_task");
        await Task.Delay(200);

        var row = await db.BackgroundTasks.FindAsync("failing_task");
        row!.LastRunSucceeded.Should().BeFalse();
        row.LastErrorMessage.Should().Be("Something broke.");
        svc.IsRunning("failing_task").Should().BeFalse();
    }
}
