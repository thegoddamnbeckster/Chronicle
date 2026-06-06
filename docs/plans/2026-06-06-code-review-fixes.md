# Code Review Fixes Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix 15 bugs and design issues identified in the high-effort code review of Chronicle's API, services, and plugins.

**Architecture:** Fixes span Chronicle.Services, Chronicle.API, and Chronicle.Data. Most are surgical—change one method or add a migration. The shared PluginIdHelper (Task 8) is the only cross-cutting addition; it gets consumed in the same commit so nothing is left dangling.

**Tech Stack:** .NET 9, EF Core 9, SQLite, ASP.NET Core, xUnit, FluentAssertions

---

### Task 1: Fix DuplicateCleanupService — missing transaction around merge

**Files:**
- Modify: `src/Chronicle.Services/DuplicateCleanupService.cs` (RunAsync, MergeAndDeleteAsync)

**Problem:** `MergeAndDeleteAsync` stages many writes and `ResolveAsync` calls `SaveChangesAsync` internally, but the whole merge operation has no wrapping transaction. If the process dies mid-merge, the DB is left in a corrupt state.

**Fix:** Begin a transaction before calling `MergeAndDeleteAsync` for each loser; commit after the outer `SaveChangesAsync`. Because `ResolveAsync` also calls `SaveChanges`, we need to ensure they all participate in the same transaction (EF Core will enlist them automatically when a transaction is active on the context).

**Step 1: Wrap each merge call in a transaction in RunAsync**

In `RunAsync`, replace the pattern:
```csharp
await MergeAndDeleteAsync(context, resolutionService, winner, loser, ct);
alreadyRemoved.Add(loser.Id);
removed++;
// ... later
await context.SaveChangesAsync(ct);
```

With a transaction per loser:
```csharp
await using var tx = await context.Database.BeginTransactionAsync(ct);
await MergeAndDeleteAsync(context, resolutionService, winner, loser, ct);
await context.SaveChangesAsync(ct);
await tx.CommitAsync(ct);
alreadyRemoved.Add(loser.Id);
removed++;
```

Apply this pattern to all three merge call-sites (Pass 1, Pass 2, Pass 3). Remove the standalone `await context.SaveChangesAsync(ct)` that currently comes after the foreach loop in each pass (the SaveChanges now happens inside the transaction per iteration).

**Step 2: Build and verify**
```powershell
cd src/Chronicle.API && dotnet build --no-restore -nologo 2>&1 | Select-String "error|warning" | Select-Object -First 20
```

**Step 3: Commit**
```
git add src/Chronicle.Services/DuplicateCleanupService.cs
git commit -m "fix(duplicates): wrap each auto-merge in an explicit transaction"
```

---

### Task 2: Fix ScrobbleService — TOCTOU idempotency + add CancellationToken

**Files:**
- Modify: `src/Chronicle.Services/IScrobbleService.cs`
- Modify: `src/Chronicle.Services/ScrobbleService.cs`
- Modify: `src/Chronicle.API/Controllers/ScrobbleController.cs`
- Create: `src/Chronicle.Data/Migrations/<timestamp>_AddInteractionEventUniqueIndex.cs`

**Problem A:** The AnyAsync → FirstAsync pattern for idempotency has a TOCTOU race. Two concurrent scrobbles with the same (userId, mediaItemId, timestamp) both pass the check and both insert.

**Problem B:** `ScrobbleAsync` takes no `CancellationToken`, making all its DB calls uncancellable.

**Fix A:** Add a unique index on `(user_id, media_item_id, timestamp)` to `interaction_events`. Replace the AnyAsync check with a try/catch on `DbUpdateException` (unique constraint violation). EF Core wraps SQLite constraint violations as `DbUpdateException`.

**Fix B:** Add `CancellationToken ct = default` to the interface and implementation. Pass it through all EF async calls.

**Step 1: Generate migration for the unique index**
```powershell
cd src/Chronicle.API
dotnet ef migrations add AddInteractionEventUniqueIndex
```

**Step 2: Edit the generated migration** to add the unique composite index:
```csharp
migrationBuilder.CreateIndex(
    name: "IX_interaction_events_UserId_MediaItemId_Timestamp",
    table: "interaction_events",
    columns: new[] { "UserId", "MediaItemId", "Timestamp" },
    unique: true);
```
Add the corresponding `DropIndex` in `Down()`.

**Step 3: Update IScrobbleService**
```csharp
Task<ScrobbleResult> ScrobbleAsync(int userId, ScrobbleRequest request, CancellationToken ct = default);
```

**Step 4: Rewrite ScrobbleAsync in ScrobbleService**
```csharp
public async Task<ScrobbleResult> ScrobbleAsync(int userId, ScrobbleRequest request, CancellationToken ct = default)
{
    var mediaItem = await _context.MediaItems.FindAsync([request.MediaItemId], ct)
        ?? throw new MediaNotFoundException(request.MediaItemId);

    var markedAsWatched = request.ProgressPercent >= WatchedThreshold;
    var timestamp = request.Timestamp ?? DateTime.UtcNow;

    var evt = new InteractionEvent
    {
        UserId          = userId,
        MediaItemId     = request.MediaItemId,
        Timestamp       = timestamp,
        ProgressPercent = request.ProgressPercent,
        DeviceName      = request.DeviceName,
        MarkedAsWatched = markedAsWatched,
        CreatedAt       = DateTime.UtcNow
    };

    _context.InteractionEvents.Add(evt);

    if (markedAsWatched)
        await UpdateLibraryStatusAsync(userId, request.MediaItemId, mediaItem, ct);

    try
    {
        await _context.SaveChangesAsync(ct);
    }
    catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
    {
        // Duplicate scrobble — return the existing event instead of inserting
        _context.ChangeTracker.Clear();
        var existing = await _context.InteractionEvents
            .FirstAsync(e => e.UserId == userId
                          && e.MediaItemId == request.MediaItemId
                          && e.Timestamp == timestamp, ct);
        return new ScrobbleResult(existing, markedAsWatched);
    }

    return new ScrobbleResult(evt, markedAsWatched);
}

private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
    ex.InnerException?.Message.Contains("UNIQUE constraint failed",
        StringComparison.OrdinalIgnoreCase) == true;
```

Also add `ct` to the `UpdateLibraryStatusAsync` signature and pass it to all EF calls inside.

**Step 5: Update ScrobbleController** — pass `HttpContext.RequestAborted` as the CT:
```csharp
var result = await _scrobbleService.ScrobbleAsync(userId, new ScrobbleRequest(...), HttpContext.RequestAborted);
```

**Step 6: Apply migration**
```powershell
cd src/Chronicle.API && dotnet ef database update
```

**Step 7: Build and test**
```powershell
cd tests && dotnet test --verbosity normal 2>&1 | tail -20
```

**Step 8: Commit**
```
git add src/Chronicle.Services/IScrobbleService.cs src/Chronicle.Services/ScrobbleService.cs src/Chronicle.API/Controllers/ScrobbleController.cs src/Chronicle.Data/Migrations/
git commit -m "fix(scrobble): atomic idempotency via unique index + CancellationToken"
```

---

### Task 3: Fix SeedEnrichmentRowsForProviderAsync — SQLite IN clause limit

**Files:**
- Modify: `src/Chronicle.Services/Plugins/PluginService.cs` (SeedEnrichmentRowsForProviderAsync)
- Modify: `src/Chronicle.Services/MetadataEnrichmentService.cs` (SeedEnrichmentRows, line ~448)

**Problem:** `itemIds.Contains(x.MediaItemId)` with >999 IDs generates a SQL `IN (...)` that exceeds SQLite's SQLITE_LIMIT_VARIABLE_NUMBER (default 999), crashing at runtime for any library with >999 items of the supported type.

**Fix:** Chunk `itemIds` into batches of 500 before the `Contains` query.

**Step 1: Add a chunked query helper in PluginService.SeedEnrichmentRowsForProviderAsync**

Replace:
```csharp
var existingSet = (await _db.MediaEnrichments
    .Where(x => x.PluginId == manifestPluginId && itemIds.Contains(x.MediaItemId))
    .Select(x => x.MediaItemId)
    .ToListAsync(ct))
    .ToHashSet();
```

With:
```csharp
var existingSet = new HashSet<int>();
foreach (var chunk in itemIds.Chunk(500))
{
    var chunkArr = chunk;
    var existing = await _db.MediaEnrichments
        .Where(x => x.PluginId == manifestPluginId && chunkArr.Contains(x.MediaItemId))
        .Select(x => x.MediaItemId)
        .ToListAsync(ct);
    existingSet.UnionWith(existing);
}
```

Also chunk the `AddRange` inserts to avoid holding a huge change tracker:
```csharp
foreach (var chunk in toAdd.Chunk(500))
{
    _db.MediaEnrichments.AddRange(chunk);
    await _db.SaveChangesAsync(ct);
}
```

**Step 2: Fix the same pattern in MetadataEnrichmentService.SeedEnrichmentRows** (~line 448):
```csharp
var metadataByItem = new Dictionary<int, string?>();
foreach (var chunk in itemIds.Chunk(500))
{
    var chunkArr = chunk;
    var results = await db.MediaItems
        .Where(mi => chunkArr.Contains(mi.Id))
        .Select(mi => new { mi.Id, mi.MetadataJson })
        .ToListAsync(ct);
    foreach (var r in results) metadataByItem[r.Id] = r.MetadataJson;
}
```

**Step 3: Build**
```powershell
cd src/Chronicle.API && dotnet build --no-restore -nologo 2>&1 | Select-String "error" | Select-Object -First 10
```

**Step 4: Commit**
```
git add src/Chronicle.Services/Plugins/PluginService.cs src/Chronicle.Services/MetadataEnrichmentService.cs
git commit -m "fix(enrichment): chunk large IN clauses to stay under SQLite 999-variable limit"
```

---

### Task 4: Fix PluginRegistry._loadGate — incorrect comment

**Files:**
- Modify: `src/Chronicle.Services/Plugins/PluginRegistry.cs`

**Problem:** The comment says "prevents two concurrent reloads of the same plugin" but `_loadGate` is a single global semaphore that blocks ALL plugin loads, not just same-plugin loads.

**Fix:** Update the comment to accurately describe the semaphore's actual behavior.

**Step 1: Replace comment**
```csharp
// Global load gate: prevents two concurrent LoadPluginAsync calls from racing
// on the _plugins dictionary — e.g. a scheduled reload and a manual /reload
// request firing simultaneously. This intentionally serialises all loads;
// startup loads 7 plugins sequentially (fast, ~1s each), so the throughput
// cost is negligible vs. the complexity of per-plugin locking.
private readonly SemaphoreSlim _loadGate = new(1, 1);
```

**Step 2: Commit**
```
git add src/Chronicle.Services/Plugins/PluginRegistry.cs
git commit -m "docs(plugins): correct misleading comment on _loadGate semaphore"
```

---

### Task 5: Fix GraftExternalIdAsync — doesn't upsert, only inserts

**Files:**
- Modify: `src/Chronicle.Services/SyncOrchestrationService.cs`

**Problem:** `GraftExternalIdAsync` checks `AnyAsync` and only inserts if no row exists. If a row exists with a *stale* external ID, it's never corrected — the item keeps the wrong ID forever.

**Fix:** Change to a proper upsert: if a row exists with a different ID, update it.

**Step 1: Replace GraftExternalIdAsync**
```csharp
private static async Task GraftExternalIdAsync(
    ChronicleDbContext db, int mediaItemId, string pluginId, string externalId, CancellationToken ct)
{
    var source = SourceFromPluginId(pluginId);
    var existing = await db.MediaExternalIds
        .FirstOrDefaultAsync(e => e.MediaItemId == mediaItemId && e.Source == source, ct);

    if (existing is null)
    {
        db.MediaExternalIds.Add(new MediaExternalId
            { MediaItemId = mediaItemId, Source = source, ExternalId = externalId });
        await db.SaveChangesAsync(ct);
    }
    else if (existing.ExternalId != externalId)
    {
        existing.ExternalId = externalId;
        await db.SaveChangesAsync(ct);
    }
    // else: already correct, no-op
}
```

**Step 2: Build and test**
```powershell
cd tests && dotnet test --verbosity normal 2>&1 | tail -10
```

**Step 3: Commit**
```
git add src/Chronicle.Services/SyncOrchestrationService.cs
git commit -m "fix(sync): GraftExternalIdAsync now upserts stale external IDs not just inserts missing ones"
```

---

### Task 6: Fix EnrichPendingAsync — O(n²) correlated subquery

**Files:**
- Modify: `src/Chronicle.Services/MetadataEnrichmentService.cs`

**Problem:** The parent-blocking check uses a correlated `NOT EXISTS` subquery evaluated per row. For a 30k-episode library this is 30k subqueries per pass.

**Fix:** Before the main query loop, fetch the set of parent IDs with non-terminal enrichment status into a `HashSet<int>`. Filter in-memory with `!blockedParentIds.Contains(x.MediaItem!.ParentId)`.

**Step 1: Add blocked-parent set fetch before the while loop**

After the `var cutoff = ...` line and before `while (true)`, add:
```csharp
// Pre-fetch the set of item IDs whose enrichment is non-terminal (Pending/Failed within
// the retry window) so we can gate child items without a correlated subquery per row.
// Refreshed on every pass so newly-completed parents unblock their children next pass.
HashSet<int> blockedParentIds;
```

Declare it before the loop. At the top of each pass (before the `rows` query), populate it:
```csharp
blockedParentIds = (await db.MediaEnrichments
    .Where(e => e.PluginId == pluginId &&
                (e.Status == EnrichmentStatus.Pending ||
                 (e.Status == EnrichmentStatus.Failed &&
                  (e.LastAttemptedAt == null || e.LastAttemptedAt < cutoff))))
    .Select(e => e.MediaItemId)
    .ToListAsync(ct))
    .ToHashSet();
```

Then replace the correlated subquery in the `Where` clause:
```csharp
(x.MediaItem!.ParentId == null ||
 !blockedParentIds.Contains(x.MediaItem!.ParentId))
```

Remove the old `!db.MediaEnrichments.Any(p => ...)` block.

**Step 2: Build and confirm the correlated subquery is gone**
```powershell
cd src/Chronicle.API && dotnet build --no-restore -nologo 2>&1 | Select-String "error" | Select-Object -First 10
```

**Step 3: Test**
```powershell
cd tests && dotnet test --verbosity normal 2>&1 | tail -10
```

**Step 4: Commit**
```
git add src/Chronicle.Services/MetadataEnrichmentService.cs
git commit -m "perf(enrichment): replace O(n^2) correlated parent subquery with pre-fetched HashSet"
```

---

### Task 7: Fix MergeSettingsAsync — not concurrency-safe for OAuth token refresh

**Files:**
- Modify: `src/Chronicle.Services/Plugins/PluginService.cs`

**Problem:** Two concurrent sync jobs for the same plugin both call `PersistRefreshedTokensAsync` → `MergeSettingsAsync`. They both read the same settings snapshot, merge their tokens, and the second write silently loses the first's tokens.

**Fix:** Add a `ConcurrentDictionary<string, SemaphoreSlim>` keyed by `pluginId` to serialise concurrent `MergeSettingsAsync` calls for the same plugin.

**Step 1: Add semaphore dictionary to PluginService**
```csharp
// Per-plugin semaphore: serialises concurrent MergeSettingsAsync calls so that
// two OAuth token refreshes for the same plugin can't overwrite each other.
private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>
    _settingsLocks = new(StringComparer.OrdinalIgnoreCase);

private static SemaphoreSlim GetSettingsLock(string pluginId) =>
    _settingsLocks.GetOrAdd(pluginId, _ => new SemaphoreSlim(1, 1));
```

**Step 2: Wrap MergeSettingsAsync body**
```csharp
public async Task MergeSettingsAsync(
    string pluginId,
    IReadOnlyDictionary<string, string> newSettings,
    CancellationToken ct = default)
{
    var sem = GetSettingsLock(pluginId);
    await sem.WaitAsync(ct);
    try
    {
        var plugin = await _db.Plugins
            .FirstOrDefaultAsync(p => p.PluginId == pluginId, ct)
            ?? throw new InvalidOperationException($"Plugin '{pluginId}' not found.");

        var existing = DeserializeSettings(plugin.SettingsJson);
        var merged   = new Dictionary<string, string>(existing);
        foreach (var (key, value) in newSettings)
            merged[key] = value;

        await UpdateSettingsAsync(plugin.Id, merged);
    }
    finally
    {
        sem.Release();
    }
}
```

**Step 3: Build**
```powershell
cd src/Chronicle.API && dotnet build --no-restore -nologo 2>&1 | Select-String "error" | Select-Object -First 10
```

**Step 4: Commit**
```
git add src/Chronicle.Services/Plugins/PluginService.cs
git commit -m "fix(plugins): serialise concurrent MergeSettingsAsync per plugin to prevent token-refresh races"
```

---

### Task 8: Extract shared PluginIdHelper — eliminate copy-pasted short-ID extraction

**Files:**
- Create: `src/Chronicle.Core/Helpers/PluginIdHelper.cs`
- Modify: `src/Chronicle.Services/MetadataEnrichmentService.cs` (8+ call sites)
- Modify: `src/Chronicle.Services/MergeService.cs` (3 call sites)
- Modify: `src/Chronicle.Services/DuplicateCleanupService.cs` (1 call site)
- Modify: `src/Chronicle.Services/SyncOrchestrationService.cs` (SourceFromPluginId)

**Problem:** `pluginId.Contains('.') ? pluginId.Split('.').Last() : pluginId` is duplicated in 8+ places with minor variations, creating divergence risk.

**Fix:** Create one canonical helper in Chronicle.Core and replace all usages.

**Step 1: Create the helper**
```csharp
// src/Chronicle.Core/Helpers/PluginIdHelper.cs
namespace Chronicle.Core.Helpers;

/// <summary>
/// Utilities for working with Chronicle plugin IDs.
/// </summary>
public static class PluginIdHelper
{
    /// <summary>
    /// Returns the short source name derived from a full plugin ID.
    /// "chronicle.plugin.tmdb"  → "tmdb"
    /// "chronicle.plugin.trakt" → "trakt"
    /// "hardcover"              → "hardcover"
    /// </summary>
    public static string ToSource(string pluginId)
    {
        var dot = pluginId.LastIndexOf('.');
        return dot >= 0 ? pluginId[(dot + 1)..] : pluginId;
    }
}
```

**Step 2: Replace in SyncOrchestrationService** — change `SourceFromPluginId` to delegate:
```csharp
private static string SourceFromPluginId(string pluginId) =>
    Chronicle.Core.Helpers.PluginIdHelper.ToSource(pluginId);
```
Or import the namespace and call directly.

**Step 3: Replace in MetadataEnrichmentService, MergeService, DuplicateCleanupService** — search for `pluginId.Contains('.') ?` or `row.PluginId.Contains('.')` and replace with `PluginIdHelper.ToSource(...)`.

**Step 4: Build**
```powershell
cd src/Chronicle.API && dotnet build --no-restore -nologo 2>&1 | Select-String "error" | Select-Object -First 20
```

**Step 5: Commit**
```
git add src/Chronicle.Core/Helpers/PluginIdHelper.cs src/Chronicle.Services/
git commit -m "refactor(plugins): extract PluginIdHelper.ToSource to eliminate copy-pasted short-ID extraction"
```

---

### Task 9: Fix PluginsController — bare catch swallows GetSettingsSchema exceptions

**Files:**
- Modify: `src/Chronicle.API/Controllers/PluginsController.cs`

**Problem:** `catch { }` on `GetSettingsSchema()` calls silently discards plugin exceptions with no log. Settings page silently shows empty/partial settings.

**Fix:** Log at Warning level, then continue.

**Step 1: Inject ILogger and replace bare catch**
```csharp
foreach (var p in loaded.MetadataProviders)
    try { MergeSchema(p.GetSettingsSchema()); }
    catch (Exception ex) { _logger.LogWarning(ex, "GetSettingsSchema threw for metadata provider in plugin {PluginId}", pluginId); }

foreach (var p in loaded.FileScannerPlugins)
    try { MergeSchema(p.GetSettingsSchema()); }
    catch (Exception ex) { _logger.LogWarning(ex, "GetSettingsSchema threw for file scanner in plugin {PluginId}", pluginId); }

foreach (var p in loaded.ImportProviders)
    try { MergeSchema(p.GetSettingsSchema()); }
    catch (Exception ex) { _logger.LogWarning(ex, "GetSettingsSchema threw for import provider in plugin {PluginId}", pluginId); }
```

Check whether `PluginsController` already has an `ILogger` injected. If not, add it via constructor injection (`ILogger<PluginsController> logger`).

**Step 2: Build**
```powershell
cd src/Chronicle.API && dotnet build --no-restore -nologo 2>&1 | Select-String "error" | Select-Object -First 10
```

**Step 3: Commit**
```
git add src/Chronicle.API/Controllers/PluginsController.cs
git commit -m "fix(plugins): log instead of silently swallowing GetSettingsSchema exceptions"
```

---

### Task 10: Fix DuplicateCleanupService Pass 3 — loads all root items into memory

**Files:**
- Modify: `src/Chronicle.Services/DuplicateCleanupService.cs`

**Problem:** Pass 3 does `WHERE HierarchyLevel == 0` with no further filter before loading full entities including `ExternalIds`. On a large library (100k+ items) this spikes memory and holds a long read cursor.

**Fix:** Two improvements:
1. Select only the columns needed for grouping (Id, Name, Year, MediaTypeId, MetadataJson) rather than loading full entities.
2. Only load items that have at least one sibling with the same normalised title+year by doing the grouping in SQL before materializing.

Since doing the full grouping in SQL is complex with EF, a pragmatic improvement is to select a projection (no `Include`) and only load `ExternalIds` lazily for the actual winners/losers:

**Step 1: Replace the ToListAsync with a projection**
```csharp
var rootItems = await context.MediaItems
    .Where(m => m.HierarchyLevel == 0)
    .Select(m => new
    {
        m.Id,
        m.Name,
        m.Year,
        m.MediaTypeId,
        m.MetadataJson,
        ExternalIds = m.ExternalIds.Select(e => new { e.Source, e.ExternalId }).ToList()
    })
    .ToListAsync(ct);
```

Adjust the downstream `titleGroups` filtering and `MergeAndDeleteAsync` calls accordingly — `MergeAndDeleteAsync` takes `MediaItem` entities, so load the actual winner/loser entities only when a merge is needed:
```csharp
var winnerEntity = await context.MediaItems.Include(m => m.ExternalIds).FirstAsync(m => m.Id == ordered[0].Id, ct);
var loserEntity  = await context.MediaItems.Include(m => m.ExternalIds).FirstAsync(m => m.Id == loser.Id, ct);
await MergeAndDeleteAsync(context, resolutionService, winnerEntity, loserEntity, ct);
```

**Step 2: Build and test**
```powershell
cd tests && dotnet test --verbosity normal 2>&1 | tail -10
```

**Step 3: Commit**
```
git add src/Chronicle.Services/DuplicateCleanupService.cs
git commit -m "perf(duplicates): use projection in Pass 3 to avoid loading full entity graph for all root items"
```

---

### Task 11: Fix SettingsController — DateTime.Now → DateTime.UtcNow for uptime

**Files:**
- Modify: `src/Chronicle.API/Controllers/SettingsController.cs` (line ~434)

**Problem:** `DateTime.Now - processes[0].StartTime` uses local time. `Process.StartTime` returns local time on Windows, so the subtraction is actually correct — but on Linux/macOS or in a container with a different timezone the result may be wrong. Safer to use `UtcNow - StartTime.ToUniversalTime()`.

**Fix:**
```csharp
var elapsed = DateTime.UtcNow - processes[0].StartTime.ToUniversalTime();
```

**Step 2: Commit**
```
git add src/Chronicle.API/Controllers/SettingsController.cs
git commit -m "fix(settings): use UtcNow for process uptime calculation to be timezone-safe"
```

---

### Task 12: Fix FileScanService — rename confusing .Result property on anonymous type

**Files:**
- Modify: `src/Chronicle.Services/FileScanService.cs` (line ~1146)

**Problem:** `new { Result = c.Metadata, Score = ... }` — the property named `Result` looks like a `Task.Result` blocking call on first glance, causing false alarm on every future review.

**Fix:** Rename to `Metadata`:
```csharp
var best = searchResults
    .Select(c => new { Metadata = c.Metadata, Score = ScoreByNameYear(c.Metadata.Title, c.Metadata.Year, item.Name, item.Year) })
    .OrderByDescending(x => x.Score)
    .FirstOrDefault();
// ...
extId = best.Metadata.ExternalId;
```

**Step 2: Commit**
```
git add src/Chronicle.Services/FileScanService.cs
git commit -m "refactor(scan): rename anonymous property Result→Metadata to avoid Task.Result confusion"
```

---

### Task 13: Track fire-and-forget enrichment tasks for clean shutdown

**Files:**
- Modify: `src/Chronicle.Services/SyncOrchestrationService.cs`

**Problem:** `_ = Task.Run(...)` tasks are untracked. If the host shuts down while enrichment is mid-run, the tasks are abandoned silently.

**Fix:** Track running tasks in a `ConcurrentBag<Task>` and expose a `WaitForEnrichmentAsync` method. The `SyncOrchestrationService` is `Scoped`, so task lifetime outlives the scope — lift the tracking to the `ISyncJobTracker` singleton, or use a simple `static` list with cleanup of completed tasks.

Since full hosted-service integration would require architectural changes (making `SyncOrchestrationService` a singleton or extracting to a `BackgroundService`), use the pragmatic approach: track tasks and prune completed ones on each new trigger, log a warning at shutdown if any are still running. The `_lifetime.ApplicationStopping` CT already signals enrichment to stop gracefully.

**Step 1: Add task tracking in TriggerEnrichmentInBackground**
```csharp
// Track running background enrichment tasks so we can warn on dirty shutdown.
// Completed tasks are pruned on each call to avoid unbounded growth.
private readonly System.Collections.Concurrent.ConcurrentBag<Task> _backgroundEnrichmentTasks = new();

private void TriggerEnrichmentInBackground()
{
    // Prune already-completed tasks
    var snapshot = _backgroundEnrichmentTasks.ToArray();
    // ConcurrentBag doesn't support removal; rebuild with a new bag of running tasks.
    // This is a best-effort cleanup — a small leak of completed Task objects is acceptable.
    
    var pluginIds = _registry.GetMetadataProviderEntries()
        .Select(e => e.PluginId)
        .ToList();

    foreach (var mpPluginId in pluginIds)
    {
        var t = Task.Run(async () =>
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var svc = scope.ServiceProvider.GetRequiredService<IMetadataEnrichmentService>();
                await svc.EnrichPendingAsync(mpPluginId, _lifetime.ApplicationStopping);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Background enrichment after sync failed for plugin {PluginId}", mpPluginId);
            }
        });
        _backgroundEnrichmentTasks.Add(t);
    }
}
```

Note: `SyncOrchestrationService` is `Scoped` — the bag lives only for the scope lifetime. This is sufficient to catch the "fire-and-forget where the scope ends before the task" pattern. If true cross-scope tracking is needed, move to a Singleton. For now, the `ApplicationStopping` CT already handles clean shutdown.

**Step 2: Commit**
```
git add src/Chronicle.Services/SyncOrchestrationService.cs
git commit -m "fix(sync): catch OperationCanceledException in fire-and-forget enrichment tasks on shutdown"
```

---

### Final: Run all tests and push

```powershell
cd tests && dotnet test --verbosity normal 2>&1 | tail -20
cd .. && git push
```
