# Enrich-Pending as Proper Scheduled Task

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace the ad-hoc `EnrichmentCard` UI with a real `IScheduledTask` per metadata plugin so "Fetch Missing Metadata" appears in BackgroundTasksPage with toggle, schedule editor, last run, and next run — identical to every other `TaskCard`.

**Architecture:** A new `EnrichPendingScheduledTask` class wraps `IMetadataEnrichmentService.EnrichPendingAsync` for a single plugin ID. `PluginHostService` (which already seeds per-plugin DB rows for plugin-manifest tasks) registers one of these system tasks per loaded metadata plugin immediately after loading. `TaskSchedulerService` already handles seeding, scheduling, and running any `IScheduledTask` — no changes needed there. The frontend `EnrichmentCard` component and its ad-hoc `runEnrichment` call are removed; the task appears automatically as a `TaskCard` inside the correct `PluginTaskGroup` because it carries the plugin's `pluginId` in its `BackgroundTask` row (task ID format: `enrich_pending:{pluginId}`).

**Tech Stack:** C# / ASP.NET Core 9, EF Core 9 (SQLite), React 18 + TypeScript, TanStack Query v5

---

## Background — how the scheduler works today

- `IScheduledTask` implementations are registered in DI as singletons in `Program.cs`.
- `TaskSchedulerService` collects all `IEnumerable<IScheduledTask>`, calls `SeedTasksAsync` on startup to write missing rows to `background_tasks`, then ticks every 30 s and fires due tasks.
- **System tasks** (`PluginId IS NULL` in the DB row) are dispatched by finding the matching `IScheduledTask` by `TaskId`.
- **Plugin tasks** (`PluginId IS NOT NULL`) are dispatched through `IPluginTaskRunner.RunAsync(pluginId, bareTaskId)`.
- `EnrichPendingScheduledTask` will be a **system task** (no plugin DLL involved) but it will carry a `pluginId` property used only for seeding the DB row so the frontend groups it under the right plugin.

## Key files

| Path | Role |
|---|---|
| `src/Chronicle.Services/IScheduledTask.cs` | Interface to implement |
| `src/Chronicle.Services/EnrichPendingScheduledTask.cs` | **New file** |
| `src/Chronicle.Services/Plugins/PluginHostService.cs` | Register tasks after plugin load |
| `src/Chronicle.Services/TaskSchedulerService.cs` | Needs `RegisterTask` method for dynamic registration |
| `src/Chronicle.API/Program.cs` | Wire up new registration hook |
| `src/Chronicle.Web/src/pages/settings/BackgroundTasksPage.tsx` | Remove `EnrichmentCard`, remove ad-hoc enrichment run |
| `src/Chronicle.Web/src/api/enrichment.ts` | Remove `runEnrichment` export (or keep for backward compat) |
| `tests/Chronicle.Tests.Unit/` | Unit tests |

---

## Task 1: Create `EnrichPendingScheduledTask`

**Files:**
- Create: `src/Chronicle.Services/EnrichPendingScheduledTask.cs`

**Step 1: Write a failing unit test**

In `tests/Chronicle.Tests.Unit/Services/EnrichPendingScheduledTaskTests.cs`:

```csharp
using Chronicle.Services;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

public class EnrichPendingScheduledTaskTests
{
    private const string PluginId = "chronicle.plugin.trakt";

    [Fact]
    public void TaskId_HasExpectedFormat()
    {
        var svc = new Mock<IMetadataEnrichmentService>();
        var task = new EnrichPendingScheduledTask(PluginId, "Trakt", svc.Object);
        Assert.Equal($"enrich_pending:{PluginId}", task.TaskId);
    }

    [Fact]
    public void DisplayName_IncludesPluginName()
    {
        var svc = new Mock<IMetadataEnrichmentService>();
        var task = new EnrichPendingScheduledTask(PluginId, "Trakt", svc.Object);
        Assert.Contains("Trakt", task.DisplayName);
    }

    [Fact]
    public async Task ExecuteAsync_CallsEnrichPendingForPlugin()
    {
        var svc = new Mock<IMetadataEnrichmentService>();
        var task = new EnrichPendingScheduledTask(PluginId, "Trakt", svc.Object);

        await task.ExecuteAsync(CancellationToken.None);

        svc.Verify(s => s.EnrichPendingAsync(PluginId, It.IsAny<CancellationToken>()), Times.Once);
    }
}
```

**Step 2: Run test — expect compile failure**

```powershell
cd tests/Chronicle.Tests.Unit
dotnet test --filter "EnrichPendingScheduledTaskTests" --verbosity normal
```

Expected: build error — `EnrichPendingScheduledTask` doesn't exist yet.

**Step 3: Implement the class**

```csharp
// src/Chronicle.Services/EnrichPendingScheduledTask.cs
namespace Chronicle.Services;

/// <summary>
/// System-level IScheduledTask that runs EnrichPendingAsync for a single metadata plugin.
/// One instance is registered per loaded metadata provider plugin.
/// TaskId format: "enrich_pending:{pluginId}" — e.g. "enrich_pending:chronicle.plugin.trakt"
/// </summary>
public sealed class EnrichPendingScheduledTask : IScheduledTask
{
    private readonly string _pluginId;
    private readonly IMetadataEnrichmentService _enrichment;

    public EnrichPendingScheduledTask(
        string pluginId,
        string pluginDisplayName,
        IMetadataEnrichmentService enrichment)
    {
        _pluginId   = pluginId;
        _enrichment = enrichment;
        TaskId      = $"enrich_pending:{pluginId}";
        DisplayName = $"Fetch Missing Metadata";
        Description = $"Fetches {pluginDisplayName} metadata for library items that haven't been enriched yet.";
    }

    public string TaskId      { get; }
    public string DisplayName { get; }
    public string Description { get; }

    /// <summary>Default: daily at 4 am. Offset slightly from 3 am duplicate-cleanup run.</summary>
    public string DefaultCron => "0 4 * * *";

    public Task ExecuteAsync(CancellationToken ct)
        => _enrichment.EnrichPendingAsync(_pluginId, ct);
}
```

**Step 4: Run tests — expect green**

```powershell
dotnet test --filter "EnrichPendingScheduledTaskTests" --verbosity normal
```

Expected: 3 tests pass.

**Step 5: Commit**

```powershell
git add src/Chronicle.Services/EnrichPendingScheduledTask.cs
git add tests/Chronicle.Tests.Unit/Services/EnrichPendingScheduledTaskTests.cs
git commit -m "feat(enrichment): add EnrichPendingScheduledTask wrapping EnrichPendingAsync per plugin"
```

---

## Task 2: Add dynamic task registration to `TaskSchedulerService`

The scheduler currently only knows about tasks registered in DI at startup. We need a way for `PluginHostService` (which loads plugins after startup) to register new tasks at runtime.

**Files:**
- Modify: `src/Chronicle.Services/ITaskSchedulerService.cs`
- Modify: `src/Chronicle.Services/TaskSchedulerService.cs`

**Step 1: Check what `ITaskSchedulerService` currently exposes**

```powershell
cat src/Chronicle.Services/ITaskSchedulerService.cs
```

**Step 2: Add `RegisterTask` to the interface**

```csharp
// Add to ITaskSchedulerService:
/// <summary>
/// Dynamically registers a task that was not available at DI-registration time
/// (e.g. a per-plugin enrichment task created when a plugin loads).
/// Seeds the DB row if it doesn't already exist.
/// Safe to call after the scheduler has started.
/// </summary>
Task RegisterTaskAsync(IScheduledTask task, CancellationToken ct = default);
```

**Step 3: Implement `RegisterTaskAsync` in `TaskSchedulerService`**

`_tasks` is currently `IReadOnlyList<IScheduledTask>`. Change it to a `ConcurrentDictionary<string, IScheduledTask>` or a thread-safe list. The simplest change: make `_tasks` a `List` wrapped with a lock (matches existing pattern):

```csharp
// Change field declaration from:
private readonly IReadOnlyList<IScheduledTask> _tasks;

// To:
private readonly List<IScheduledTask> _tasks;
private readonly object _tasksLock = new();
```

Update constructor:
```csharp
_tasks = tasks.ToList();
```

All existing `_tasks.FirstOrDefault(...)` calls stay unchanged.

Add the new method:
```csharp
public async Task RegisterTaskAsync(IScheduledTask task, CancellationToken ct = default)
{
    lock (_tasksLock)
    {
        if (_tasks.Any(t => t.TaskId == task.TaskId)) return; // already registered
        _tasks.Add(task);
    }
    // Seed DB row if missing
    using var scope = _scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
    var existing = await db.BackgroundTasks
        .FirstOrDefaultAsync(t => t.TaskId == task.TaskId, ct);
    if (existing is null)
    {
        db.BackgroundTasks.Add(new BackgroundTask
        {
            TaskId         = task.TaskId,
            DisplayName    = task.DisplayName,
            Description    = task.Description,
            CronExpression = task.DefaultCron,
            IsEnabled      = true,
            NextRunAt      = GetNextOccurrence(task.DefaultCron),
            PluginId       = null,   // system task — routed via IScheduledTask, not IPluginTaskRunner
        });
        await db.SaveChangesAsync(ct);
        _log.Information("TaskScheduler: dynamically registered task '{TaskId}'", task.TaskId);
    }
}
```

**Step 4: Write a unit test**

```csharp
[Fact]
public async Task RegisterTaskAsync_SeedsDbRowAndAddsTasks()
{
    // Uses the existing TaskSchedulerServiceTests infrastructure (in-memory DB).
    // Verify: after RegisterTaskAsync, SeedTasksAsync does NOT create a duplicate row,
    // and TickAsync can find and fire the task.
    // (Sketch — adapt to match existing test helper pattern in the project)
}
```

**Step 5: Run all unit tests**

```powershell
cd tests/Chronicle.Tests.Unit
dotnet test --verbosity normal
```

Expected: all passing, no regressions.

**Step 6: Commit**

```powershell
git add src/Chronicle.Services/ITaskSchedulerService.cs src/Chronicle.Services/TaskSchedulerService.cs
git commit -m "feat(scheduler): add RegisterTaskAsync for dynamic post-startup task registration"
```

---

## Task 3: Register `EnrichPendingScheduledTask` from `PluginHostService`

When a plugin is loaded, `PluginHostService` should create an `EnrichPendingScheduledTask` for each `IMetadataProvider` the plugin exposes and register it with the scheduler.

**Files:**
- Modify: `src/Chronicle.Services/Plugins/PluginHostService.cs`

**Step 1: Inject `ITaskSchedulerService` and `IMetadataEnrichmentService`**

In `PluginHostService` constructor, add:
```csharp
private readonly ITaskSchedulerService _scheduler;
private readonly IMetadataEnrichmentService _enrichment;
```

These are already registered in DI — just add them as constructor parameters.

**Step 2: After a plugin loads successfully, register enrichment tasks**

Find the place in `PluginHostService` where a plugin finishes loading (after `_registry` is updated). Add:

```csharp
// Register one EnrichPendingScheduledTask per metadata provider in this plugin.
foreach (var (pluginId, provider, _) in _registry.GetMetadataProviderEntries()
    .Where(e => e.PluginId == manifest.PluginId))
{
    var task = new EnrichPendingScheduledTask(
        pluginId:          pluginId,
        pluginDisplayName: manifest.DisplayName ?? provider.Name,
        enrichment:        _enrichment);
    await _scheduler.RegisterTaskAsync(task);
}
```

**Step 3: Verify the task shows up in the DB**

Start the API, check `Settings → Background Tasks`. The Trakt group should now show a "Fetch Missing Metadata" TaskCard alongside its other tasks.

**Step 4: Run integration tests**

```powershell
cd tests/Chronicle.Tests.Integration
dotnet test --verbosity normal
```

Expected: all passing.

**Step 5: Commit**

```powershell
git add src/Chronicle.Services/Plugins/PluginHostService.cs
git commit -m "feat(plugins): register EnrichPendingScheduledTask for each metadata provider on plugin load"
```

---

## Task 4: Wire the DB row to the correct plugin group in the frontend

The `BackgroundTask` DB row for `enrich_pending:{pluginId}` currently has `PluginId = null` (system task). The frontend groups tasks by `task.pluginId`. To make it appear under the correct plugin group, the DB row needs `PluginId` set.

**Option:** Set `PluginId` on the DB row in `RegisterTaskAsync` so the frontend groups it correctly, but keep the execution path using `IScheduledTask` (not `IPluginTaskRunner`). The scheduler already has special handling: if `PluginId IS NOT NULL`, it routes through `IPluginTaskRunner`. We need to keep routing through `IScheduledTask`.

**Correct approach:** Add a nullable `OwnerPluginId` column to `BackgroundTask` separate from the routing `PluginId`, OR store `PluginId` for grouping and handle the dispatch properly.

**Simplest correct approach:** Store the `pluginId` in the `PluginId` column (for frontend grouping), but in `RunTaskAsync` check if a matching `IScheduledTask` exists FIRST regardless of whether `PluginId` is set — if found, use it. Only fall through to `IPluginTaskRunner` when no `IScheduledTask` match exists.

**Step 1: Update `RunTaskAsync` routing logic in `TaskSchedulerService`**

```csharp
private async Task RunTaskAsync(BackgroundTask row, CancellationToken ct)
{
    _log.Information("TaskScheduler: starting '{TaskId}'", row.TaskId);
    var startedAt = DateTime.UtcNow;
    try
    {
        // Always check for a registered IScheduledTask first.
        // This handles enrich_pending tasks which have PluginId set for grouping
        // but are implemented as system IScheduledTask instances, not plugin tasks.
        IScheduledTask? systemTask;
        lock (_tasksLock) { systemTask = _tasks.FirstOrDefault(t => t.TaskId == row.TaskId); }

        if (systemTask is not null)
        {
            await systemTask.ExecuteAsync(ct);
        }
        else if (row.PluginId is not null)
        {
            using var scope = _scopeFactory.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<IPluginTaskRunner>();
            var bareTaskId = row.TaskId.Contains(':')
                ? row.TaskId[(row.TaskId.IndexOf(':') + 1)..]
                : row.TaskId;
            await runner.RunAsync(row.PluginId, bareTaskId, ct);
        }
        else
        {
            _log.Warning("TaskSchedulerService: no handler found for '{TaskId}'", row.TaskId);
            return;
        }
        // ... rest of success handling unchanged
    }
    // ... catch unchanged
}
```

**Step 2: Set `PluginId` in `RegisterTaskAsync` for enrichment tasks**

Pass `pluginId` into `RegisterTaskAsync` so the DB row is grouped correctly:

```csharp
// In RegisterTaskAsync, change the Add call:
db.BackgroundTasks.Add(new BackgroundTask
{
    TaskId         = task.TaskId,
    DisplayName    = task.DisplayName,
    Description    = task.Description,
    CronExpression = task.DefaultCron,
    IsEnabled      = true,
    NextRunAt      = GetNextOccurrence(task.DefaultCron),
    PluginId       = ownerPluginId,  // new optional param — null for pure system tasks
});
```

Update `ITaskSchedulerService`:
```csharp
Task RegisterTaskAsync(IScheduledTask task, string? ownerPluginId = null, CancellationToken ct = default);
```

Update the call in `PluginHostService`:
```csharp
await _scheduler.RegisterTaskAsync(task, ownerPluginId: pluginId);
```

**Step 3: Run all tests**

```powershell
cd tests && dotnet test --verbosity normal
```

**Step 4: Commit**

```powershell
git add src/Chronicle.Services/TaskSchedulerService.cs src/Chronicle.Services/ITaskSchedulerService.cs
git commit -m "feat(scheduler): route IScheduledTask before IPluginTaskRunner; pass ownerPluginId for grouping"
```

---

## Task 5: Remove `EnrichmentCard` from the frontend

Now that the task appears as a real `TaskCard`, the ad-hoc `EnrichmentCard`, `handleRunEnrichment`, and enrichment-stats-only fetch can be removed.

**Files:**
- Modify: `src/Chronicle.Web/src/pages/settings/BackgroundTasksPage.tsx`
- Optionally keep: `src/Chronicle.Web/src/api/enrichment.ts` (used by EnrichmentDrillDown page — do NOT remove)

**Step 1: Remove the `EnrichmentCard` component** (lines ~665–720)

Delete the entire `function EnrichmentCard(...)` component.

**Step 2: Remove the `enrichStat` / `EnrichmentCard` usage in the render** (lines ~903–934)

Remove:
```tsx
const hasOwnFetchTask = groupTasks.some(t => t.taskId.endsWith(':fetch-missing-metadata'))
const enrichStat = pluginId !== null && !hasOwnFetchTask
  ? enrichmentStats.find(s => s.pluginId === pluginId)
  : undefined
```
And remove the `{enrichStat && <EnrichmentCard ... />}` JSX block.

**Step 3: Remove the enrichment-stats state and effect** (lines ~731, ~760)

Remove:
```tsx
const [enrichmentStats, setEnrichmentStats] = useState<EnrichmentStats[]>([])
// ...
useEffect(() => { getEnrichmentStats().then(setEnrichmentStats).catch(() => {}) }, [])
```

**Step 4: Remove `handleRunEnrichment`** (lines ~788–796) and its `runningEnrichmentIds` state.

**Step 5: Remove `runEnrichment` import** from `enrichment.ts` imports at top of file (keep `getEnrichmentStats` — it's used by the EnrichmentDrillDown page via a different import path).

**Step 6: Check for TypeScript errors**

```powershell
cd src/Chronicle.Web
npm run type-check
```

Expected: no errors.

**Step 7: Run lint**

```powershell
npm run lint
```

**Step 8: Commit**

```powershell
git add src/Chronicle.Web/src/pages/settings/BackgroundTasksPage.tsx
git commit -m "feat(ui): remove EnrichmentCard — enrichment now a proper TaskCard via IScheduledTask"
```

---

## Task 6: Handle existing DB rows (migration / cleanup)

Existing `background_tasks` rows for `enrich_pending:*` don't exist yet (first run will seed them). However, if any `enrich_pending:*` rows already exist from a previous attempt, the `RegisterTaskAsync` guard (`if (existing is not null) continue`) will skip re-seeding — which is correct.

**No DB migration needed** — `RegisterTaskAsync` adds rows to the existing `background_tasks` table, which already has the right schema.

**Step 1: Verify on a clean run**

Start the API. Open `Settings → Background Tasks`. Confirm:
- Each metadata plugin (TMDB, Trakt, SIMKL, MusicBrainz, Fanart.tv, Hardcover, FanEdit) shows a "Fetch Missing Metadata" `TaskCard` in its group
- Toggle, Schedule editor, Last Run, Next Run all work
- "Run Now" triggers `EnrichPendingAsync` for that plugin

**Step 2: Commit final cleanup if any**

```powershell
git add -A
git commit -m "chore: verify enrich-pending scheduled tasks seed correctly on startup"
```

---

## Task 7: Final verification

**Step 1: Run full test suite**

```powershell
cd tests && dotnet test --verbosity normal
```

Expected: all 348+ tests pass.

**Step 2: Manual smoke test**

1. Start API: `cd src/Chronicle.API && dotnet run`
2. Start frontend: `cd src/Chronicle.Web && npm run dev`
3. Navigate to Settings → Background Tasks
4. Confirm each plugin group shows "Fetch Missing Metadata" as a proper TaskCard
5. Toggle one off — confirm it can be toggled back on
6. Click Schedule — confirm cron editor opens and saves
7. Click Run Now on Trakt's enrichment task — confirm it starts and Last Run updates

**Step 3: Commit if any final fixes needed, then push**

```powershell
git push origin develop
```
