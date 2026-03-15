# Background Tasks Page Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Build a full-stack Background Tasks settings page that lets users view, schedule, and manually trigger Chronicle's background jobs.

**Architecture:** A central `TaskSchedulerService` (BackgroundService) drives all registered `IScheduledTask` implementations — replacing each task's own internal loop. Tasks self-register via DI; the scheduler seeds a `background_tasks` DB table on startup, ticks every 30 seconds, and fires due tasks. The React page polls live state and provides a visual schedule builder backed by Cronos cron expressions.

**Tech Stack:** C# / ASP.NET Core 9 / EF Core 9 / Cronos (cron parsing) / React 18 + TypeScript / axios / CSS Modules

---

## Prerequisites

Before starting: confirm you are in the `epic-perlman` worktree at `W:\Scripts\Chronicle\.claude\worktrees\epic-perlman`.

```bash
git branch   # should show claude/epic-perlman
```

Read the design doc before starting: `docs/plans/2026-03-15-background-tasks-design.md`

---

## Task 1: Add Cronos NuGet Package

**Files:**
- Modify: `src/Chronicle.Services/Chronicle.Services.csproj`

**Step 1: Add the package**

```bash
cd src/Chronicle.Services
dotnet add package Cronos
```

**Step 2: Verify it builds**

```bash
cd src/Chronicle.Services && dotnet build
```

Expected: Build succeeded, 0 errors.

**Step 3: Commit**

```bash
git add src/Chronicle.Services/Chronicle.Services.csproj
git commit -m "chore(deps): add Cronos cron parsing library to Chronicle.Services"
```

---

## Task 2: Create `BackgroundTask` Core Model

**Files:**
- Create: `src/Chronicle.Core/Models/BackgroundTask.cs`

**Step 1: Create the model**

```csharp
namespace Chronicle.Core.Models;

public class BackgroundTask
{
    public string TaskId           { get; set; } = string.Empty;
    public string DisplayName      { get; set; } = string.Empty;
    public string Description      { get; set; } = string.Empty;
    public string CronExpression   { get; set; } = string.Empty;
    public bool   IsEnabled        { get; set; } = true;
    public DateTime? LastRunAt     { get; set; }
    public bool?  LastRunSucceeded { get; set; }
    public string? LastErrorMessage{ get; set; }
    public DateTime? NextRunAt     { get; set; }
}
```

**Step 2: Build to verify**

```bash
cd src/Chronicle.Core && dotnet build
```

Expected: Build succeeded, 0 errors.

**Step 3: Commit**

```bash
git add src/Chronicle.Core/Models/BackgroundTask.cs
git commit -m "feat(core): add BackgroundTask domain model"
```

---

## Task 3: Register Model in DbContext and Create Migration

**Files:**
- Modify: `src/Chronicle.Data/ChronicleDbContext.cs`
- Create: migration file (auto-generated)

**Step 1: Add DbSet and model config to `ChronicleDbContext.cs`**

Add to the DbSet block (after line 25, `public DbSet<MediaItemRefreshLog> ...`):

```csharp
public DbSet<BackgroundTask> BackgroundTasks => Set<BackgroundTask>();
```

Add to `OnModelCreating` (before the closing brace of the method):

```csharp
modelBuilder.Entity<BackgroundTask>(e =>
{
    e.ToTable("background_tasks");
    e.HasKey(t => t.TaskId);
    e.Property(t => t.TaskId).HasMaxLength(100);
    e.Property(t => t.DisplayName).IsRequired();
    e.Property(t => t.Description).IsRequired();
    e.Property(t => t.CronExpression).IsRequired();
});
```

**Step 2: Create the migration**

```bash
cd src/Chronicle.API
dotnet ef migrations add AddBackgroundTasksTable --project ../Chronicle.Data
```

Expected: A new migration file appears in `src/Chronicle.Data/Migrations/`.

**Step 3: Verify migration applies**

```bash
dotnet ef database update --project ../Chronicle.Data
```

Expected: Done. No errors.

**Step 4: Build everything**

```bash
cd src/Chronicle.API && dotnet build
```

Expected: Build succeeded.

**Step 5: Commit**

```bash
git add src/Chronicle.Data/ src/Chronicle.API/
git commit -m "feat(data): add background_tasks table migration"
```

---

## Task 4: Define `IScheduledTask` Interface and `TriggerResult` Enum

**Files:**
- Create: `src/Chronicle.Services/IScheduledTask.cs`
- Create: `src/Chronicle.Services/ITaskSchedulerService.cs`

**Step 1: Create `IScheduledTask.cs`**

```csharp
namespace Chronicle.Services;

/// <summary>
/// Marker interface for a task managed by TaskSchedulerService.
/// Register implementations as IScheduledTask in DI to have them
/// automatically discovered, seeded into the DB, and scheduled.
/// </summary>
public interface IScheduledTask
{
    /// <summary>Unique stable key stored in background_tasks.task_id.</summary>
    string TaskId { get; }

    /// <summary>Human-readable name shown in the UI.</summary>
    string DisplayName { get; }

    /// <summary>One-sentence description shown under the task name in the UI.</summary>
    string Description { get; }

    /// <summary>5-field cron expression used to seed the DB on first startup.</summary>
    string DefaultCron { get; }

    /// <summary>
    /// Execute one run of this task.
    /// Must not throw — callers catch and persist exceptions.
    /// </summary>
    Task ExecuteAsync(CancellationToken ct);
}
```

**Step 2: Create `ITaskSchedulerService.cs`**

```csharp
namespace Chronicle.Services;

public enum TriggerResult
{
    Started,
    AlreadyRunning,
    NotFound
}

/// <summary>
/// Exposes scheduler operations to the API layer without coupling to BackgroundService.
/// </summary>
public interface ITaskSchedulerService
{
    /// <summary>Returns true if the given task is currently executing.</summary>
    bool IsRunning(string taskId);

    /// <summary>
    /// Fires the task immediately if it is not already running.
    /// Returns Started, AlreadyRunning, or NotFound.
    /// </summary>
    Task<TriggerResult> TriggerNowAsync(string taskId, CancellationToken ct = default);
}
```

**Step 3: Build**

```bash
cd src/Chronicle.Services && dotnet build
```

Expected: Build succeeded.

**Step 4: Commit**

```bash
git add src/Chronicle.Services/IScheduledTask.cs src/Chronicle.Services/ITaskSchedulerService.cs
git commit -m "feat(services): add IScheduledTask interface and ITaskSchedulerService"
```

---

## Task 5: Write Failing Unit Tests for `TaskSchedulerService`

**Files:**
- Create: `tests/Chronicle.Tests.Unit/Services/TaskSchedulerServiceTests.cs`

**Step 1: Create the test file**

```csharp
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
            CronExpression = "0 2 * * *",   // user-customised schedule
            IsEnabled = true,
            NextRunAt = DateTime.UtcNow.AddHours(2)
        });
        await db.SaveChangesAsync();

        var scopeFactory = MakeScopeFactory(db);
        var task = MakeTask("metadata_refresh", "0 */4 * * *");
        var svc = new TaskSchedulerService(new[] { task.Object }, scopeFactory);

        await svc.SeedTasksAsync(CancellationToken.None);

        // cron should still be the user's custom value, not the default
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
            NextRunAt = DateTime.UtcNow.AddMinutes(-1)  // overdue
        });
        await db.SaveChangesAsync();

        var scopeFactory = MakeScopeFactory(db);
        var task = MakeTask("test_task");
        var svc = new TaskSchedulerService(new[] { task.Object }, scopeFactory);

        await svc.TickAsync(CancellationToken.None);
        await Task.Delay(200);  // let background Task.Run complete

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

        // task that takes 500ms — long enough for tick to "see" it running
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

        // First tick — should fire
        await svc.TickAsync(CancellationToken.None);
        await Task.Delay(50);  // let Task.Run start

        // Update next_run_at so second tick sees it as due again
        db.BackgroundTasks.First().NextRunAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        // Second tick — should skip because task is still running
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

        await svc.TriggerNowAsync("test_task");  // first trigger — starts
        await Task.Delay(50);                    // let Task.Run register running state

        var second = await svc.TriggerNowAsync("test_task");  // second — already running
        second.Should().Be(TriggerResult.AlreadyRunning);
        tcs.SetResult();
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

        // Should not throw
        await svc.TriggerNowAsync("failing_task");
        await Task.Delay(200);

        var row = await db.BackgroundTasks.FindAsync("failing_task");
        row!.LastRunSucceeded.Should().BeFalse();
        row.LastErrorMessage.Should().Be("Something broke.");
        svc.IsRunning("failing_task").Should().BeFalse();
    }
}
```

**Step 2: Run tests to verify they fail**

```bash
cd tests/Chronicle.Tests.Unit
dotnet test --filter "TaskSchedulerServiceTests" --verbosity normal
```

Expected: Build errors because `TaskSchedulerService` doesn't exist yet. That's correct — proceed to Task 6.

---

## Task 6: Implement `TaskSchedulerService`

**Files:**
- Create: `src/Chronicle.Services/TaskSchedulerService.cs`

**Step 1: Create the service**

```csharp
using System.Collections.Concurrent;
using Chronicle.Data;
using Cronos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Chronicle.Services;

/// <summary>
/// Central scheduler that drives all registered IScheduledTask implementations.
/// Ticks every 30 seconds, fires tasks whose next_run_at has elapsed,
/// and prevents concurrent execution of the same task by any trigger path.
/// </summary>
public sealed class TaskSchedulerService : BackgroundService, ITaskSchedulerService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyList<IScheduledTask> _tasks;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<string, bool> _running = new();
    private readonly ILogger _log = Log.ForContext<TaskSchedulerService>();

    public TaskSchedulerService(
        IEnumerable<IScheduledTask> tasks,
        IServiceScopeFactory scopeFactory)
    {
        _tasks = tasks.ToList();
        _scopeFactory = scopeFactory;
    }

    // ── BackgroundService ─────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.Information("TaskSchedulerService starting with {Count} task(s)", _tasks.Count);
        await SeedTasksAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Error(ex, "TaskSchedulerService: unhandled error in tick");
            }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ── ITaskSchedulerService ─────────────────────────────────────────────────

    public bool IsRunning(string taskId) => _running.ContainsKey(taskId);

    public async Task<TriggerResult> TriggerNowAsync(
        string taskId, CancellationToken ct = default)
    {
        var task = _tasks.FirstOrDefault(t => t.TaskId == taskId);
        if (task is null)
            return TriggerResult.NotFound;

        if (!_running.TryAdd(taskId, true))
            return TriggerResult.AlreadyRunning;

        _ = Task.Run(() => RunTaskAsync(task, CancellationToken.None), CancellationToken.None);
        return TriggerResult.Started;
    }

    // ── Internals (internal for unit tests) ──────────────────────────────────

    internal async Task SeedTasksAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        foreach (var task in _tasks)
        {
            var existing = await db.BackgroundTasks.FindAsync([task.TaskId], ct);
            if (existing is not null) continue;

            var nextRun = GetNextOccurrence(task.DefaultCron);
            db.BackgroundTasks.Add(new Chronicle.Core.Models.BackgroundTask
            {
                TaskId        = task.TaskId,
                DisplayName   = task.DisplayName,
                Description   = task.Description,
                CronExpression= task.DefaultCron,
                IsEnabled     = true,
                NextRunAt     = nextRun
            });

            _log.Information("TaskScheduler: seeded task '{TaskId}' with cron '{Cron}'",
                task.TaskId, task.DefaultCron);
        }

        await db.SaveChangesAsync(ct);
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db  = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var now = DateTime.UtcNow;

        var dueRows = await db.BackgroundTasks
            .Where(t => t.IsEnabled && t.NextRunAt != null && t.NextRunAt <= now)
            .ToListAsync(ct);

        foreach (var row in dueRows)
        {
            var task = _tasks.FirstOrDefault(t => t.TaskId == row.TaskId);
            if (task is null)
            {
                _log.Warning("TaskScheduler: no IScheduledTask found for DB row '{TaskId}'", row.TaskId);
                continue;
            }

            if (!_running.TryAdd(row.TaskId, true))
            {
                _log.Warning("TaskScheduler: '{TaskId}' is already running — skipping scheduled fire", row.TaskId);
                row.NextRunAt = GetNextOccurrence(row.CronExpression);
                continue;
            }

            row.NextRunAt = GetNextOccurrence(row.CronExpression);
            _ = Task.Run(() => RunTaskAsync(task, CancellationToken.None), CancellationToken.None);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task RunTaskAsync(IScheduledTask task, CancellationToken ct)
    {
        _log.Information("TaskScheduler: starting '{TaskId}'", task.TaskId);
        var startedAt = DateTime.UtcNow;

        try
        {
            await task.ExecuteAsync(ct);
            await PersistRunResultAsync(task.TaskId, startedAt, succeeded: true, error: null);
            _log.Information("TaskScheduler: '{TaskId}' completed successfully in {Elapsed:F1}s",
                task.TaskId, (DateTime.UtcNow - startedAt).TotalSeconds);
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            _log.Error(ex, "TaskScheduler: '{TaskId}' failed", task.TaskId);
            await PersistRunResultAsync(task.TaskId, startedAt, succeeded: false, error: msg);
        }
        finally
        {
            _running.TryRemove(task.TaskId, out _);
        }
    }

    private async Task PersistRunResultAsync(
        string taskId, DateTime lastRunAt, bool succeeded, string? error)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db  = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var row = await db.BackgroundTasks.FindAsync(taskId);
            if (row is null) return;

            row.LastRunAt        = lastRunAt;
            row.LastRunSucceeded = succeeded;
            row.LastErrorMessage = succeeded ? null : error;
            row.NextRunAt        = GetNextOccurrence(row.CronExpression);

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "TaskScheduler: failed to persist run result for '{TaskId}'", taskId);
        }
    }

    private static DateTime? GetNextOccurrence(string cronExpression)
    {
        try
        {
            var expr = CronExpression.Parse(cronExpression);
            return expr.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Utc);
        }
        catch
        {
            return null;
        }
    }
}
```

**Step 2: Run the unit tests**

```bash
cd tests/Chronicle.Tests.Unit
dotnet test --filter "TaskSchedulerServiceTests" --verbosity normal
```

Expected: All `TaskSchedulerServiceTests` pass.

**Step 3: Run the full test suite to make sure nothing broke**

```bash
dotnet test --verbosity normal
```

Expected: All tests pass (same count as before this task).

**Step 4: Commit**

```bash
git add src/Chronicle.Services/TaskSchedulerService.cs
git commit -m "feat(services): implement TaskSchedulerService with cron scheduling and run-now support"
```

---

## Task 7: Refactor `MetadataRefreshService` to Implement `IScheduledTask`

**Files:**
- Modify: `src/Chronicle.Services/MetadataRefreshService.cs`

**Step 1: Make the changes**

Change the class declaration from:
```csharp
public sealed class MetadataRefreshService : BackgroundService, IMetadataRefreshService
```
to:
```csharp
public sealed class MetadataRefreshService : IScheduledTask, IMetadataRefreshService
```

Add these properties at the top of the class (after the field declarations):

```csharp
// ── IScheduledTask ────────────────────────────────────────────────────────────
public string TaskId      => "metadata_refresh";
public string DisplayName => "Metadata Refresh";
public string Description => "Refreshes titles, posters, and metadata for all library items using active metadata plugins.";
public string DefaultCron => "0 */4 * * *";   // every 4 hours

async Task IScheduledTask.ExecuteAsync(CancellationToken ct) => await RefreshAllAsync(ct);
```

Remove the `BackgroundService` lifecycle method entirely — delete the `protected override async Task ExecuteAsync(...)` method and its inner `while` loop, the startup delay constant, and `GetIntervalAsync`. The `StartupDelay` and `DefaultInterval` constants can be removed.

The class should retain: `_scopeFactory`, `_log`, `RefreshAllAsync`, `RefreshItemAsync`, `GetRefreshLogsAsync`, and all private helpers.

**Step 2: Build**

```bash
cd src/Chronicle.Services && dotnet build
```

Expected: Build succeeded. (The existing unit tests for MetadataRefreshService will still pass since they test `RefreshAllAsync` directly.)

**Step 3: Run existing unit tests**

```bash
cd tests/Chronicle.Tests.Unit
dotnet test --filter "MetadataRefreshServiceTests" --verbosity normal
```

Expected: All pass.

**Step 4: Commit**

```bash
git add src/Chronicle.Services/MetadataRefreshService.cs
git commit -m "refactor(services): migrate MetadataRefreshService to IScheduledTask"
```

---

## Task 8: Refactor `DuplicateCleanupService` to Implement `IScheduledTask`

**Files:**
- Modify: `src/Chronicle.Services/DuplicateCleanupService.cs`

**Step 1: Make the changes**

Change the class declaration from:
```csharp
public sealed class DuplicateCleanupService : BackgroundService
```
to:
```csharp
public sealed class DuplicateCleanupService : IScheduledTask
```

Add these properties and the `ExecuteAsync` bridge at the top of the class body:

```csharp
// ── IScheduledTask ────────────────────────────────────────────────────────────
public string TaskId      => "duplicate_cleanup";
public string DisplayName => "Duplicate Cleanup";
public string Description => "Scans for duplicate media items sharing the same file path and removes all but the best-quality copy.";
public string DefaultCron => "0 3 * * *";   // 3:00 AM daily

async Task IScheduledTask.ExecuteAsync(CancellationToken ct)
{
    var removed = await RunAsync(ct);
    if (removed > 0)
        _log.Information("DuplicateCleanup: removed {Count} duplicate media items", removed);
}
```

Remove the `protected override async Task ExecuteAsync(...)` method and its `while` loop, plus the `StartupDelay` and `Interval` constants.

**Step 2: Build and test**

```bash
cd src/Chronicle.Services && dotnet build
cd tests/Chronicle.Tests.Unit && dotnet test --verbosity normal
```

Expected: All pass.

**Step 3: Commit**

```bash
git add src/Chronicle.Services/DuplicateCleanupService.cs
git commit -m "refactor(services): migrate DuplicateCleanupService to IScheduledTask"
```

---

## Task 9: Update `Program.cs` Registrations

**Files:**
- Modify: `src/Chronicle.API/Program.cs`

**Step 1: Replace the background service registrations**

Find and remove these lines (around lines 141–153):

```csharp
// ── Background duplicate cleanup ──────────────────────────────────────────────
// ...
builder.Services.AddHostedService<DuplicateCleanupService>();

// ── Background metadata refresh ───────────────────────────────────────────────
// ...
builder.Services.AddSingleton<MetadataRefreshService>();
builder.Services.AddSingleton<IMetadataRefreshService>(sp => sp.GetRequiredService<MetadataRefreshService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<MetadataRefreshService>());
```

Replace with:

```csharp
// ── Scheduled background tasks ────────────────────────────────────────────────
// Each IScheduledTask is a singleton so both its IScheduledTask role (consumed by
// TaskSchedulerService) and any additional service interfaces share one instance.
// TaskSchedulerService discovers all IScheduledTask registrations via
// IEnumerable<IScheduledTask> in its constructor.
builder.Services.AddSingleton<MetadataRefreshService>();
builder.Services.AddSingleton<IMetadataRefreshService>(
    sp => sp.GetRequiredService<MetadataRefreshService>());
builder.Services.AddSingleton<IScheduledTask>(
    sp => sp.GetRequiredService<MetadataRefreshService>());

builder.Services.AddSingleton<DuplicateCleanupService>();
builder.Services.AddSingleton<IScheduledTask>(
    sp => sp.GetRequiredService<DuplicateCleanupService>());

builder.Services.AddSingleton<TaskSchedulerService>();
builder.Services.AddSingleton<ITaskSchedulerService>(
    sp => sp.GetRequiredService<TaskSchedulerService>());
builder.Services.AddHostedService(
    sp => sp.GetRequiredService<TaskSchedulerService>());
```

Also remove the `metadata_refresh_interval_hours` seed data from `ChronicleDbContext.OnModelCreating` — it is no longer needed since the schedule is now stored in `background_tasks`. Find this block in `src/Chronicle.Data/ChronicleDbContext.cs` and remove the `HasData` call:

```csharp
// Remove this line:
e.HasData(new AppSetting { Key = "metadata_refresh_interval_hours", Value = "4" });
```

Since removing a `HasData` seed is a DB schema change, create a migration to remove the now-obsolete seed row:

```bash
cd src/Chronicle.API
dotnet ef migrations add RemoveMetadataRefreshIntervalSetting --project ../Chronicle.Data
```

**Step 2: Build the full solution**

```bash
cd src/Chronicle.API && dotnet build
```

Expected: Build succeeded.

**Step 3: Run all tests**

```bash
cd tests && dotnet test --verbosity normal
```

Expected: All tests pass.

**Step 4: Commit**

```bash
git add src/Chronicle.API/Program.cs src/Chronicle.Data/
git commit -m "feat(api): wire TaskSchedulerService as the central background task runner"
```

---

## Task 10: Write Failing Integration Tests for `BackgroundTasksController`

**Files:**
- Create: `tests/Chronicle.Tests.Integration/BackgroundTasksTests.cs`

**Step 1: Create the test file**

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Chronicle.Tests.Integration;

public class BackgroundTasksTests : IClassFixture<ChronicleApiFactory>
{
    private readonly ChronicleApiFactory _factory;

    public BackgroundTasksTests(ChronicleApiFactory factory)
    {
        factory.SeedDatabase();
        _factory = factory;
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = _factory.CreateClient();
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { username = $"bg_{Guid.NewGuid():N}", password = "Password123!" });
        var token = JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task GetTasks_Authenticated_ReturnsTaskList()
    {
        var client = await AdminClientAsync();

        var resp = await client.GetAsync("/api/v1/background-tasks");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        doc.GetProperty("success").GetBoolean().Should().BeTrue();

        var tasks = doc.GetProperty("data");
        tasks.GetArrayLength().Should().BeGreaterThan(0);

        // Spot-check first task shape
        var first = tasks[0];
        first.TryGetProperty("taskId", out _).Should().BeTrue();
        first.TryGetProperty("displayName", out _).Should().BeTrue();
        first.TryGetProperty("cronExpression", out _).Should().BeTrue();
        first.TryGetProperty("isEnabled", out _).Should().BeTrue();
        first.TryGetProperty("isRunning", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetTasks_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/background-tasks");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PatchTask_ValidCron_PersistsChange()
    {
        var client = await AdminClientAsync();

        // Get the first task id
        var listResp = await client.GetAsync("/api/v1/background-tasks");
        var tasks = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data");
        var taskId = tasks[0].GetProperty("taskId").GetString()!;

        var patchResp = await client.PatchAsJsonAsync(
            $"/api/v1/background-tasks/{taskId}",
            new { cronExpression = "0 2 * * *", isEnabled = true });

        patchResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Re-fetch and verify
        var getResp = await client.GetAsync("/api/v1/background-tasks");
        var updated = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data")
            .EnumerateArray()
            .First(t => t.GetProperty("taskId").GetString() == taskId);

        updated.GetProperty("cronExpression").GetString().Should().Be("0 2 * * *");
    }

    [Fact]
    public async Task PatchTask_InvalidCron_Returns400WithFriendlyMessage()
    {
        var client = await AdminClientAsync();

        var listResp = await client.GetAsync("/api/v1/background-tasks");
        var taskId = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data")[0].GetProperty("taskId").GetString()!;

        var resp = await client.PatchAsJsonAsync(
            $"/api/v1/background-tasks/{taskId}",
            new { cronExpression = "not a cron" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("success").GetBoolean().Should().BeFalse();
        var msg = body.GetProperty("error").GetProperty("message").GetString()!;
        msg.Should().Contain("cron expression");
    }

    [Fact]
    public async Task PatchTask_UnknownId_Returns404()
    {
        var client = await AdminClientAsync();
        var resp = await client.PatchAsJsonAsync(
            "/api/v1/background-tasks/does_not_exist",
            new { isEnabled = false });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RunTask_ValidId_Returns202()
    {
        var client = await AdminClientAsync();

        var listResp = await client.GetAsync("/api/v1/background-tasks");
        var taskId = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data")[0].GetProperty("taskId").GetString()!;

        var resp = await client.PostAsync($"/api/v1/background-tasks/{taskId}/run", null);
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task RunTask_UnknownId_Returns404()
    {
        var client = await AdminClientAsync();
        var resp = await client.PostAsync("/api/v1/background-tasks/ghost_task/run", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
```

**Step 2: Run tests to verify they fail**

```bash
cd tests/Chronicle.Tests.Integration
dotnet test --filter "BackgroundTasksTests" --verbosity normal
```

Expected: `NotFound` (404) on all requests because the controller doesn't exist yet. Proceed to Task 11.

---

## Task 11: Implement `BackgroundTasksController`

**Files:**
- Create: `src/Chronicle.API/Controllers/BackgroundTasksController.cs`

**Step 1: Create the controller**

```csharp
using Chronicle.Data;
using Chronicle.Services;
using Cronos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.API.Controllers;

[ApiController]
[Route("api/v1/background-tasks")]
[Authorize]
public class BackgroundTasksController : ControllerBase
{
    private readonly ChronicleDbContext _db;
    private readonly ITaskSchedulerService _scheduler;

    public BackgroundTasksController(
        ChronicleDbContext db,
        ITaskSchedulerService scheduler)
    {
        _db        = db;
        _scheduler = scheduler;
    }

    /// <summary>Returns all registered background tasks with live status.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var rows = await _db.BackgroundTasks.OrderBy(t => t.DisplayName).ToListAsync();

        var dtos = rows.Select(r => new BackgroundTaskDto(
            TaskId:           r.TaskId,
            DisplayName:      r.DisplayName,
            Description:      r.Description,
            CronExpression:   r.CronExpression,
            IsEnabled:        r.IsEnabled,
            IsRunning:        _scheduler.IsRunning(r.TaskId),
            LastRunAt:        r.LastRunAt,
            LastRunSucceeded: r.LastRunSucceeded,
            LastErrorMessage: r.LastErrorMessage,
            NextRunAt:        r.NextRunAt
        ));

        return Ok(new { success = true, data = dtos });
    }

    /// <summary>Updates a task's schedule and/or enabled state.</summary>
    [HttpPatch("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateBackgroundTaskRequest body)
    {
        var row = await _db.BackgroundTasks.FindAsync(id);
        if (row is null)
            return NotFound(new
            {
                success = false,
                error = new { code = "TASK_NOT_FOUND", message = $"No background task with ID '{id}' was found." }
            });

        if (body.CronExpression is not null)
        {
            if (!TryParseCron(body.CronExpression, out var parsed))
                return BadRequest(new
                {
                    success = false,
                    error = new
                    {
                        code = "INVALID_CRON",
                        message = $"The cron expression '{body.CronExpression}' is not valid. " +
                                  "A cron expression has five fields: minute, hour, day-of-month, month, day-of-week. " +
                                  "Example: 0 */4 * * * (every 4 hours)."
                    }
                });

            row.CronExpression = body.CronExpression;
            row.NextRunAt      = parsed!.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Utc);
        }

        if (body.IsEnabled.HasValue)
            row.IsEnabled = body.IsEnabled.Value;

        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Triggers a task immediately. Returns 409 if already running.</summary>
    [HttpPost("{id}/run")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> RunNow(string id)
    {
        var result = await _scheduler.TriggerNowAsync(id);

        return result switch
        {
            TriggerResult.Started      => Accepted(new { success = true, message = "Task started." }),
            TriggerResult.AlreadyRunning => Conflict(new
            {
                success = false,
                error = new
                {
                    code = "TASK_ALREADY_RUNNING",
                    message = "This task is already running. Wait for it to finish before running it again."
                }
            }),
            TriggerResult.NotFound => NotFound(new
            {
                success = false,
                error = new { code = "TASK_NOT_FOUND", message = $"No background task with ID '{id}' was found." }
            }),
            _ => StatusCode(500)
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool TryParseCron(string expression, out CronExpression? parsed)
    {
        try
        {
            parsed = CronExpression.Parse(expression);
            return true;
        }
        catch
        {
            parsed = null;
            return false;
        }
    }
}

public record BackgroundTaskDto(
    string TaskId,
    string DisplayName,
    string Description,
    string CronExpression,
    bool IsEnabled,
    bool IsRunning,
    DateTime? LastRunAt,
    bool? LastRunSucceeded,
    string? LastErrorMessage,
    DateTime? NextRunAt
);

public record UpdateBackgroundTaskRequest(
    string? CronExpression,
    bool? IsEnabled
);
```

**Step 2: Run the integration tests**

```bash
cd tests/Chronicle.Tests.Integration
dotnet test --filter "BackgroundTasksTests" --verbosity normal
```

Expected: All `BackgroundTasksTests` pass.

**Step 3: Run all tests**

```bash
cd tests && dotnet test --verbosity normal
```

Expected: All tests pass.

**Step 4: Commit**

```bash
git add src/Chronicle.API/Controllers/BackgroundTasksController.cs
git commit -m "feat(api): add BackgroundTasksController with GET, PATCH, and run-now endpoints"
```

---

## Task 12: Frontend API Client

**Files:**
- Create: `src/Chronicle.Web/src/api/backgroundTasks.ts`

**Step 1: Create the API client**

```typescript
import client from './client'

export interface BackgroundTask {
  taskId: string
  displayName: string
  description: string
  cronExpression: string
  isEnabled: boolean
  isRunning: boolean
  lastRunAt: string | null        // UTC ISO-8601
  lastRunSucceeded: boolean | null
  lastErrorMessage: string | null
  nextRunAt: string | null        // UTC ISO-8601
}

export async function getBackgroundTasks(): Promise<BackgroundTask[]> {
  const res = await client.get<{ success: true; data: BackgroundTask[] }>('/background-tasks')
  return res.data.data
}

export async function updateBackgroundTask(
  taskId: string,
  patch: { cronExpression?: string; isEnabled?: boolean },
): Promise<void> {
  await client.patch(`/background-tasks/${taskId}`, patch)
}

export async function runBackgroundTask(taskId: string): Promise<void> {
  await client.post(`/background-tasks/${taskId}/run`)
}
```

**Step 2: Commit**

```bash
git add src/Chronicle.Web/src/api/backgroundTasks.ts
git commit -m "feat(web): add backgroundTasks API client"
```

---

## Task 13: Cron ↔ Visual Builder Utility

**Files:**
- Create: `src/Chronicle.Web/src/utils/cronBuilder.ts`

**Step 1: Create the utility**

```typescript
export type Frequency = 'minutes' | 'hours' | 'daily' | 'weekly' | 'monthly'

export interface ScheduleParams {
  frequency: Frequency
  interval: number        // "every N units" (minutes: 1–59, hours: 1–23)
  timeHour: number        // 0–23 (daily / weekly / monthly)
  timeMinute: number      // 0–59 (daily / weekly / monthly)
  daysOfWeek: number[]    // 0=Sun … 6=Sat (weekly)
  dayOfMonth: number      // 1–31 (monthly)
}

export const DEFAULT_PARAMS: ScheduleParams = {
  frequency: 'hours',
  interval: 4,
  timeHour: 2,
  timeMinute: 0,
  daysOfWeek: [1],  // Monday
  dayOfMonth: 1,
}

/** Convert ScheduleParams → 5-field cron string. */
export function paramsToCron(p: ScheduleParams): string {
  switch (p.frequency) {
    case 'minutes':
      return `*/${p.interval} * * * *`
    case 'hours':
      return `0 */${p.interval} * * *`
    case 'daily':
      return `${p.timeMinute} ${p.timeHour} * * *`
    case 'weekly': {
      const days = p.daysOfWeek.length > 0 ? p.daysOfWeek.join(',') : '1'
      return `${p.timeMinute} ${p.timeHour} * * ${days}`
    }
    case 'monthly':
      return `${p.timeMinute} ${p.timeHour} ${p.dayOfMonth} * *`
  }
}

const MINUTES_RE = /^\*\/(\d+) \* \* \* \*$/
const HOURS_RE   = /^0 \*\/(\d+) \* \* \*$/
const DAILY_RE   = /^(\d+) (\d+) \* \* \*$/
const WEEKLY_RE  = /^(\d+) (\d+) \* \* ([\d,]+)$/
const MONTHLY_RE = /^(\d+) (\d+) (\d+) \* \*$/

/**
 * Parse a cron string into ScheduleParams.
 * Returns null if the expression can't be represented by the visual builder
 * (e.g. complex expressions with ranges or step values in unexpected positions).
 */
export function cronToParams(cron: string): ScheduleParams | null {
  let m: RegExpMatchArray | null

  m = cron.match(MINUTES_RE)
  if (m) return { ...DEFAULT_PARAMS, frequency: 'minutes', interval: parseInt(m[1]) }

  m = cron.match(HOURS_RE)
  if (m) return { ...DEFAULT_PARAMS, frequency: 'hours', interval: parseInt(m[1]) }

  m = cron.match(DAILY_RE)
  if (m) return {
    ...DEFAULT_PARAMS,
    frequency: 'daily',
    timeMinute: parseInt(m[1]),
    timeHour: parseInt(m[2]),
  }

  m = cron.match(WEEKLY_RE)
  if (m) return {
    ...DEFAULT_PARAMS,
    frequency: 'weekly',
    timeMinute: parseInt(m[1]),
    timeHour: parseInt(m[2]),
    daysOfWeek: m[3].split(',').map(Number),
  }

  m = cron.match(MONTHLY_RE)
  if (m) return {
    ...DEFAULT_PARAMS,
    frequency: 'monthly',
    timeMinute: parseInt(m[1]),
    timeHour: parseInt(m[2]),
    dayOfMonth: parseInt(m[3]),
  }

  return null
}

const DOW_NAMES = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']

/** Human-readable summary of a ScheduleParams, e.g. "Every 4 hours" */
export function describeSchedule(p: ScheduleParams): string {
  const pad = (n: number) => String(n).padStart(2, '0')
  const time = `${pad(p.timeHour)}:${pad(p.timeMinute)}`

  switch (p.frequency) {
    case 'minutes':
      return p.interval === 1 ? 'Every minute' : `Every ${p.interval} minutes`
    case 'hours':
      return p.interval === 1 ? 'Every hour' : `Every ${p.interval} hours`
    case 'daily':
      return `Daily at ${time}`
    case 'weekly': {
      const days = p.daysOfWeek.map(d => DOW_NAMES[d] ?? d).join(', ')
      return `Every ${days} at ${time}`
    }
    case 'monthly':
      return `Monthly on day ${p.dayOfMonth} at ${time}`
  }
}

/** Validate ScheduleParams and return an error string or null if valid. */
export function validateParams(p: ScheduleParams): string | null {
  if (p.frequency === 'minutes' && (p.interval < 1 || p.interval > 59))
    return 'Interval must be between 1 and 59 minutes.'
  if (p.frequency === 'hours' && (p.interval < 1 || p.interval > 23))
    return 'Interval must be between 1 and 23 hours.'
  if (p.frequency === 'weekly' && p.daysOfWeek.length === 0)
    return 'Select at least one day of the week.'
  if (p.frequency === 'monthly' && (p.dayOfMonth < 1 || p.dayOfMonth > 31))
    return 'Day of month must be between 1 and 31.'
  return null
}
```

**Step 2: Commit**

```bash
git add src/Chronicle.Web/src/utils/cronBuilder.ts
git commit -m "feat(web): add cron <-> visual builder utility"
```

---

## Task 14: Background Tasks Page Component

**Files:**
- Create: `src/Chronicle.Web/src/pages/settings/BackgroundTasksPage.tsx`
- Create: `src/Chronicle.Web/src/pages/settings/BackgroundTasksPage.module.css`

**Step 1: Create the CSS module**

```css
/* BackgroundTasksPage.module.css */

.page {
  padding: 24px;
  max-width: 860px;
}

.title {
  font-size: 1.5rem;
  font-weight: 600;
  margin: 0 0 24px;
}

/* ── Task card ─────────────────────────────────────────────── */

.card {
  background: var(--bg-card);
  border: 1px solid var(--border);
  border-radius: 6px;
  padding: 20px;
  margin-bottom: 16px;
}

.cardHeader {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 12px;
}

.cardTitleGroup {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.taskName {
  font-size: 1.05rem;
  font-weight: 600;
  margin: 0;
}

.taskDesc {
  font-size: 0.85rem;
  color: var(--text-muted);
  margin: 0;
}

.cardActions {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-shrink: 0;
}

/* ── Status badge ──────────────────────────────────────────── */

.badge {
  display: inline-block;
  padding: 2px 8px;
  border-radius: 3px;
  font-size: 0.75rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.03em;
}
.idle    { background: var(--bg-muted); color: var(--text-muted); }
.running { background: #1a4d8f; color: #a8ccff; animation: pulse 1.5s infinite; }
.success { background: #1a4d2a; color: #6fcf97; }
.failed  { background: #4d1a1a; color: #eb5757; }

@keyframes pulse {
  0%, 100% { opacity: 1; }
  50%       { opacity: 0.6; }
}

/* ── Meta grid ─────────────────────────────────────────────── */

.metaGrid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 8px 24px;
  margin-bottom: 14px;
}

.metaRow {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.metaLabel {
  font-size: 0.72rem;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  color: var(--text-muted);
}

.metaValue {
  font-size: 0.88rem;
}

.errorText {
  font-size: 0.83rem;
  color: #eb5757;
  margin: 0 0 12px;
  padding: 8px 12px;
  background: rgba(235, 87, 87, 0.08);
  border-left: 3px solid #eb5757;
  border-radius: 3px;
}

/* ── Buttons ───────────────────────────────────────────────── */

.runBtn {
  padding: 6px 14px;
  border-radius: 4px;
  border: none;
  background: var(--accent);
  color: #fff;
  font-size: 0.85rem;
  cursor: pointer;
  white-space: nowrap;
}
.runBtn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.editBtn {
  padding: 5px 12px;
  border-radius: 4px;
  border: 1px solid var(--border);
  background: transparent;
  color: var(--text);
  font-size: 0.85rem;
  cursor: pointer;
}
.editBtn:hover { background: var(--bg-hover); }

/* ── Toggle ────────────────────────────────────────────────── */

.toggle {
  position: relative;
  display: inline-flex;
  align-items: center;
  width: 36px;
  height: 20px;
  border-radius: 10px;
  border: none;
  background: var(--bg-muted);
  cursor: pointer;
  transition: background 0.2s;
  flex-shrink: 0;
}
.toggleOn { background: var(--accent); }
.toggleThumb {
  position: absolute;
  left: 3px;
  width: 14px;
  height: 14px;
  border-radius: 50%;
  background: #fff;
  transition: left 0.2s;
}
.toggleOn .toggleThumb { left: 19px; }

/* ── Schedule editor ───────────────────────────────────────── */

.scheduleEditor {
  border-top: 1px solid var(--border);
  padding-top: 16px;
  margin-top: 4px;
}

.editorTitle {
  font-size: 0.9rem;
  font-weight: 600;
  margin: 0 0 14px;
}

.formRow {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  margin-bottom: 10px;
}

.label {
  font-size: 0.85rem;
  color: var(--text-muted);
  white-space: nowrap;
}

.select,
.numberInput,
.timeInput,
.cronInput {
  padding: 5px 9px;
  border-radius: 4px;
  border: 1px solid var(--border);
  background: var(--bg-input);
  color: var(--text);
  font-size: 0.88rem;
}

.numberInput { width: 64px; }
.timeInput   { width: 88px; }
.cronInput   { width: 180px; font-family: monospace; }

.dowRow {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
  margin-bottom: 10px;
}

.dowBtn {
  padding: 4px 10px;
  border-radius: 4px;
  border: 1px solid var(--border);
  background: transparent;
  color: var(--text-muted);
  font-size: 0.8rem;
  cursor: pointer;
}
.dowBtnActive {
  background: var(--accent);
  border-color: var(--accent);
  color: #fff;
}

.preview {
  font-size: 0.82rem;
  color: var(--text-muted);
  margin-bottom: 12px;
  font-style: italic;
}

.cronPreview {
  font-size: 0.82rem;
  margin-top: 6px;
}
.cronOk  { color: #6fcf97; }
.cronErr { color: #eb5757; }

.editorButtons {
  display: flex;
  gap: 8px;
  margin-top: 14px;
}

.saveBtn {
  padding: 6px 16px;
  border-radius: 4px;
  border: none;
  background: var(--accent);
  color: #fff;
  font-size: 0.85rem;
  cursor: pointer;
}
.saveBtn:disabled { opacity: 0.5; cursor: not-allowed; }

.cancelBtn {
  padding: 6px 14px;
  border-radius: 4px;
  border: 1px solid var(--border);
  background: transparent;
  color: var(--text);
  font-size: 0.85rem;
  cursor: pointer;
}

.fieldError {
  font-size: 0.82rem;
  color: #eb5757;
  margin-top: 4px;
}

.saveError {
  font-size: 0.83rem;
  color: #eb5757;
  margin-top: 8px;
}

.loading { color: var(--text-muted); padding: 24px 0; }
.errorMsg { color: #eb5757; padding: 24px 0; }
```

**Step 2: Create the page component**

```tsx
import { useState, useEffect, useCallback } from 'react'
import AdvancedToggle from '@/components/ui/AdvancedToggle'
import {
  getBackgroundTasks,
  updateBackgroundTask,
  runBackgroundTask,
  type BackgroundTask,
} from '@/api/backgroundTasks'
import {
  cronToParams,
  paramsToCron,
  describeSchedule,
  validateParams,
  DEFAULT_PARAMS,
  type Frequency,
  type ScheduleParams,
} from '@/utils/cronBuilder'
import { ApiError } from '@/api/client'
import styles from './BackgroundTasksPage.module.css'

// ── Helpers ─────────────────────────────────────────────────────────────────

/** Format a UTC ISO string as a local datetime string. */
function fmtLocal(iso: string | null): string {
  if (!iso) return 'Never'
  return new Date(iso).toLocaleString(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short',
  })
}

/** Format a UTC ISO string as a relative string (e.g. "2 hours ago"). */
function fmtRelative(iso: string | null): string {
  if (!iso) return 'Never'
  const diffMs = Date.now() - new Date(iso).getTime()
  const diffSec = Math.round(diffMs / 1000)
  if (Math.abs(diffSec) < 60) return 'Just now'
  const diffMin = Math.round(diffSec / 60)
  if (Math.abs(diffMin) < 60) return `${Math.abs(diffMin)}m ${diffMs > 0 ? 'ago' : 'from now'}`
  const diffHr = Math.round(diffMin / 60)
  if (Math.abs(diffHr) < 24) return `${Math.abs(diffHr)}h ${diffMs > 0 ? 'ago' : 'from now'}`
  const diffDay = Math.round(diffHr / 24)
  return `${Math.abs(diffDay)}d ${diffMs > 0 ? 'ago' : 'from now'}`
}

function statusBadge(task: BackgroundTask) {
  if (task.isRunning) return { cls: styles.running, label: 'Running' }
  if (task.lastRunSucceeded === null) return { cls: styles.idle, label: 'Idle' }
  if (task.lastRunSucceeded) return { cls: styles.success, label: 'Success' }
  return { cls: styles.failed, label: 'Failed' }
}

const DOW_LABELS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']

// ── Schedule editor ──────────────────────────────────────────────────────────

interface ScheduleEditorProps {
  taskId: string
  initialCron: string
  isEnabled: boolean
  onSave: (taskId: string, cron: string, enabled: boolean) => Promise<void>
  onCancel: () => void
}

function ScheduleEditor({ taskId, initialCron, isEnabled, onSave, onCancel }: ScheduleEditorProps) {
  const [params, setParams] = useState<ScheduleParams>(
    () => cronToParams(initialCron) ?? DEFAULT_PARAMS,
  )
  const [rawCron, setRawCron] = useState(initialCron)
  const [useRaw, setUseRaw]   = useState(cronToParams(initialCron) === null)
  const [enabled, setEnabled] = useState(isEnabled)
  const [saving, setSaving]   = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)

  // Keep rawCron in sync when visual builder changes
  function updateParams(next: Partial<ScheduleParams>) {
    const merged = { ...params, ...next }
    setParams(merged)
    setRawCron(paramsToCron(merged))
  }

  // When user edits raw cron, try to update visual builder
  function handleRawChange(val: string) {
    setRawCron(val)
    const parsed = cronToParams(val)
    if (parsed) setParams(parsed)
  }

  function validateRaw(): { ok: boolean; msg: string } {
    try {
      // Basic 5-field check
      const parts = rawCron.trim().split(/\s+/)
      if (parts.length !== 5) throw new Error('wrong field count')
      return { ok: true, msg: '' }
    } catch {
      return {
        ok: false,
        msg: "This isn't a valid cron expression. A cron expression has five fields: minute, hour, day-of-month, month, day-of-week. Example: 0 */4 * * * (every 4 hours).",
      }
    }
  }

  const validationError = useRaw ? validateRaw() : { ok: !validateParams(params), msg: validateParams(params) ?? '' }
  // Fix the inversion:
  const canSave = useRaw ? validateRaw().ok : !validateParams(params)

  async function handleSave() {
    if (!canSave) return
    setSaving(true)
    setSaveError(null)
    try {
      await onSave(taskId, useRaw ? rawCron.trim() : paramsToCron(params), enabled)
    } catch (err) {
      if (err instanceof ApiError) setSaveError(err.message)
      else setSaveError('An unexpected error occurred. Please try again.')
    } finally {
      setSaving(false)
    }
  }

  const freq = params.frequency

  return (
    <div className={styles.scheduleEditor}>
      <h3 className={styles.editorTitle}>Edit Schedule</h3>

      {/* Enable toggle */}
      <div className={styles.formRow}>
        <span className={styles.label}>Enabled</span>
        <button
          role="switch"
          aria-checked={enabled}
          className={`${styles.toggle} ${enabled ? styles.toggleOn : ''}`}
          onClick={() => setEnabled(!enabled)}
        >
          <span className={styles.toggleThumb} />
        </button>
      </div>

      {/* Frequency */}
      <div className={styles.formRow}>
        <span className={styles.label}>Frequency</span>
        <select
          className={styles.select}
          value={freq}
          onChange={e => updateParams({ frequency: e.target.value as Frequency })}
        >
          <option value="minutes">Minutes</option>
          <option value="hours">Hours</option>
          <option value="daily">Daily</option>
          <option value="weekly">Weekly</option>
          <option value="monthly">Monthly</option>
        </select>
      </div>

      {/* Interval (minutes / hours) */}
      {(freq === 'minutes' || freq === 'hours') && (
        <div className={styles.formRow}>
          <span className={styles.label}>Every</span>
          <input
            type="number"
            className={styles.numberInput}
            min={1}
            max={freq === 'minutes' ? 59 : 23}
            value={params.interval}
            onChange={e => updateParams({ interval: parseInt(e.target.value) || 1 })}
          />
          <span className={styles.label}>{freq}</span>
        </div>
      )}

      {/* Time of day (daily / weekly / monthly) */}
      {(freq === 'daily' || freq === 'weekly' || freq === 'monthly') && (
        <div className={styles.formRow}>
          <span className={styles.label}>At</span>
          <input
            type="time"
            className={styles.timeInput}
            value={`${String(params.timeHour).padStart(2, '0')}:${String(params.timeMinute).padStart(2, '0')}`}
            onChange={e => {
              const [h, m] = e.target.value.split(':').map(Number)
              updateParams({ timeHour: h, timeMinute: m })
            }}
          />
        </div>
      )}

      {/* Day of week (weekly) */}
      {freq === 'weekly' && (
        <div className={styles.dowRow}>
          {DOW_LABELS.map((label, i) => (
            <button
              key={i}
              className={`${styles.dowBtn} ${params.daysOfWeek.includes(i) ? styles.dowBtnActive : ''}`}
              onClick={() => {
                const next = params.daysOfWeek.includes(i)
                  ? params.daysOfWeek.filter(d => d !== i)
                  : [...params.daysOfWeek, i].sort()
                updateParams({ daysOfWeek: next })
              }}
            >
              {label}
            </button>
          ))}
        </div>
      )}

      {/* Day of month (monthly) */}
      {freq === 'monthly' && (
        <div className={styles.formRow}>
          <span className={styles.label}>On day</span>
          <input
            type="number"
            className={styles.numberInput}
            min={1}
            max={31}
            value={params.dayOfMonth}
            onChange={e => updateParams({ dayOfMonth: parseInt(e.target.value) || 1 })}
          />
          <span className={styles.label}>of the month</span>
        </div>
      )}

      {/* Validation error from visual builder */}
      {!useRaw && validateParams(params) && (
        <p className={styles.fieldError}>{validateParams(params)}</p>
      )}

      {/* Preview */}
      {!useRaw && (
        <p className={styles.preview}>{describeSchedule(params)}</p>
      )}

      {/* Advanced: raw cron */}
      <AdvancedToggle label="Advanced: edit cron expression directly">
        <div className={styles.formRow}>
          <input
            type="text"
            className={styles.cronInput}
            value={rawCron}
            onChange={e => { setUseRaw(true); handleRawChange(e.target.value) }}
            onFocus={() => setUseRaw(true)}
            placeholder="0 */4 * * *"
            spellCheck={false}
          />
        </div>
        {useRaw && (
          <p className={`${styles.cronPreview} ${validateRaw().ok ? styles.cronOk : styles.cronErr}`}>
            {validateRaw().ok
              ? `Cron expression looks valid.`
              : validateRaw().msg}
          </p>
        )}
      </AdvancedToggle>

      {saveError && <p className={styles.saveError}>{saveError}</p>}

      <div className={styles.editorButtons}>
        <button className={styles.saveBtn} onClick={handleSave} disabled={saving || !canSave}>
          {saving ? 'Saving…' : 'Save'}
        </button>
        <button className={styles.cancelBtn} onClick={onCancel}>
          Cancel
        </button>
      </div>
    </div>
  )
}

// ── Main page ────────────────────────────────────────────────────────────────

export default function BackgroundTasksPage() {
  const [tasks, setTasks]     = useState<BackgroundTask[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError]     = useState<string | null>(null)
  const [editingId, setEditingId]   = useState<string | null>(null)
  const [runningIds, setRunningIds] = useState<Set<string>>(new Set())

  const load = useCallback(async () => {
    try {
      const data = await getBackgroundTasks()
      setTasks(data)
      setError(null)
    } catch {
      setError('Could not reach the Chronicle API. Check that the service is running.')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => { load() }, [load])

  // Poll while any task is running
  useEffect(() => {
    const anyRunning = tasks.some(t => t.isRunning || runningIds.has(t.taskId))
    if (!anyRunning) return
    const id = setInterval(load, 3000)
    return () => clearInterval(id)
  }, [tasks, runningIds, load])

  async function handleRunNow(taskId: string) {
    setRunningIds(prev => new Set(prev).add(taskId))
    try {
      await runBackgroundTask(taskId)
      await load()
    } catch (err) {
      if (err instanceof ApiError) {
        // 409 = already running — just refresh state
        if (err.statusCode === 409) await load()
        else alert(err.message)
      }
    } finally {
      setRunningIds(prev => { const s = new Set(prev); s.delete(taskId); return s })
    }
  }

  async function handleSave(taskId: string, cron: string, isEnabled: boolean) {
    await updateBackgroundTask(taskId, { cronExpression: cron, isEnabled })
    setEditingId(null)
    await load()
  }

  if (loading) return <div className={styles.page}><p className={styles.loading}>Loading background tasks…</p></div>
  if (error)   return <div className={styles.page}><p className={styles.errorMsg}>{error}</p></div>

  return (
    <div className={styles.page}>
      <h1 className={styles.title}>Background Tasks</h1>

      {tasks.map(task => {
        const { cls, label } = statusBadge(task)
        const isRunning = task.isRunning || runningIds.has(task.taskId)

        return (
          <div key={task.taskId} className={styles.card}>
            <div className={styles.cardHeader}>
              <div className={styles.cardTitleGroup}>
                <h2 className={styles.taskName}>{task.displayName}</h2>
                <p className={styles.taskDesc}>{task.description}</p>
              </div>
              <div className={styles.cardActions}>
                <span className={`${styles.badge} ${cls}`}>{label}</span>
                <button
                  role="switch"
                  aria-checked={task.isEnabled}
                  className={`${styles.toggle} ${task.isEnabled ? styles.toggleOn : ''}`}
                  onClick={() =>
                    updateBackgroundTask(task.taskId, { isEnabled: !task.isEnabled }).then(load)
                  }
                  title={task.isEnabled ? 'Disable task' : 'Enable task'}
                >
                  <span className={styles.toggleThumb} />
                </button>
                <button
                  className={styles.runBtn}
                  onClick={() => handleRunNow(task.taskId)}
                  disabled={isRunning}
                >
                  {isRunning ? 'Running…' : 'Run Now'}
                </button>
                <button
                  className={styles.editBtn}
                  onClick={() => setEditingId(editingId === task.taskId ? null : task.taskId)}
                >
                  {editingId === task.taskId ? 'Close' : 'Schedule'}
                </button>
              </div>
            </div>

            {/* Error message from last run */}
            {task.lastRunSucceeded === false && task.lastErrorMessage && (
              <p className={styles.errorText}>{task.lastErrorMessage}</p>
            )}

            <div className={styles.metaGrid}>
              <div className={styles.metaRow}>
                <span className={styles.metaLabel}>Last Run</span>
                <span
                  className={styles.metaValue}
                  title={task.lastRunAt ? fmtLocal(task.lastRunAt) : undefined}
                >
                  {fmtRelative(task.lastRunAt)}
                </span>
              </div>
              <div className={styles.metaRow}>
                <span className={styles.metaLabel}>Next Run</span>
                <span
                  className={styles.metaValue}
                  title={task.nextRunAt ? fmtLocal(task.nextRunAt) : undefined}
                >
                  {task.isEnabled ? fmtRelative(task.nextRunAt) : 'Disabled'}
                </span>
              </div>
              <div className={styles.metaRow}>
                <span className={styles.metaLabel}>Schedule</span>
                <span className={styles.metaValue} style={{ fontFamily: 'monospace', fontSize: '0.82rem' }}>
                  {task.cronExpression}
                </span>
              </div>
            </div>

            {editingId === task.taskId && (
              <ScheduleEditor
                taskId={task.taskId}
                initialCron={task.cronExpression}
                isEnabled={task.isEnabled}
                onSave={handleSave}
                onCancel={() => setEditingId(null)}
              />
            )}
          </div>
        )
      })}
    </div>
  )
}
```

**Step 3: Commit**

```bash
git add src/Chronicle.Web/src/pages/settings/BackgroundTasksPage.tsx \
        src/Chronicle.Web/src/pages/settings/BackgroundTasksPage.module.css
git commit -m "feat(web): add BackgroundTasksPage with visual schedule editor and run-now button"
```

---

## Task 15: Wire Route and Nav Link

**Files:**
- Modify: `src/Chronicle.Web/src/App.tsx`
- Modify: `src/Chronicle.Web/src/components/layout/Layout.tsx`

**Step 1: Add import and route to `App.tsx`**

Add import after line 16 (`import LibrarySettingsPage ...`):
```tsx
import BackgroundTasksPage from '@/pages/settings/BackgroundTasksPage'
```

Add route after line 58 (`<Route path="settings/service" ...>`):
```tsx
<Route path="settings/background-tasks" element={<BackgroundTasksPage />} />
```

**Step 2: Add nav link in `Layout.tsx`**

In the Settings `NavGroup` block, add before `<NavLink to="/settings/library"` (alphabetically first):

```tsx
<NavLink to="/settings/background-tasks" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
  Background Tasks
</NavLink>
```

**Step 3: Build the frontend**

```bash
cd src/Chronicle.Web && npm run type-check
```

Expected: No type errors.

```bash
npm run lint
```

Expected: No lint errors.

**Step 4: Commit**

```bash
git add src/Chronicle.Web/src/App.tsx src/Chronicle.Web/src/components/layout/Layout.tsx
git commit -m "feat(web): add Background Tasks route and Settings nav link"
```

---

## Task 16: Run Full Test Suite and Final Verification

**Step 1: Run all backend tests**

```bash
cd tests && dotnet test --verbosity normal
```

Expected: All tests pass. Record the count — should be ≥ 199 + the new tests added in this feature.

**Step 2: Type-check and lint the frontend**

```bash
cd src/Chronicle.Web && npm run type-check && npm run lint
```

Expected: No errors.

**Step 3: Build the backend in Release mode**

```bash
cd src/Chronicle.API && dotnet build -c Release
```

Expected: Build succeeded, 0 errors, 0 warnings (or only pre-existing warnings).

**Step 4: Final commit**

If all clean:

```bash
git add -A
git commit -m "chore: final cleanup for background-tasks feature"
```

---

## Summary of New Files

| File | Purpose |
|---|---|
| `src/Chronicle.Core/Models/BackgroundTask.cs` | EF entity |
| `src/Chronicle.Services/IScheduledTask.cs` | Task interface |
| `src/Chronicle.Services/ITaskSchedulerService.cs` | Scheduler service interface + TriggerResult |
| `src/Chronicle.Services/TaskSchedulerService.cs` | Central scheduler BackgroundService |
| `src/Chronicle.API/Controllers/BackgroundTasksController.cs` | REST API |
| `src/Chronicle.Web/src/api/backgroundTasks.ts` | Frontend API client |
| `src/Chronicle.Web/src/utils/cronBuilder.ts` | Cron ↔ visual builder utilities |
| `src/Chronicle.Web/src/pages/settings/BackgroundTasksPage.tsx` | React page |
| `src/Chronicle.Web/src/pages/settings/BackgroundTasksPage.module.css` | Page styles |
| `src/Chronicle.Data/Migrations/{ts}_AddBackgroundTasksTable.cs` | Migration (auto-generated) |
| `tests/Chronicle.Tests.Unit/Services/TaskSchedulerServiceTests.cs` | Unit tests |
| `tests/Chronicle.Tests.Integration/BackgroundTasksTests.cs` | Integration tests |
