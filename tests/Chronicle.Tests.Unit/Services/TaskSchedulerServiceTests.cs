using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.InMemory.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
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

    /// <summary>
    /// Was `services.AddSingleton(db)` -- every scope this factory hands out got the literal
    /// SAME ChronicleDbContext instance, not an independent one the way real ASP.NET Core
    /// scoping works. TaskSchedulerService fires background work via un-awaited `Task.Run`
    /// (TickAsync/TriggerNowAsync), so its own `SaveChangesAsync` and a background
    /// RunTaskAsync's `PersistRunResultAsync` `SaveChangesAsync` could both land on that one
    /// shared instance from two different threads at once -- EF Core DbContexts aren't
    /// thread-safe, and this threw "a second operation was started on this context instance"
    /// intermittently (confirmed flaky in CI, unrelated to whatever else was being tested at
    /// the time). Deriving the shared EF Core InMemory store name from `db` and registering a
    /// real scoped DbContext means every scope gets its own instance pointed at the same
    /// underlying store -- same data visible to `db` and to every scope, but no two threads
    /// ever touch one instance concurrently, matching how the real DI container behaves.
    /// </summary>
    private static IServiceScopeFactory MakeScopeFactory(ChronicleDbContext db)
    {
        // EF1001: InMemoryOptionsExtension is an internal API, but reading back the store name
        // an existing context was built with is a standard, widely-used pattern for exactly
        // this "share one in-memory store across independent DbContext instances" scenario --
        // there's no public API for it. Test-only code; accepted tradeoff.
#pragma warning disable EF1001
        var storeName = db.GetService<IDbContextOptions>()
            .FindExtension<InMemoryOptionsExtension>()!.StoreName;
#pragma warning restore EF1001
        var services = new ServiceCollection();
        services.AddDbContext<ChronicleDbContext>(opts => opts.UseInMemoryDatabase(storeName));
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

    /// <summary>
    /// Replaces a fixed `await Task.Delay(N)` guess for "the fire-and-forget background work
    /// TickAsync/TriggerNowAsync dispatches via un-awaited Task.Run must be done by now" -- a
    /// fixed delay is either too short (flaky under load) or wastefully long, and one of these
    /// (RunTask_ExceptionPersistsErrorAndDoesNotThrow) started failing outright, not just
    /// flaking, once MakeScopeFactory stopped handing out one shared DbContext instance (see
    /// that method's own doc comment) -- the real background write now takes a hair longer than
    /// 200ms did, wrong on the same instance regardless. Polls `condition` instead of guessing.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Condition not met within {timeoutMs}ms.");
            await Task.Delay(10);
        }
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
        await WaitForAsync(() => !svc.IsRunning("test_task"));

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
        await WaitForAsync(() => !svc.IsRunning("test_task"));

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

        // TickAsync's per-row _running.TryAdd runs synchronously before it ever dispatches the
        // background Task.Run, so IsRunning is already true (and stays true -- the task is
        // deliberately blocked on `tcs`, unresolved until tcs.SetResult() below) the moment
        // this first call returns -- no wait needed before the second Tick.
        await svc.TickAsync(CancellationToken.None);

        db.BackgroundTasks.First().NextRunAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        // The second TickAsync's "already running, skip" decision happens synchronously inside
        // the awaited call itself (the _running.TryAdd check), not in the background dispatch --
        // no further wait is needed once it returns.
        await svc.TickAsync(CancellationToken.None);

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

        // TriggerNowAsync's _running.TryAdd runs synchronously before it ever dispatches the
        // background Task.Run, so IsRunning is already true the moment this first call
        // returns -- no wait needed before checking that a second trigger sees it as running.
        await svc.TriggerNowAsync("test_task");

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
        await WaitForAsync(() => !svc.IsRunning("chronicle.plugin.musicbrainz:fetch-missing-metadata"));

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

        await WaitForAsync(() => !svc.IsRunning("chronicle.plugin.tmdb:resync-all-metadata"));

        runner.Verify(r => r.RunAsync(
            "chronicle.plugin.tmdb",
            "resync-all-metadata",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Fetch-missing-metadata: keep retrying until the queue is actually drained ───────────
    // Per-user request (2026-09-15): "I would really prefer that it keep trying periodically
    // until the queues are emptied" -- see ComputeNextRunAtAsync's own doc for the full
    // reasoning (EnrichPendingAsync's while(true) loop only ever stops early because the
    // provider became unavailable, so "still Pending right after a completed run" reliably
    // means "got blocked", not just "big queue, more to do eventually").

    [Fact]
    public async Task PersistRunResult_FetchMissingMetadataStillPending_SchedulesSoonRetryNotFullCron()
    {
        var db = MakeDb();
        db.BackgroundTasks.Add(new BackgroundTask
        {
            TaskId         = "chronicle.plugin.tmdb:fetch-missing-metadata",
            PluginId       = "chronicle.plugin.tmdb",
            DisplayName    = "Fetch Missing Metadata",
            Description    = "Looks up metadata for new items.",
            CronExpression = "0 4 * * *", // once daily -- the actual live TMDB schedule
            IsEnabled      = true,
            NextRunAt      = DateTime.UtcNow.AddMinutes(-1),
        });
        db.MediaEnrichments.Add(new MediaItemEnrichment
        {
            MediaItemId = 1, PluginId = "chronicle.plugin.tmdb", Status = EnrichmentStatus.Pending,
        });
        await db.SaveChangesAsync();

        var runner = new Mock<IPluginTaskRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask); // completes without throwing -- the graceful "provider unavailable" outcome

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<IPluginTaskRunner>(runner.Object);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var svc = new TaskSchedulerService(Array.Empty<IScheduledTask>(), scopeFactory);

        await svc.TickAsync(CancellationToken.None);
        await WaitForAsync(() => !svc.IsRunning("chronicle.plugin.tmdb:fetch-missing-metadata"));

        var row = await db.BackgroundTasks.AsNoTracking()
            .FirstAsync(t => t.TaskId == "chronicle.plugin.tmdb:fetch-missing-metadata");
        row.NextRunAt.Should().NotBeNull();
        row.NextRunAt!.Value.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(15), TimeSpan.FromSeconds(30),
            "still-Pending work should retry soon, not wait for tomorrow's 4am cron");
    }

    [Fact]
    public async Task PersistRunResult_FetchMissingMetadataFullyDrained_UsesNormalCron()
    {
        var db = MakeDb();
        db.BackgroundTasks.Add(new BackgroundTask
        {
            TaskId         = "chronicle.plugin.tmdb:fetch-missing-metadata",
            PluginId       = "chronicle.plugin.tmdb",
            DisplayName    = "Fetch Missing Metadata",
            Description    = "Looks up metadata for new items.",
            CronExpression = "0 4 * * *",
            IsEnabled      = true,
            NextRunAt      = DateTime.UtcNow.AddMinutes(-1),
        });
        // No Pending rows for this plugin -- the queue is genuinely empty.
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
        await WaitForAsync(() => !svc.IsRunning("chronicle.plugin.tmdb:fetch-missing-metadata"));

        var row = await db.BackgroundTasks.AsNoTracking()
            .FirstAsync(t => t.TaskId == "chronicle.plugin.tmdb:fetch-missing-metadata");
        row.NextRunAt.Should().NotBeNull();
        // Falls back to the real cron occurrence -- comfortably more than 15 minutes away for
        // an "0 4 * * *" schedule regardless of what time the test happens to run.
        row.NextRunAt!.Value.Should().BeAfter(DateTime.UtcNow.AddHours(1),
            "a fully-drained queue must fall back to the task's own normal cron, not keep retrying soon");
    }

    [Fact]
    public async Task PersistRunResult_FetchMissingMetadataStillPending_NeverSchedulesLaterThanNormalCron()
    {
        var db = MakeDb();
        db.BackgroundTasks.Add(new BackgroundTask
        {
            TaskId         = "chronicle.plugin.musicbrainz:fetch-missing-metadata",
            PluginId       = "chronicle.plugin.musicbrainz",
            DisplayName    = "Fetch Missing Metadata",
            Description    = "Looks up metadata for new items.",
            CronExpression = "* * * * *", // every minute -- already far more frequent than the 15-minute retry floor
            IsEnabled      = true,
            NextRunAt      = DateTime.UtcNow.AddMinutes(-1),
        });
        db.MediaEnrichments.Add(new MediaItemEnrichment
        {
            MediaItemId = 1, PluginId = "chronicle.plugin.musicbrainz", Status = EnrichmentStatus.Pending,
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
        await WaitForAsync(() => !svc.IsRunning("chronicle.plugin.musicbrainz:fetch-missing-metadata"));

        var row = await db.BackgroundTasks.AsNoTracking()
            .FirstAsync(t => t.TaskId == "chronicle.plugin.musicbrainz:fetch-missing-metadata");
        row.NextRunAt.Should().NotBeNull();
        row.NextRunAt!.Value.Should().BeBefore(DateTime.UtcNow.AddMinutes(2),
            "a plugin whose own cron is already more frequent than the retry floor must keep its faster cadence");
    }

    [Fact]
    public async Task PersistRunResult_NonFetchMissingPluginTask_IgnoresPendingRows()
    {
        var db = MakeDb();
        db.BackgroundTasks.Add(new BackgroundTask
        {
            TaskId         = "chronicle.plugin.tmdb:resync-all-metadata",
            PluginId       = "chronicle.plugin.tmdb",
            DisplayName    = "Re-sync All Metadata",
            Description    = "Re-downloads metadata.",
            CronExpression = "0 3 * * 0",
            IsEnabled      = true,
            NextRunAt      = DateTime.UtcNow.AddMinutes(-1),
        });
        db.MediaEnrichments.Add(new MediaItemEnrichment
        {
            MediaItemId = 1, PluginId = "chronicle.plugin.tmdb", Status = EnrichmentStatus.Pending,
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
        await WaitForAsync(() => !svc.IsRunning("chronicle.plugin.tmdb:resync-all-metadata"));

        var row = await db.BackgroundTasks.AsNoTracking()
            .FirstAsync(t => t.TaskId == "chronicle.plugin.tmdb:resync-all-metadata");
        row.NextRunAt.Should().NotBeNull();
        row.NextRunAt!.Value.Should().BeAfter(DateTime.UtcNow.AddHours(1),
            "the soon-retry behavior is scoped to fetch-missing-metadata only, not every plugin task");
    }

    [Fact]
    public async Task PersistRunResult_FetchMissingMetadataThrows_UsesNormalCronNotSoonRetry()
    {
        var db = MakeDb();
        db.BackgroundTasks.Add(new BackgroundTask
        {
            TaskId         = "chronicle.plugin.tmdb:fetch-missing-metadata",
            PluginId       = "chronicle.plugin.tmdb",
            DisplayName    = "Fetch Missing Metadata",
            Description    = "Looks up metadata for new items.",
            CronExpression = "0 4 * * *",
            IsEnabled      = true,
            NextRunAt      = DateTime.UtcNow.AddMinutes(-1),
        });
        db.MediaEnrichments.Add(new MediaItemEnrichment
        {
            MediaItemId = 1, PluginId = "chronicle.plugin.tmdb", Status = EnrichmentStatus.Pending,
        });
        await db.SaveChangesAsync();

        var runner = new Mock<IPluginTaskRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("plugin blew up"));

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<IPluginTaskRunner>(runner.Object);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var svc = new TaskSchedulerService(Array.Empty<IScheduledTask>(), scopeFactory);

        await svc.TickAsync(CancellationToken.None);
        await WaitForAsync(() => !svc.IsRunning("chronicle.plugin.tmdb:fetch-missing-metadata"));

        var row = await db.BackgroundTasks.AsNoTracking()
            .FirstAsync(t => t.TaskId == "chronicle.plugin.tmdb:fetch-missing-metadata");
        row.LastRunSucceeded.Should().BeFalse();
        row.NextRunAt.Should().NotBeNull();
        row.NextRunAt!.Value.Should().BeAfter(DateTime.UtcNow.AddHours(1),
            "a genuine unhandled crash gets the normal cadence, not a rapid retry loop against whatever just broke it");
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
        await WaitForAsync(() => !svc.IsRunning("failing_task"));

        // FindAsync would return db's own already-tracked local copy of this row (added via
        // `db` a few lines up) without re-querying the store -- it can never see the background
        // task's write, which landed through a DIFFERENT DbContext instance sharing the same
        // in-memory store. AsNoTracking forces a genuine read from the shared store instead.
        var row = await db.BackgroundTasks.AsNoTracking().FirstOrDefaultAsync(t => t.TaskId == "failing_task");
        row!.LastRunSucceeded.Should().BeFalse();
        row.LastErrorMessage.Should().Be("Something broke.");
        svc.IsRunning("failing_task").Should().BeFalse();
    }
}
