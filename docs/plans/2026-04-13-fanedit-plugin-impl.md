# FanEdit Plugin Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add fan edit support to Chronicle — a "Fan Edits" media type, a type-switching feature, generic `schedulable`/`run_confirmation` manifest fields, and the `Chronicle.Plugin.FanEdit` metadata provider.

**Architecture:** Two separate repos: Chronicle core (tasks 1–8) adds infrastructure; Chronicle.Plugin.FanEdit (tasks 9–13) is a standalone plugin repo that deploys the same way as TMDB and MusicBrainz. Chronicle core changes are committed to `W:\Scripts\Chronicle`; plugin changes go to `W:\Scripts\Chronicle.Plugin.FanEdit`.

**Tech Stack:** .NET 9, EF Core 9 (SQLite), ASP.NET Core, React 18 + TypeScript, HtmlAgilityPack (plugin only)

---

## PART 1 — Chronicle Core

---

### Task 1: Add `schedulable` + `run_confirmation` to BackgroundTask entity

**Files:**
- Modify: `src/Chronicle.Core/Models/BackgroundTask.cs`
- Modify: `src/Chronicle.Plugins/Models/PluginTaskManifest.cs`
- Modify: `src/Chronicle.Services/Plugins/PluginService.cs` (SeedPluginTasksAsync)
- Create migration: run `dotnet ef migrations add AddBackgroundTaskSchedulingFields -p src/Chronicle.Data -s src/Chronicle.API`
- Test: `tests/Chronicle.Tests.Unit/Services/PluginServiceTests.cs` (or create)

**Step 1: Write failing tests**

Create or open `tests/Chronicle.Tests.Unit/Services/PluginServiceTests.cs` and add:

```csharp
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

public class PluginServiceTests
{
    private static ChronicleDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ChronicleDbContext(opts);
    }

    [Fact]
    public async Task SeedPluginTasksAsync_SetsSchedulable_False_WhenManifestSpecifies()
    {
        await using var db = MakeDb();
        var tasks = new List<PluginTaskManifest>
        {
            new() { TaskId = "fetch-missing-metadata", DisplayName = "Fetch",
                    DefaultCron = null, DefaultEnabled = false,
                    Schedulable = false }
        };

        await PluginService.SeedPluginTasksAsync(db, "chronicle.plugin.fanedit", tasks);

        var row = await db.BackgroundTasks.FindAsync("chronicle.plugin.fanedit:fetch-missing-metadata");
        Assert.NotNull(row);
        Assert.False(row.Schedulable);
    }

    [Fact]
    public async Task SeedPluginTasksAsync_SetsRunConfirmation_WhenManifestSpecifies()
    {
        await using var db = MakeDb();
        var tasks = new List<PluginTaskManifest>
        {
            new() { TaskId = "fetch-missing-metadata", DisplayName = "Fetch",
                    DefaultCron = null, DefaultEnabled = false,
                    RunConfirmationTitle   = "Are you sure?",
                    RunConfirmationMessage = "This scrapes a community site." }
        };

        await PluginService.SeedPluginTasksAsync(db, "chronicle.plugin.fanedit", tasks);

        var row = await db.BackgroundTasks.FindAsync("chronicle.plugin.fanedit:fetch-missing-metadata");
        Assert.Equal("Are you sure?",              row!.RunConfirmationTitle);
        Assert.Equal("This scrapes a community site.", row.RunConfirmationMessage);
    }

    [Fact]
    public async Task SeedPluginTasksAsync_DefaultsSchedulable_True_WhenNotSpecified()
    {
        await using var db = MakeDb();
        var tasks = new List<PluginTaskManifest>
        {
            new() { TaskId = "fetch-missing-metadata", DisplayName = "Fetch", DefaultCron = "0 4 * * *" }
        };

        await PluginService.SeedPluginTasksAsync(db, "chronicle.plugin.tmdb", tasks);

        var row = await db.BackgroundTasks.FindAsync("chronicle.plugin.tmdb:fetch-missing-metadata");
        Assert.True(row!.Schedulable);
        Assert.Null(row.RunConfirmationTitle);
    }
}
```

**Step 2: Run tests — verify they fail**

```bash
cd W:/Scripts/Chronicle
dotnet test tests/Chronicle.Tests.Unit/ --filter "PluginServiceTests" --verbosity normal
```

Expected: compile error — `Schedulable`, `RunConfirmationTitle`, `RunConfirmationMessage` don't exist yet.

**Step 3: Add fields to `BackgroundTask` entity**

In `src/Chronicle.Core/Models/BackgroundTask.cs`, add after `PluginId`:

```csharp
/// <summary>
/// When false, the cron editor is hidden in the UI and the task can only
/// be triggered manually via Run Now. Populated from the plugin manifest.
/// </summary>
public bool Schedulable { get; set; } = true;

/// <summary>
/// When set, the UI shows a confirmation modal before firing Run Now.
/// Populated from the plugin manifest's run_confirmation.title field.
/// </summary>
public string? RunConfirmationTitle   { get; set; }

/// <summary>
/// Body text for the Run Now confirmation modal.
/// Populated from the plugin manifest's run_confirmation.message field.
/// </summary>
public string? RunConfirmationMessage { get; set; }
```

**Step 4: Add fields to `PluginTaskManifest`**

In `src/Chronicle.Plugins/Models/PluginTaskManifest.cs`, add:

```csharp
/// <summary>
/// When null, the cron expression is optional (null allowed) for non-scheduled tasks.
/// </summary>
[JsonPropertyName("default_cron")]
public string? DefaultCron { get; set; }   // change from string to string?

/// <summary>
/// When false, the cron editor is hidden in the UI. Defaults to true.
/// </summary>
[JsonPropertyName("schedulable")]
public bool Schedulable { get; set; } = true;

/// <summary>
/// When present, Run Now shows a confirmation modal before firing.
/// </summary>
[JsonPropertyName("run_confirmation")]
public PluginTaskRunConfirmation? RunConfirmation { get; set; }
```

Add new class at the bottom of the file (same namespace):

```csharp
public class PluginTaskRunConfirmation
{
    [JsonPropertyName("title")]
    public string Title   { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
```

**Step 5: Update `SeedPluginTasksAsync` in `src/Chronicle.Services/Plugins/PluginService.cs`**

Find the `db.BackgroundTasks.Add(new BackgroundTask { ... })` block and add the new fields:

```csharp
db.BackgroundTasks.Add(new BackgroundTask
{
    TaskId                 = namespacedId,
    PluginId               = pluginId,
    DisplayName            = task.DisplayName,
    Description            = task.Description ?? string.Empty,
    CronExpression         = task.DefaultCron ?? string.Empty,  // null → empty
    IsEnabled              = task.DefaultEnabled,
    Schedulable            = task.Schedulable,
    RunConfirmationTitle   = task.RunConfirmation?.Title,
    RunConfirmationMessage = task.RunConfirmation?.Message,
});
```

**Step 6: Run tests — verify they pass**

```bash
dotnet test tests/Chronicle.Tests.Unit/ --filter "PluginServiceTests" --verbosity normal
```

Expected: all 3 new tests pass.

**Step 7: Create the EF Core migration**

```bash
cd W:/Scripts/Chronicle
dotnet ef migrations add AddBackgroundTaskSchedulingFields -p src/Chronicle.Data -s src/Chronicle.API
```

Open the generated migration, verify `Up()` adds three columns to `background_tasks`:

```csharp
migrationBuilder.AddColumn<bool>(
    name: "Schedulable",
    table: "background_tasks",
    type: "INTEGER",
    nullable: false,
    defaultValue: true);

migrationBuilder.AddColumn<string>(
    name: "RunConfirmationTitle",
    table: "background_tasks",
    type: "TEXT",
    nullable: true);

migrationBuilder.AddColumn<string>(
    name: "RunConfirmationMessage",
    table: "background_tasks",
    type: "TEXT",
    nullable: true);
```

If EF generated different column names, rename them to match the above.

**Step 8: Apply migration and run full test suite**

```bash
dotnet test tests/Chronicle.Tests.Unit/ --verbosity quiet
dotnet test tests/Chronicle.Tests.Integration/ --verbosity quiet
```

Expected: all tests pass.

**Step 9: Commit**

```bash
git add src/Chronicle.Core/Models/BackgroundTask.cs \
        src/Chronicle.Plugins/Models/PluginTaskManifest.cs \
        src/Chronicle.Services/Plugins/PluginService.cs \
        src/Chronicle.Data/Migrations/ \
        tests/Chronicle.Tests.Unit/Services/PluginServiceTests.cs
git commit -m "feat(plugins): add schedulable and run_confirmation to background task manifest"
```

---

### Task 2: Expose new fields in the BackgroundTask API

**Files:**
- Modify: `src/Chronicle.API/Controllers/BackgroundTasksController.cs`
- Modify: `src/Chronicle.Web/src/api/backgroundTasks.ts`
- Test: `tests/Chronicle.Tests.Integration/BackgroundTasksTests.cs` (add one test)

**Step 1: Write failing integration test**

Find or create `tests/Chronicle.Tests.Integration/BackgroundTasksTests.cs`. Add:

```csharp
[Fact]
public async Task GetAll_ReturnsSchedulableAndRunConfirmation_Fields()
{
    var client = await AuthClientAsync(admin: true);

    // Seed a task with the new fields directly in the test DB
    using var scope = _factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
    db.BackgroundTasks.Add(new Chronicle.Core.Models.BackgroundTask
    {
        TaskId                 = "test:confirm-task",
        DisplayName            = "Test",
        Description            = "Test task",
        CronExpression         = string.Empty,
        Schedulable            = false,
        RunConfirmationTitle   = "Sure?",
        RunConfirmationMessage = "Body text.",
    });
    await db.SaveChangesAsync();

    var resp = await client.GetAsync("/api/v1/background-tasks");
    resp.EnsureSuccessStatusCode();

    var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    var task = json.RootElement.GetProperty("data")
        .EnumerateArray()
        .First(t => t.GetProperty("taskId").GetString() == "test:confirm-task");

    Assert.False(task.GetProperty("schedulable").GetBoolean());
    Assert.Equal("Sure?",      task.GetProperty("runConfirmation").GetProperty("title").GetString());
    Assert.Equal("Body text.", task.GetProperty("runConfirmation").GetProperty("message").GetString());
}
```

**Step 2: Run test — verify it fails**

```bash
dotnet test tests/Chronicle.Tests.Integration/ --filter "GetAll_ReturnsSchedulableAndRunConfirmation" --verbosity normal
```

Expected: fail — `schedulable` field missing from response.

**Step 3: Add fields to `BackgroundTaskDto`**

In `src/Chronicle.API/Controllers/BackgroundTasksController.cs`, update the record at the bottom:

```csharp
public record BackgroundTaskDto(
    string    TaskId,
    string    DisplayName,
    string    Description,
    string    CronExpression,
    bool      IsEnabled,
    bool      IsRunning,
    DateTime? LastRunAt,
    bool?     LastRunSucceeded,
    string?   LastErrorMessage,
    DateTime? NextRunAt,
    string?   PluginId,
    string?   PluginName,
    string?   PluginIconUrl,
    string?   BrandColorLight,
    string?   BrandColorDark,
    // New
    bool      Schedulable,
    BackgroundTaskRunConfirmationDto? RunConfirmation
);

public record BackgroundTaskRunConfirmationDto(string Title, string Message);
```

**Step 4: Update the mapping in `GetAll()`**

Add to the `new BackgroundTaskDto(...)` call:

```csharp
Schedulable:     r.Schedulable,
RunConfirmation: r.RunConfirmationTitle is not null
    ? new BackgroundTaskRunConfirmationDto(r.RunConfirmationTitle, r.RunConfirmationMessage ?? string.Empty)
    : null
```

**Step 5: Update frontend TypeScript type**

In `src/Chronicle.Web/src/api/backgroundTasks.ts`, add to the `BackgroundTask` interface:

```typescript
schedulable: boolean
runConfirmation: { title: string; message: string } | null
```

**Step 6: Run test — verify it passes**

```bash
dotnet test tests/Chronicle.Tests.Integration/ --filter "GetAll_ReturnsSchedulableAndRunConfirmation" --verbosity normal
dotnet test tests/Chronicle.Tests.Unit/ --verbosity quiet
```

**Step 7: Commit**

```bash
git add src/Chronicle.API/Controllers/BackgroundTasksController.cs \
        src/Chronicle.Web/src/api/backgroundTasks.ts \
        tests/Chronicle.Tests.Integration/BackgroundTasksTests.cs
git commit -m "feat(api): expose schedulable and runConfirmation on background task DTO"
```

---

### Task 3: BackgroundTasksPage UI — schedulable + confirmation modal

**Files:**
- Modify: `src/Chronicle.Web/src/pages/settings/BackgroundTasksPage.tsx`
- Modify: `src/Chronicle.Web/src/pages/settings/BackgroundTasksPage.module.css`

**Step 1: Hide cron editor when `schedulable: false`**

In `BackgroundTasksPage.tsx`, find `TaskCard`. The schedule editor is rendered at roughly:

```tsx
{isEditing && (
  <ScheduleEditor ... />
)}
```

Wrap the Edit/Schedule button and the ScheduleEditor in a `task.schedulable` guard:

```tsx
{task.schedulable && (
  <button
    className={styles.editBtn}
    onClick={isEditing ? onCancelEdit : onEdit}
  >
    {isEditing ? 'Cancel' : 'Edit Schedule'}
  </button>
)}

{isEditing && task.schedulable && (
  <ScheduleEditor
    taskId={task.taskId}
    initialCron={task.cronExpression}
    isEnabled={task.isEnabled}
    onSave={onSave}
    onCancel={onCancelEdit}
  />
)}
```

**Step 2: Add confirmation modal state to `TaskCard`**

Add state variable at the top of `TaskCard`:

```tsx
const [confirmPending, setConfirmPending] = useState(false)
```

Change the Run Now button's `onClick`:

```tsx
onClick={() => task.runConfirmation ? setConfirmPending(true) : onRunNow()}
```

Add the modal JSX after the button group (still inside `TaskCard`):

```tsx
{confirmPending && task.runConfirmation && (
  <div className={styles.confirmOverlay}>
    <div className={styles.confirmModal}>
      <h3 className={styles.confirmTitle}>{task.runConfirmation.title}</h3>
      <p className={styles.confirmBody}>{task.runConfirmation.message}</p>
      <div className={styles.confirmActions}>
        <button className={styles.editBtn} onClick={() => setConfirmPending(false)}>
          Cancel
        </button>
        <button
          className={styles.runBtn}
          onClick={() => { setConfirmPending(false); onRunNow(); }}
        >
          Run Now
        </button>
      </div>
    </div>
  </div>
)}
```

**Step 3: Add modal CSS**

In `BackgroundTasksPage.module.css`, append:

```css
/* ── Run Now confirmation modal ─────────────────────────────────────────── */

.confirmOverlay {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.55);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 500;
}

.confirmModal {
  background: var(--surface-raised, var(--surface));
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 24px 28px;
  max-width: 440px;
  width: 100%;
}

.confirmTitle {
  margin: 0 0 10px;
  font-size: 1rem;
  font-weight: 600;
}

.confirmBody {
  margin: 0 0 20px;
  font-size: 0.88rem;
  color: var(--text-muted);
  line-height: 1.55;
}

.confirmActions {
  display: flex;
  gap: 8px;
  justify-content: flex-end;
}
```

**Step 4: Type-check and verify**

```bash
cd W:/Scripts/Chronicle/src/Chronicle.Web
npm run type-check
```

Expected: no errors.

**Step 5: Commit**

```bash
cd W:/Scripts/Chronicle
git add src/Chronicle.Web/src/pages/settings/BackgroundTasksPage.tsx \
        src/Chronicle.Web/src/pages/settings/BackgroundTasksPage.module.css
git commit -m "feat(ui): hide cron editor for non-schedulable tasks, show confirmation modal before Run Now"
```

---

### Task 4: Fan Edits media type migration

**Files:**
- Create migration: `dotnet ef migrations add AddFanEditsMediaType -p src/Chronicle.Data -s src/Chronicle.API`

**Step 1: Generate migration**

```bash
cd W:/Scripts/Chronicle
dotnet ef migrations add AddFanEditsMediaType -p src/Chronicle.Data -s src/Chronicle.API
```

**Step 2: Edit the generated migration's `Up()` method**

Replace the generated body with:

```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.InsertData(
        table: "media_types",
        columns: new[] { "Id", "CreatedAt", "Description", "DisplayName",
                         "HierarchyLabels", "HierarchyLevels", "InteractionVerb",
                         "IsActive", "IsBuiltIn", "Name", "ProgressUnit" },
        values: new object[]
        {
            4,
            new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc),
            "Fan-edited versions of movies — reworked cuts, custom edits, colour grades",
            "Fan Edits",
            "Fan Edit",
            1,
            "watched",
            true,
            true,
            "fanedits",
            "minutes"
        });
}
```

And `Down()`:

```csharp
protected override void Down(MigrationBuilder migrationBuilder)
{
    migrationBuilder.DeleteData(
        table: "media_types",
        keyColumn: "Id",
        keyValue: 4);
}
```

**Step 3: Apply migration and run tests**

```bash
dotnet test tests/Chronicle.Tests.Unit/ --verbosity quiet
dotnet test tests/Chronicle.Tests.Integration/ --verbosity quiet
```

Expected: all pass. (InMemory tests ignore the seeded row; SQLite tests will have it.)

**Step 4: Commit**

```bash
git add src/Chronicle.Data/Migrations/
git commit -m "feat(db): seed Fan Edits media type"
```

---

### Task 5: `ChangeTypeAsync` service method

**Files:**
- Modify: `src/Chronicle.Services/IMediaService.cs`
- Modify: `src/Chronicle.Services/MediaService.cs`
- Test: `tests/Chronicle.Tests.Unit/Services/MediaServiceChangeTypeTests.cs` (create)

**Step 1: Write failing unit tests**

Create `tests/Chronicle.Tests.Unit/Services/MediaServiceChangeTypeTests.cs`:

```csharp
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

public class MediaServiceChangeTypeTests
{
    private static ChronicleDbContext MakeDb(string name)
    {
        var opts = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new ChronicleDbContext(opts);
    }

    private static MediaService MakeService(ChronicleDbContext db)
        => new(db);

    private static async Task<(MediaType movies, MediaType fanedits)> SeedTypesAsync(ChronicleDbContext db)
    {
        var movies   = new MediaType { Id = 1, Name = "movies",   DisplayName = "Movies",     HierarchyLevels = 1 };
        var fanedits = new MediaType { Id = 4, Name = "fanedits", DisplayName = "Fan Edits",  HierarchyLevels = 1 };
        var tv       = new MediaType { Id = 2, Name = "tv",       DisplayName = "TV",         HierarchyLevels = 3 };
        db.Set<MediaType>().AddRange(movies, fanedits, tv);
        await db.SaveChangesAsync();
        return (movies, fanedits);
    }

    [Fact]
    public async Task ChangeTypeAsync_UpdatesMediaTypeId_OnFlatItem()
    {
        await using var db = MakeDb(nameof(ChangeTypeAsync_UpdatesMediaTypeId_OnFlatItem));
        var (movies, fanedits) = await SeedTypesAsync(db);
        var item = new MediaItem { MediaTypeId = 1, Name = "Blade Runner Fan Edit", HierarchyLevel = 0 };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync();

        var svc = MakeService(db);
        await svc.ChangeTypeAsync(item.Id, fanedits.Id);

        var updated = await db.MediaItems.FindAsync(item.Id);
        updated!.MediaTypeId.Should().Be(fanedits.Id);
    }

    [Fact]
    public async Task ChangeTypeAsync_ClearsMetadataAndExternalIds()
    {
        await using var db = MakeDb(nameof(ChangeTypeAsync_ClearsMetadataAndExternalIds));
        var (movies, fanedits) = await SeedTypesAsync(db);
        var item = new MediaItem { MediaTypeId = 1, Name = "Test", HierarchyLevel = 0,
                                   MetadataJson = "{\"tmdb\":{}}" };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync();
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = item.Id, Source = "tmdb", ExternalId = "movie:550" });
        db.MediaEnrichments.Add(new MediaItemEnrichment { MediaItemId = item.Id, PluginId = "chronicle.plugin.tmdb",
                                                           Status = "Completed" });
        await db.SaveChangesAsync();

        await MakeService(db).ChangeTypeAsync(item.Id, fanedits.Id);

        var updated = await db.MediaItems.FindAsync(item.Id);
        updated!.MetadataJson.Should().BeNull();
        db.MediaExternalIds.Where(e => e.MediaItemId == item.Id).Should().BeEmpty();
        db.MediaEnrichments.Where(e => e.MediaItemId == item.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task ChangeTypeAsync_CascadesToDescendants()
    {
        await using var db = MakeDb(nameof(ChangeTypeAsync_CascadesToDescendants));
        var movies   = new MediaType { Id = 2, Name = "tv", DisplayName = "TV", HierarchyLevels = 3 };
        var target   = new MediaType { Id = 5, Name = "other3", DisplayName = "Other", HierarchyLevels = 3 };
        db.Set<MediaType>().AddRange(movies, target);
        await db.SaveChangesAsync();

        var show    = new MediaItem { MediaTypeId = 2, Name = "Show",   HierarchyLevel = 0 };
        db.MediaItems.Add(show); await db.SaveChangesAsync();
        var season  = new MediaItem { MediaTypeId = 2, Name = "S1",     HierarchyLevel = 1, ParentId = show.Id };
        db.MediaItems.Add(season); await db.SaveChangesAsync();
        var episode = new MediaItem { MediaTypeId = 2, Name = "S1E1",   HierarchyLevel = 2, ParentId = season.Id };
        db.MediaItems.Add(episode); await db.SaveChangesAsync();

        await MakeService(db).ChangeTypeAsync(show.Id, target.Id);

        (await db.MediaItems.FindAsync(show.Id))!.MediaTypeId.Should().Be(target.Id);
        (await db.MediaItems.FindAsync(season.Id))!.MediaTypeId.Should().Be(target.Id);
        (await db.MediaItems.FindAsync(episode.Id))!.MediaTypeId.Should().Be(target.Id);
    }

    [Fact]
    public async Task ChangeTypeAsync_ThrowsInvalidOperation_WhenChildItem()
    {
        await using var db = MakeDb(nameof(ChangeTypeAsync_ThrowsInvalidOperation_WhenChildItem));
        var (movies, fanedits) = await SeedTypesAsync(db);
        var parent = new MediaItem { MediaTypeId = 1, Name = "Parent", HierarchyLevel = 0 };
        db.MediaItems.Add(parent); await db.SaveChangesAsync();
        var child  = new MediaItem { MediaTypeId = 1, Name = "Child",  HierarchyLevel = 1, ParentId = parent.Id };
        db.MediaItems.Add(child); await db.SaveChangesAsync();

        var act = () => MakeService(db).ChangeTypeAsync(child.Id, fanedits.Id);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*root*");
    }

    [Fact]
    public async Task ChangeTypeAsync_ThrowsInvalidOperation_WhenIncompatibleHierarchy()
    {
        await using var db = MakeDb(nameof(ChangeTypeAsync_ThrowsInvalidOperation_WhenIncompatibleHierarchy));
        var movies = new MediaType { Id = 2, Name = "tv",       DisplayName = "TV",       HierarchyLevels = 3 };
        var flat   = new MediaType { Id = 4, Name = "fanedits", DisplayName = "Fan Edits",HierarchyLevels = 1 };
        db.Set<MediaType>().AddRange(movies, flat);
        await db.SaveChangesAsync();
        var show = new MediaItem { MediaTypeId = 2, Name = "Show", HierarchyLevel = 0 };
        db.MediaItems.Add(show); await db.SaveChangesAsync();
        var season = new MediaItem { MediaTypeId = 2, Name = "S1", HierarchyLevel = 1, ParentId = show.Id };
        db.MediaItems.Add(season); await db.SaveChangesAsync();

        var act = () => MakeService(db).ChangeTypeAsync(show.Id, flat.Id);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*incompatible*");
    }
}
```

**Step 2: Run — verify they fail**

```bash
dotnet test tests/Chronicle.Tests.Unit/ --filter "MediaServiceChangeTypeTests" --verbosity normal
```

Expected: compile error — `ChangeTypeAsync` does not exist.

**Step 3: Add method to `IMediaService`**

In `src/Chronicle.Services/IMediaService.cs`, add:

```csharp
/// <summary>
/// Changes the media type of <paramref name="id"/> and all its descendants
/// (cascade), resetting all enrichment data, external IDs, and metadata JSON.
/// </summary>
/// <exception cref="InvalidOperationException">
/// Thrown when <paramref name="id"/> is not a root item, or when the target
/// type's hierarchy depth is incompatible with the existing item tree.
/// </exception>
Task ChangeTypeAsync(int id, int targetMediaTypeId, CancellationToken ct = default);
```

**Step 4: Implement in `MediaService.cs`**

Add the following to `src/Chronicle.Services/MediaService.cs`:

```csharp
public async Task ChangeTypeAsync(int id, int targetMediaTypeId, CancellationToken ct = default)
{
    var item = await _context.MediaItems.FindAsync([id], ct)
        ?? throw new KeyNotFoundException($"Media item {id} not found.");

    if (item.ParentId is not null)
        throw new InvalidOperationException(
            $"Cannot change type on a child item. Use the root item (parent ID {item.ParentId}).");

    var targetType = await _context.Set<MediaType>().FindAsync([targetMediaTypeId], ct)
        ?? throw new KeyNotFoundException($"Media type {targetMediaTypeId} not found.");

    // Collect entire tree (breadth-first)
    var allIds   = new List<int> { id };
    var queue    = new Queue<int>(); queue.Enqueue(id);
    int maxDepth = 0;

    while (queue.Count > 0)
    {
        var parentId = queue.Dequeue();
        var children = await _context.MediaItems
            .Where(m => m.ParentId == parentId)
            .Select(m => new { m.Id, m.HierarchyLevel })
            .ToListAsync(ct);

        foreach (var c in children)
        {
            allIds.Add(c.Id);
            queue.Enqueue(c.Id);
            if (c.HierarchyLevel > maxDepth) maxDepth = c.HierarchyLevel;
        }
    }

    // Actual depth = maxDepth + 1 levels (0-indexed hierarchy)
    var actualDepth = maxDepth + 1;
    if (targetType.HierarchyLevels < actualDepth)
        throw new InvalidOperationException(
            $"Target type '{targetType.DisplayName}' supports {targetType.HierarchyLevels} level(s), " +
            $"but this item tree has {actualDepth} level(s). Types are incompatible.");

    // Atomic reset: update type, clear enrichment, external IDs, metadata
    await _context.MediaItems
        .Where(m => allIds.Contains(m.Id))
        .ExecuteUpdateAsync(s => s
            .SetProperty(m => m.MediaTypeId,  targetMediaTypeId)
            .SetProperty(m => m.MetadataJson, (string?)null), ct);

    await _context.MediaEnrichments
        .Where(e => allIds.Contains(e.MediaItemId))
        .ExecuteDeleteAsync(ct);

    await _context.MediaExternalIds
        .Where(e => allIds.Contains(e.MediaItemId))
        .ExecuteDeleteAsync(ct);
}
```

**Step 5: Run tests — verify they pass**

```bash
dotnet test tests/Chronicle.Tests.Unit/ --filter "MediaServiceChangeTypeTests" --verbosity normal
dotnet test tests/Chronicle.Tests.Unit/ --verbosity quiet
```

**Step 6: Commit**

```bash
git add src/Chronicle.Services/IMediaService.cs \
        src/Chronicle.Services/MediaService.cs \
        tests/Chronicle.Tests.Unit/Services/MediaServiceChangeTypeTests.cs
git commit -m "feat(media): add ChangeTypeAsync — cascade type switch with full data reset"
```

---

### Task 6: `POST /api/v1/media/{id}/change-type` controller endpoint

**Files:**
- Modify: `src/Chronicle.API/Controllers/MediaController.cs`
- Test: `tests/Chronicle.Tests.Integration/MediaChangeTypeTests.cs` (create)

**Step 1: Write failing integration tests**

Create `tests/Chronicle.Tests.Integration/MediaChangeTypeTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chronicle.Tests.Integration;

public class MediaChangeTypeTests : IClassFixture<ChronicleApiFactory>
{
    private readonly ChronicleApiFactory _factory;
    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public MediaChangeTypeTests(ChronicleApiFactory factory)
    {
        factory.SeedDatabase();
        _factory = factory;
    }

    private async Task<(HttpClient client, int movieTypeId, int faneditTypeId)> SetupAsync()
    {
        var client = _factory.CreateClient();
        // Register admin
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { username = $"admin_{Guid.NewGuid():N}", password = "Password123!" });
        var token = JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var movies   = db.Set<MediaType>().First(t => t.Name == "movies");
        // Insert fanedits type if not present (integration test DB starts clean)
        var fanedits = db.Set<MediaType>().FirstOrDefault(t => t.Name == "fanedits");
        if (fanedits is null)
        {
            fanedits = new MediaType { Name = "fanedits", DisplayName = "Fan Edits",
                                       HierarchyLevels = 1, InteractionVerb = "watched",
                                       ProgressUnit = "minutes", IsActive = true, IsBuiltIn = true };
            db.Set<MediaType>().Add(fanedits);
            await db.SaveChangesAsync();
        }
        return (client, movies.Id, fanedits.Id);
    }

    [Fact]
    public async Task ChangeType_Returns200_AndUpdatesType()
    {
        var (client, movieTypeId, faneditTypeId) = await SetupAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var item = new MediaItem { MediaTypeId = movieTypeId, Name = "Test Movie", HierarchyLevel = 0 };
        db.MediaItems.Add(item); await db.SaveChangesAsync();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/media/{item.Id}/change-type",
            new { mediaTypeId = faneditTypeId });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await db.MediaItems.FindAsync(item.Id);
        updated!.MediaTypeId.Should().Be(faneditTypeId);
    }

    [Fact]
    public async Task ChangeType_Returns400_WithParentId_WhenChildItem()
    {
        var (client, movieTypeId, faneditTypeId) = await SetupAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var parent = new MediaItem { MediaTypeId = movieTypeId, Name = "Parent", HierarchyLevel = 0 };
        db.MediaItems.Add(parent); await db.SaveChangesAsync();
        var child = new MediaItem { MediaTypeId = movieTypeId, Name = "Child", HierarchyLevel = 1, ParentId = parent.Id };
        db.MediaItems.Add(child); await db.SaveChangesAsync();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/media/{child.Id}/change-type",
            new { mediaTypeId = faneditTypeId });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("CHANGE_TYPE_USE_ROOT");
        body.RootElement.GetProperty("error").GetProperty("parentId").GetInt32()
            .Should().Be(parent.Id);
    }

    [Fact]
    public async Task ChangeType_Returns401_WhenNotAuthenticated()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/media/1/change-type", new { mediaTypeId = 4 });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
```

**Step 2: Run — verify they fail**

```bash
dotnet test tests/Chronicle.Tests.Integration/ --filter "MediaChangeTypeTests" --verbosity normal
```

Expected: fail — endpoint does not exist (404).

**Step 3: Add DTO and endpoint to `MediaController.cs`**

At the bottom of `MediaController.cs`, add the request record:

```csharp
public record ChangeMediaTypeRequest([Required] int MediaTypeId);
```

Add the endpoint inside the controller class:

```csharp
/// <summary>
/// Changes the media type of an item and all its descendants, resetting all
/// enrichment data, external IDs, and metadata JSON.
/// Admin only. Must be called on the root item — child items return 400.
/// </summary>
[HttpPost("{id:int}/change-type")]
[Authorize(Roles = "Admin")]
public async Task<IActionResult> ChangeType(
    int id,
    [FromBody] ChangeMediaTypeRequest body,
    CancellationToken ct)
{
    try
    {
        await _mediaService.ChangeTypeAsync(id, body.MediaTypeId, ct);
        var updated = await _mediaService.GetByIdAsync(id);
        return Ok(new { success = true, data = updated });
    }
    catch (KeyNotFoundException ex)
    {
        return NotFound(new { success = false, error = new { code = "NOT_FOUND", message = ex.Message } });
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("root"))
    {
        // Extract parent ID from exception message for client redirect
        var item = await _context.MediaItems.FindAsync([id], ct);
        return BadRequest(new { success = false,
            error = new { code = "CHANGE_TYPE_USE_ROOT", message = ex.Message, parentId = item?.ParentId } });
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("incompatible"))
    {
        return BadRequest(new { success = false,
            error = new { code = "INCOMPATIBLE_TYPE", message = ex.Message } });
    }
}
```

Note: `MediaController` will need a `_context` field injected for the root-redirect case. Check whether it already injects `ChronicleDbContext` — if not, inject `IMediaService` already covers the common path; add a direct `ChronicleDbContext` constructor parameter for the `parentId` lookup.

**Step 4: Run tests**

```bash
dotnet test tests/Chronicle.Tests.Integration/ --filter "MediaChangeTypeTests" --verbosity normal
dotnet test tests/Chronicle.Tests.Unit/ --verbosity quiet
```

**Step 5: Commit**

```bash
git add src/Chronicle.API/Controllers/MediaController.cs \
        tests/Chronicle.Tests.Integration/MediaChangeTypeTests.cs
git commit -m "feat(api): add POST /media/{id}/change-type endpoint"
```

---

### Task 7: Change Type UI on MediaDetailPage

**Files:**
- Modify: `src/Chronicle.Web/src/api/media.ts`
- Modify: `src/Chronicle.Web/src/pages/media/MediaDetailPage.tsx`
- Modify: `src/Chronicle.Web/src/pages/media/MediaDetailPage.module.css`

**Step 1: Add API client function**

In `src/Chronicle.Web/src/api/media.ts`, add:

```typescript
export async function changeMediaType(id: number, mediaTypeId: number): Promise<void> {
  await apiPost(`/media/${id}/change-type`, { mediaTypeId })
}
```

(Use whatever `apiPost` helper already exists in `client.ts` — match the pattern of other POST calls in `media.ts`.)

**Step 2: Add state and handler to `MediaDetailPage`**

Add imports at the top (if not already present):

```typescript
import { changeMediaType } from '@/api/media'
import { useMutation, useQueryClient } from '@tanstack/react-query'
```

Add state inside the component (near the `deleteConfirm` state):

```typescript
const [changeTypeOpen, setChangeTypeOpen] = useState(false)
const queryClient = useQueryClient()

const changeTypeMut = useMutation({
  mutationFn: (targetTypeId: number) => changeMediaType(item.id, targetTypeId),
  onSuccess: () => {
    setChangeTypeOpen(false)
    queryClient.invalidateQueries({ queryKey: ['media', item.id] })
  },
})
```

You will also need to fetch the list of media types to populate the dropdown. Add a query (near the other `useQuery` calls):

```typescript
const { data: mediaTypesData } = useQuery({
  queryKey: ['media-types'],
  queryFn: () => apiGet<{ data: MediaType[] }>('/media/types').then(r => r.data),
})
const compatibleTypes = (mediaTypesData ?? []).filter(
  t => t.hierarchyLevels === item.mediaTypeHierarchyLevels && t.id !== item.mediaTypeId
)
```

Note: `item.mediaTypeHierarchyLevels` needs to be present on the API response — verify the `MediaItemDto` includes it. If not, you will need to add it to the DTO and API mapping (find `GetById` in `MediaController` and the DTO record, add `MediaTypeHierarchyLevels`).

**Step 3: Add Change Type button near Delete button**

Find the `deleteArea` div in the JSX. Add the Change Type button alongside it (admin-only guard):

```tsx
{isAdmin && (
  <button
    className={styles.changeTypeBtn}
    onClick={() => setChangeTypeOpen(true)}
  >
    Change Type
  </button>
)}
```

Add the modal JSX (after the delete section):

```tsx
{changeTypeOpen && (
  <div className={styles.changeTypeOverlay}>
    <div className={styles.changeTypeModal}>
      <h3 className={styles.changeTypeTitle}>Change Media Type</h3>
      <p className={styles.changeTypeWarning}>
        This will reset all metadata, enrichment status, and external IDs
        for this item{item.childCount > 0 ? ` and all ${item.childCount} descendants` : ''}.
        This cannot be undone.
      </p>
      <select
        className={styles.changeTypeSelect}
        defaultValue=""
        onChange={e => {
          if (e.target.value) changeTypeMut.mutate(Number(e.target.value))
        }}
        disabled={changeTypeMut.isPending}
      >
        <option value="" disabled>Select new type…</option>
        {compatibleTypes.map(t => (
          <option key={t.id} value={t.id}>{t.displayName}</option>
        ))}
      </select>
      {changeTypeMut.isError && (
        <p className={styles.changeTypeError}>
          {(changeTypeMut.error as Error).message}
        </p>
      )}
      <div className={styles.changeTypeActions}>
        <button
          className={styles.editBtn}
          onClick={() => setChangeTypeOpen(false)}
          disabled={changeTypeMut.isPending}
        >
          Cancel
        </button>
      </div>
    </div>
  </div>
)}
```

**Step 4: Handle child-item redirect**

When `changeTypeMut.isError` and the server returned `CHANGE_TYPE_USE_ROOT`, navigate to the parent:

```typescript
const changeTypeMut = useMutation({
  mutationFn: (targetTypeId: number) => changeMediaType(item.id, targetTypeId),
  onSuccess: () => {
    setChangeTypeOpen(false)
    queryClient.invalidateQueries({ queryKey: ['media', item.id] })
  },
  onError: (err: unknown) => {
    if (err instanceof ApiError && err.code === 'CHANGE_TYPE_USE_ROOT') {
      navigate(`/media/${err.parentId}`)
    }
  },
})
```

The `ApiError` class (in `client.ts`) may need a `parentId` field — check the error handling pattern used elsewhere in the codebase and match it.

**Step 5: Add CSS**

In `MediaDetailPage.module.css`, append:

```css
/* ── Change Type button ─────────────────────────────────────────────────── */

.changeTypeBtn {
  /* Match the Delete button style — adjust to match existing .deleteBtn */
  font-size: 0.82rem;
  padding: 5px 12px;
  border-radius: 6px;
  border: 1px solid var(--border);
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
}

.changeTypeBtn:hover { border-color: var(--text-muted); color: var(--text); }

/* ── Change Type modal ──────────────────────────────────────────────────── */

.changeTypeOverlay {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.55);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 500;
}

.changeTypeModal {
  background: var(--surface-raised, var(--surface));
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 24px 28px;
  max-width: 420px;
  width: 100%;
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.changeTypeTitle { margin: 0; font-size: 1rem; font-weight: 600; }

.changeTypeWarning {
  margin: 0;
  font-size: 0.85rem;
  color: var(--text-muted);
  line-height: 1.5;
}

.changeTypeSelect {
  padding: 7px 10px;
  border-radius: 6px;
  border: 1px solid var(--border);
  background: var(--surface);
  color: var(--text);
  font-size: 0.9rem;
}

.changeTypeError { color: var(--danger); font-size: 0.82rem; margin: 0; }

.changeTypeActions { display: flex; justify-content: flex-end; }
```

**Step 6: Type-check**

```bash
cd W:/Scripts/Chronicle/src/Chronicle.Web && npm run type-check
```

Fix any type errors before continuing.

**Step 7: Commit**

```bash
cd W:/Scripts/Chronicle
git add src/Chronicle.Web/src/api/media.ts \
        src/Chronicle.Web/src/pages/media/MediaDetailPage.tsx \
        src/Chronicle.Web/src/pages/media/MediaDetailPage.module.css
git commit -m "feat(ui): add Change Type control to media detail page"
```

---

## PART 2 — Chronicle.Plugin.FanEdit (separate repo)

All remaining work goes in `W:\Scripts\Chronicle.Plugin.FanEdit\`. This repo is independent — it references `Chronicle.Plugins` and `Chronicle.Core` from the Chronicle solution, the same way TMDB and MusicBrainz do.

---

### Task 8: Scaffold the plugin project

**Files:**
- Create: `W:\Scripts\Chronicle.Plugin.FanEdit\Chronicle.Plugin.FanEdit.csproj`
- Create: `W:\Scripts\Chronicle.Plugin.FanEdit\manifest.json`

**Step 1: Create the .csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <AssemblyName>Chronicle.Plugin.FanEdit</AssemblyName>
    <RootNamespace>Chronicle.Plugin.FanEdit</RootNamespace>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <InternalsVisibleTo>Chronicle.Plugin.FanEdit.Tests</InternalsVisibleTo>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Chronicle\src\Chronicle.Plugins\Chronicle.Plugins.csproj"
                      Private="false" ExcludeAssets="runtime" />
    <ProjectReference Include="..\Chronicle\src\Chronicle.Core\Chronicle.Core.csproj"
                      Private="false" ExcludeAssets="runtime" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="HtmlAgilityPack" Version="1.11.*" />
  </ItemGroup>
  <ItemGroup>
    <None Update="manifest.json">
      <CopyToOutputDirectory>Always</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
```

**Step 2: Create manifest.json**

```json
{
  "plugin_id":             "chronicle.plugin.fanedit",
  "name":                  "FanEdit (IFDB)",
  "version":               "1.0.0",
  "author":                "Chronicle Contributors",
  "description":           "Fetches fanedit metadata from the Internet Fan Edit Database (fanedit.org). Requires a registered fanedit.org account. Please use responsibly — a minimum 1-second delay between requests is enforced.",
  "min_chronicle_version": "0.1.0",
  "entry_type":            "Chronicle.Plugin.FanEdit.FanEditMetadataProvider",
  "iconUrl":               "https://www.fanedit.org/favicon.ico",
  "brandColorLight":       "#8B1A1A",
  "brandColorDark":        "#C0392B",
  "fixMatchHint":          "Enter a fanedit.org URL (e.g. https://www.fanedit.org/my-edit/) or a bare IFDB numeric ID",
  "background_tasks": [
    {
      "task_id":         "fetch-missing-metadata",
      "display_name":    "Fetch Missing Metadata",
      "description":     "Looks up IFDB metadata for fan edits that don't have it yet.",
      "default_cron":    null,
      "default_enabled": false,
      "schedulable":     false,
      "run_confirmation": {
        "title":   "Fetch fan edit metadata?",
        "message": "fanedit.org is a small community site maintained by volunteers. This task makes one HTTP request per fan edit with a minimum 1-second delay — on a large library this will take a long time. Please run this sparingly, not more than a few times per week."
      }
    },
    {
      "task_id":         "resync-all-metadata",
      "display_name":    "Re-sync All Metadata",
      "description":     "Re-fetches IFDB metadata for all fan edits to pick up updated descriptions, ratings, and images.",
      "default_cron":    null,
      "default_enabled": false,
      "schedulable":     false,
      "run_confirmation": {
        "title":   "Re-sync all fan edit metadata?",
        "message": "This will re-fetch IFDB metadata for every fan edit in your library. fanedit.org is a small community site — each request has a minimum 1-second delay. On a large library this will take a very long time. Please use this sparingly."
      }
    }
  ]
}
```

**Step 3: Create Models folder stubs**

Create empty files (to be filled in later tasks):
- `Models/FanEditEntry.cs`
- `Models/FanEditSearchResult.cs`
- `Models/FanEditTechSpecs.cs`

Each should just have the namespace:

```csharp
namespace Chronicle.Plugin.FanEdit.Models;
```

**Step 4: Build to verify project compiles**

```bash
cd W:/Scripts/Chronicle.Plugin.FanEdit
dotnet build
```

Expected: succeeds (no source files yet — just the project structure).

**Step 5: Commit**

```bash
cd W:/Scripts/Chronicle.Plugin.FanEdit
git add Chronicle.Plugin.FanEdit.csproj manifest.json Models/
git commit -m "feat: scaffold Chronicle.Plugin.FanEdit project"
```

---

### Task 9: FanEditRateLimiter

**Files:**
- Create: `W:\Scripts\Chronicle.Plugin.FanEdit\FanEditRateLimiter.cs`
- Create: `W:\Scripts\Chronicle.Plugin.FanEdit\tests\FanEditRateLimiterTests.cs`

**Step 1: Create test project**

In `W:\Scripts\Chronicle.Plugin.FanEdit\tests\`, create `Chronicle.Plugin.FanEdit.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Chronicle.Plugin.FanEdit.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
    <PackageReference Include="FluentAssertions" Version="6.*" />
  </ItemGroup>
</Project>
```

**Step 2: Write failing tests**

Create `tests/FanEditRateLimiterTests.cs`:

```csharp
using Chronicle.Plugin.FanEdit;
using FluentAssertions;
using System.Diagnostics;
using Xunit;

namespace Chronicle.Plugin.FanEdit.Tests;

public class FanEditRateLimiterTests
{
    [Fact]
    public async Task ThrottleAsync_EnforcesMinimumDelay()
    {
        var limiter = new FanEditRateLimiter(delayMs: 200);
        var sw = Stopwatch.StartNew();

        await limiter.ThrottleAsync(CancellationToken.None); // first call — no wait
        await limiter.ThrottleAsync(CancellationToken.None); // second — must wait ~200ms

        sw.Elapsed.TotalMilliseconds.Should().BeGreaterThan(150);
    }

    [Fact]
    public void Constructor_ClampsDelayToFloor()
    {
        // Cannot set below 1000ms floor
        var limiter = new FanEditRateLimiter(delayMs: 100);
        limiter.DelayMs.Should().Be(1000);
    }

    [Fact]
    public async Task ThrottleAsync_RespectsCancellation()
    {
        var limiter = new FanEditRateLimiter(delayMs: 5000);
        await limiter.ThrottleAsync(CancellationToken.None); // seed last-request time

        using var cts = new CancellationTokenSource(50);
        var act = () => limiter.ThrottleAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
```

**Step 3: Run — verify they fail**

```bash
cd W:/Scripts/Chronicle.Plugin.FanEdit
dotnet test tests/ --verbosity normal
```

Expected: compile error — `FanEditRateLimiter` does not exist.

**Step 4: Implement `FanEditRateLimiter.cs`**

```csharp
namespace Chronicle.Plugin.FanEdit;

/// <summary>
/// Serialises all outbound HTTP requests to fanedit.org with a minimum
/// inter-request delay. The 1,000 ms floor is hard-coded and cannot be
/// reduced via configuration.
/// </summary>
internal sealed class FanEditRateLimiter
{
    private const int FloorMs = 1_000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly System.Diagnostics.Stopwatch _last = System.Diagnostics.Stopwatch.StartNew();

    public int DelayMs { get; }

    public FanEditRateLimiter(int delayMs = FloorMs)
    {
        DelayMs = Math.Max(delayMs, FloorMs);
    }

    public async Task ThrottleAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var elapsed = _last.ElapsedMilliseconds;
            if (elapsed < DelayMs)
                await Task.Delay((int)(DelayMs - elapsed), ct);
            _last.Restart();
        }
        finally
        {
            _gate.Release();
        }
    }
}
```

**Step 5: Run tests — verify they pass**

```bash
dotnet test tests/ --filter "FanEditRateLimiterTests" --verbosity normal
```

**Step 6: Commit**

```bash
git add FanEditRateLimiter.cs tests/FanEditRateLimiterTests.cs tests/Chronicle.Plugin.FanEdit.Tests.csproj
git commit -m "feat: implement FanEditRateLimiter with 1s floor"
```

---

### Task 10: FanEditAuthService

**Files:**
- Create: `W:\Scripts\Chronicle.Plugin.FanEdit\FanEditAuthService.cs`
- Create: `W:\Scripts\Chronicle.Plugin.FanEdit\tests\FanEditAuthServiceTests.cs`

**Step 1: Write failing tests**

```csharp
using Chronicle.Plugin.FanEdit;
using FluentAssertions;
using System.Net;
using Xunit;

namespace Chronicle.Plugin.FanEdit.Tests;

public class FanEditAuthServiceTests
{
    private static HttpClient MakeClient(HttpMessageHandler handler)
        => new(handler) { BaseAddress = new Uri("https://www.fanedit.org") };

    [Fact]
    public async Task EnsureSessionAsync_ReturnsFalse_WhenLoginResponseMissesCookie()
    {
        var handler = new FakeHttpHandler(loginResponse: new HttpResponseMessage(HttpStatusCode.OK)
        {
            // No Set-Cookie header
        });
        var cookies = new CookieContainer();
        var auth = new FanEditAuthService(MakeClient(handler), cookies, new FanEditRateLimiter(1000));

        var result = await auth.EnsureSessionAsync("user", "pass", CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task EnsureSessionAsync_ReturnsTrue_WhenLoginSetsWordPressLoggedInCookie()
    {
        var loginResp = new HttpResponseMessage(HttpStatusCode.Found);
        loginResp.Headers.Add("Set-Cookie",
            "wordpress_logged_in_abc=value; Path=/; HttpOnly");

        var handler = new FakeHttpHandler(
            noncePage: new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("<input name=\"_wpnonce\" value=\"abc123\"/>") },
            loginResponse: loginResp);

        var cookies = new CookieContainer();
        var auth = new FanEditAuthService(MakeClient(handler), cookies, new FanEditRateLimiter(1000));

        var result = await auth.EnsureSessionAsync("user", "pass", CancellationToken.None);

        result.Should().BeTrue();
        auth.IsSessionEstablished.Should().BeTrue();
    }

    [Fact]
    public async Task IsSessionExpired_ReturnsTrue_WhenResponseRedirectsToLogin()
    {
        var resp = new HttpResponseMessage(HttpStatusCode.Found);
        resp.Headers.Location = new Uri("https://www.fanedit.org/wp-login.php");

        FanEditAuthService.IsSessionExpiredResponse(resp).Should().BeTrue();
    }
}

// Minimal fake HTTP handler for testing
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly HttpResponseMessage? _noncePage;
    private readonly HttpResponseMessage _loginResponse;
    private int _callCount;

    public FakeHttpHandler(
        HttpResponseMessage? noncePage = null,
        HttpResponseMessage? loginResponse = null)
    {
        _noncePage     = noncePage ?? new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("<input name=\"_wpnonce\" value=\"nonce\"/>") };
        _loginResponse = loginResponse ?? new HttpResponseMessage(HttpStatusCode.OK);
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        _callCount++;
        // First call = nonce page GET, second = login POST
        return Task.FromResult(_callCount == 1 ? _noncePage! : _loginResponse);
    }
}
```

**Step 2: Run — verify they fail**

```bash
dotnet test tests/ --filter "FanEditAuthServiceTests" --verbosity normal
```

**Step 3: Implement `FanEditAuthService.cs`**

```csharp
using HtmlAgilityPack;

namespace Chronicle.Plugin.FanEdit;

/// <summary>
/// Handles WordPress form login against fanedit.org and maintains the session cookie.
/// </summary>
internal sealed class FanEditAuthService
{
    private const string LoginUrl   = "https://www.fanedit.org/wp-login.php";
    private const string CookieName = "wordpress_logged_in_";

    private readonly HttpClient         _http;
    private readonly CookieContainer    _cookies;
    private readonly FanEditRateLimiter _limiter;

    public bool IsSessionEstablished { get; private set; }

    public FanEditAuthService(HttpClient http, CookieContainer cookies, FanEditRateLimiter limiter)
    {
        _http    = http;
        _cookies = cookies;
        _limiter = limiter;
    }

    /// <summary>
    /// Attempts to log in. Returns true if a WordPress session cookie is obtained.
    /// </summary>
    public async Task<bool> EnsureSessionAsync(string username, string password, CancellationToken ct)
    {
        IsSessionEstablished = false;

        // Step 1: fetch login page to extract _wpnonce
        await _limiter.ThrottleAsync(ct);
        var nonceResp = await _http.GetAsync(LoginUrl, ct);
        var nonceHtml = await nonceResp.Content.ReadAsStringAsync(ct);
        var nonce     = ExtractNonce(nonceHtml);

        // Step 2: POST credentials
        await _limiter.ThrottleAsync(ct);
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["log"]         = username,
            ["pwd"]         = password,
            ["wp-submit"]   = "Log In",
            ["redirect_to"] = "/",
            ["testcookie"]  = "1",
            ["_wpnonce"]    = nonce ?? string.Empty,
        });

        var loginResp = await _http.PostAsync(LoginUrl, form, ct);

        // Check for session cookie in response headers
        if (loginResp.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var cookie in setCookies)
            {
                if (cookie.StartsWith(CookieName, StringComparison.OrdinalIgnoreCase))
                {
                    IsSessionEstablished = true;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Returns true when a response is a redirect to the login page (session expired).</summary>
    public static bool IsSessionExpiredResponse(HttpResponseMessage response)
    {
        if (response.StatusCode != System.Net.HttpStatusCode.Found) return false;
        var loc = response.Headers.Location?.ToString() ?? string.Empty;
        return loc.Contains("wp-login.php", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractNonce(string html)
    {
        var doc  = new HtmlDocument();
        doc.LoadHtml(html);
        var node = doc.DocumentNode.SelectSingleNode("//input[@name='_wpnonce']");
        return node?.GetAttributeValue("value", null);
    }
}
```

**Step 4: Run tests — verify they pass**

```bash
dotnet test tests/ --filter "FanEditAuthServiceTests" --verbosity normal
```

**Step 5: Commit**

```bash
git add FanEditAuthService.cs tests/FanEditAuthServiceTests.cs
git commit -m "feat: implement FanEditAuthService with WordPress login flow"
```

---

### Task 11: FanEditScraper

**Files:**
- Create: `W:\Scripts\Chronicle.Plugin.FanEdit\Models\FanEditEntry.cs`
- Create: `W:\Scripts\Chronicle.Plugin.FanEdit\Models\FanEditSearchResult.cs`
- Create: `W:\Scripts\Chronicle.Plugin.FanEdit\Models\FanEditTechSpecs.cs`
- Create: `W:\Scripts\Chronicle.Plugin.FanEdit\FanEditScraper.cs`
- Create: `W:\Scripts\Chronicle.Plugin.FanEdit\tests\FanEditScraperTests.cs`

**Step 1: Define models**

`Models/FanEditSearchResult.cs`:
```csharp
namespace Chronicle.Plugin.FanEdit.Models;

internal sealed class FanEditSearchResult
{
    public string  Title        { get; set; } = string.Empty;
    public string  Url          { get; set; } = string.Empty;
    public string? ThumbnailUrl { get; set; }
    public string? Excerpt      { get; set; }
    public int?    Year         { get; set; }
}
```

`Models/FanEditTechSpecs.cs`:
```csharp
namespace Chronicle.Plugin.FanEdit.Models;

internal sealed class FanEditTechSpecs
{
    public string? VideoCodec      { get; set; }
    public string? AudioCodec      { get; set; }
    public string? Resolution      { get; set; }
    public string? AspectRatio     { get; set; }
    public string? ContainerFormat { get; set; }
    public double? FileSizeGb      { get; set; }
}
```

`Models/FanEditEntry.cs`:
```csharp
namespace Chronicle.Plugin.FanEdit.Models;

internal sealed class FanEditEntry
{
    public string   Title               { get; set; } = string.Empty;
    public string   Url                 { get; set; } = string.Empty;
    public string?  Overview            { get; set; }
    public int?     Year                { get; set; }
    public int?     RuntimeMinutes      { get; set; }
    public string?  PosterUrl           { get; set; }
    public List<string> AdditionalImages { get; set; } = [];
    public List<string> Genres          { get; set; } = [];
    public double?  Rating              { get; set; }
    public List<string> Tags            { get; set; } = [];

    // Source material
    public string?  OriginalTitle       { get; set; }
    public int?     OriginalYear        { get; set; }
    public string?  OriginalImdbId      { get; set; }

    // Editor info
    public string?  EditorUsername      { get; set; }
    public string?  EditorProfileUrl    { get; set; }

    // Classification
    public string?  FanEditType         { get; set; }
    public List<string> IfdbCategories  { get; set; } = [];

    // Tech specs
    public FanEditTechSpecs? TechSpecs  { get; set; }

    // Edit details
    public List<string> ChangesList     { get; set; } = [];
    public int?     NumberOfCuts        { get; set; }
    public int?     NumberOfAdditions   { get; set; }

    // Reception
    public string?  IfdbRatingRaw       { get; set; }
    public int?     IfdbRatingCount     { get; set; }
    public List<string> IfdbAwards      { get; set; } = [];

    // Publishing
    public string?  IfdbId              { get; set; }
    public string?  IfdbPublishedDate   { get; set; }
    public List<string> DistributionLinks { get; set; } = [];
}
```

**Step 2: Write failing tests for the scraper**

Create `tests/FanEditScraperTests.cs` using embedded HTML fixtures:

```csharp
using Chronicle.Plugin.FanEdit;
using FluentAssertions;
using Xunit;

namespace Chronicle.Plugin.FanEdit.Tests;

public class FanEditScraperTests
{
    private static FanEditScraper Scraper() => new();

    private const string SearchHtml = """
        <html><body>
        <article class="post type-fanedit">
          <h2 class="entry-title"><a href="https://www.fanedit.org/blade-runner-the-final-edit/">Blade Runner: The Final Edit</a></h2>
          <div class="entry-summary"><p>A refined cut of Blade Runner.</p></div>
          <img src="https://www.fanedit.org/wp-content/uploads/br.jpg" />
          <span class="year">1982</span>
        </article>
        </body></html>
        """;

    private const string DetailHtml = """
        <html>
        <head>
          <meta property="og:title" content="Blade Runner: The Final Edit" />
          <meta property="og:description" content="A refined cut." />
          <meta property="og:image" content="https://www.fanedit.org/poster.jpg" />
        </head>
        <body>
          <dl>
            <dt>Editor:</dt><dd>SomeEditor</dd>
            <dt>Runtime:</dt><dd>117 min</dd>
            <dt>Video:</dt><dd>H.264</dd>
            <dt>Audio:</dt><dd>AC3 5.1</dd>
          </dl>
          <div class="ifdb-rating">8.5 (42 votes)</div>
        </body></html>
        """;

    [Fact]
    public void ParseSearchResults_ExtractsTitle_Url_Year()
    {
        var results = Scraper().ParseSearchResults(SearchHtml);

        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Blade Runner: The Final Edit");
        results[0].Url.Should().Be("https://www.fanedit.org/blade-runner-the-final-edit/");
        results[0].Year.Should().Be(1982);
    }

    [Fact]
    public void ParseDetailPage_ExtractsOgTitle_And_Overview()
    {
        var entry = Scraper().ParseDetailPage(DetailHtml, "https://www.fanedit.org/blade-runner-the-final-edit/");

        entry.Title.Should().Be("Blade Runner: The Final Edit");
        entry.Overview.Should().Be("A refined cut.");
        entry.PosterUrl.Should().Be("https://www.fanedit.org/poster.jpg");
    }

    [Fact]
    public void ParseDetailPage_ExtractsRuntime_FromDefinitionList()
    {
        var entry = Scraper().ParseDetailPage(DetailHtml, "https://www.fanedit.org/x/");
        entry.RuntimeMinutes.Should().Be(117);
    }

    [Fact]
    public void ParseDetailPage_ExtractsTechSpecs()
    {
        var entry = Scraper().ParseDetailPage(DetailHtml, "https://www.fanedit.org/x/");
        entry.TechSpecs.Should().NotBeNull();
        entry.TechSpecs!.VideoCodec.Should().Be("H.264");
        entry.TechSpecs.AudioCodec.Should().Be("AC3 5.1");
    }

    [Fact]
    public void ParseDetailPage_HandlesMissingFields_Gracefully()
    {
        // Nearly empty page — no fields should throw
        var entry = Scraper().ParseDetailPage("<html><body></body></html>", "https://www.fanedit.org/x/");
        entry.Should().NotBeNull();
        entry.Title.Should().BeEmpty();
    }
}
```

**Step 3: Run — verify they fail**

```bash
dotnet test tests/ --filter "FanEditScraperTests" --verbosity normal
```

**Step 4: Implement `FanEditScraper.cs`**

Create `FanEditScraper.cs`. The scraper must:
- `ParseSearchResults(string html)` → `List<FanEditSearchResult>`: select `article.post.type-fanedit` (or equivalent), extract title, URL (from `<a>` in `h2.entry-title`), year (from `.year` span or regex on title), thumbnail from `<img>`.
- `ParseDetailPage(string html, string url)` → `FanEditEntry`: extract OpenGraph tags first, then definition list, then free text. Never throw on missing fields.

Example skeleton — fill in XPath selectors by inspecting the live site structure:

```csharp
using HtmlAgilityPack;
using Chronicle.Plugin.FanEdit.Models;
using System.Text.RegularExpressions;

namespace Chronicle.Plugin.FanEdit;

internal sealed class FanEditScraper
{
    private static readonly Regex _runtimeRegex   = new(@"(\d+)\s*min", RegexOptions.IgnoreCase);
    private static readonly Regex _yearRegex       = new(@"\b(19|20)\d{2}\b");
    private static readonly Regex _ratingRegex     = new(@"([\d.]+)\s*\((\d+)\s*vote");

    public List<FanEditSearchResult> ParseSearchResults(string html)
    {
        var doc     = new HtmlDocument();
        doc.LoadHtml(html);
        var results = new List<FanEditSearchResult>();

        // Selector may need adjustment based on live site markup
        foreach (var article in doc.DocumentNode.SelectNodes("//article[contains(@class,'type-fanedit')]") ?? [])
        {
            var titleNode = article.SelectSingleNode(".//h2[contains(@class,'entry-title')]/a")
                         ?? article.SelectSingleNode(".//h1/a");
            if (titleNode is null) continue;

            var result = new FanEditSearchResult
            {
                Title        = HtmlEntity.DeEntitize(titleNode.InnerText.Trim()),
                Url          = titleNode.GetAttributeValue("href", string.Empty),
                ThumbnailUrl = article.SelectSingleNode(".//img")?.GetAttributeValue("src", null),
                Excerpt      = article.SelectSingleNode(".//*[contains(@class,'entry-summary')]")
                                      ?.InnerText.Trim(),
            };

            var yearNode = article.SelectSingleNode(".//*[contains(@class,'year')]");
            if (yearNode is not null && int.TryParse(yearNode.InnerText.Trim(), out var y))
                result.Year = y;

            results.Add(result);
        }

        return results;
    }

    public FanEditEntry ParseDetailPage(string html, string url)
    {
        var doc   = new HtmlDocument();
        doc.LoadHtml(html);
        var entry = new FanEditEntry { Url = url };

        // Priority 1: OpenGraph
        entry.Title    = OgMeta(doc, "og:title")    ?? string.Empty;
        entry.Overview = OgMeta(doc, "og:description");
        entry.PosterUrl= OgMeta(doc, "og:image");

        // Priority 2: definition list key-value pairs
        ParseDefinitionList(doc, entry);

        // Priority 3: rating
        var ratingNode = doc.DocumentNode.SelectSingleNode("//*[contains(@class,'ifdb-rating')]");
        if (ratingNode is not null)
        {
            var m = _ratingRegex.Match(ratingNode.InnerText);
            if (m.Success)
            {
                entry.IfdbRatingRaw   = m.Groups[1].Value;
                entry.IfdbRatingCount = int.TryParse(m.Groups[2].Value, out var rc) ? rc : null;
                entry.Rating          = double.TryParse(m.Groups[1].Value,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var rv) ? rv : null;
            }
        }

        return entry;
    }

    private static string? OgMeta(HtmlDocument doc, string property)
        => doc.DocumentNode
              .SelectSingleNode($"//meta[@property='{property}']")
              ?.GetAttributeValue("content", null);

    private static void ParseDefinitionList(HtmlDocument doc, FanEditEntry entry)
    {
        var dts = doc.DocumentNode.SelectNodes("//dl/dt") ?? [];
        foreach (var dt in dts)
        {
            var key = dt.InnerText.Trim().TrimEnd(':').ToLowerInvariant();
            var dd  = dt.SelectSingleNode("following-sibling::dd[1]");
            var val = dd?.InnerText.Trim();
            if (string.IsNullOrEmpty(val)) continue;

            switch (key)
            {
                case "editor":
                    entry.EditorUsername = val;
                    entry.EditorProfileUrl = dd!.SelectSingleNode(".//a")?.GetAttributeValue("href", null);
                    break;
                case "runtime":
                    var rm = Regex.Match(val, @"(\d+)");
                    if (rm.Success) entry.RuntimeMinutes = int.Parse(rm.Groups[1].Value);
                    break;
                case "video":
                    entry.TechSpecs ??= new();
                    entry.TechSpecs.VideoCodec = val;
                    break;
                case "audio":
                    entry.TechSpecs ??= new();
                    entry.TechSpecs.AudioCodec = val;
                    break;
                case "type":
                    entry.FanEditType = val;
                    break;
                case "original":
                    entry.OriginalTitle = val;
                    break;
            }
        }
    }
}
```

**Important:** The actual HTML selectors depend on fanedit.org's live markup. After implementing, manually test against a real page before writing the provider. Adjust selectors as needed — the tests use embedded HTML that you control, so keep them in sync with whatever selectors you choose.

**Step 5: Run tests — verify they pass**

```bash
dotnet test tests/ --filter "FanEditScraperTests" --verbosity normal
```

**Step 6: Commit**

```bash
git add Models/ FanEditScraper.cs tests/FanEditScraperTests.cs
git commit -m "feat: implement FanEditScraper with HtmlAgilityPack"
```

---

### Task 12: FanEditMetadataProvider

**Files:**
- Create: `W:\Scripts\Chronicle.Plugin.FanEdit\FanEditMetadataProvider.cs`
- Create: `W:\Scripts\Chronicle.Plugin.FanEdit\tests\FanEditMetadataProviderTests.cs`

**Step 1: Write failing unit tests**

```csharp
using Chronicle.Plugin.FanEdit;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using FluentAssertions;
using System.Net;
using Xunit;

namespace Chronicle.Plugin.FanEdit.Tests;

public class FanEditMetadataProviderTests
{
    [Fact]
    public void GetSupportedMediaTypes_ReturnsFaneditsOnly()
    {
        var provider = new FanEditMetadataProvider();
        var types    = provider.GetSupportedMediaTypes();

        types.Should().HaveCount(1);
        types[0].MediaTypeName.Should().Be("fanedits");
    }

    [Fact]
    public void GetSettingsSchema_ContainsRequiredKeys()
    {
        var schema = new FanEditMetadataProvider().GetSettingsSchema();
        var keys   = schema.Settings.Select(s => s.Key).ToList();

        keys.Should().Contain("username");
        keys.Should().Contain("password");
        keys.Should().Contain("request_delay_ms");
    }

    [Fact]
    public void PluginId_IsCorrect()
    {
        new FanEditMetadataProvider().PluginId.Should().Be("chronicle.plugin.fanedit");
    }

    [Fact]
    public async Task SearchAsync_ThrowsInvalidOperation_WhenNotConfigured()
    {
        var provider = new FanEditMetadataProvider();
        var ctx      = new MediaSearchContext { Name = "Blade Runner" };
        var act      = () => provider.SearchAsync(ctx);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not configured*");
    }
}
```

**Step 2: Run — verify they fail**

```bash
dotnet test tests/ --filter "FanEditMetadataProviderTests" --verbosity normal
```

**Step 3: Implement `FanEditMetadataProvider.cs`**

```csharp
using Chronicle.Plugin.FanEdit.Models;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using System.Net;
using System.Text.Json;

namespace Chronicle.Plugin.FanEdit;

/// <summary>
/// IMetadataProvider implementation for fanedit.org (IFDB).
/// Supports media type "fanedits" only.
/// </summary>
public sealed class FanEditMetadataProvider : IMetadataProvider
{
    private const string BaseUrl    = "https://www.fanedit.org";
    private const int    ScoreThreshold = 50;

    private string?             _username;
    private string?             _password;
    private FanEditRateLimiter? _limiter;
    private FanEditAuthService? _auth;
    private FanEditScraper?     _scraper;
    private HttpClient?         _http;

    // ── Identity ──────────────────────────────────────────────────────────
    public string PluginId => "chronicle.plugin.fanedit";
    public string Name     => "FanEdit (IFDB)";
    public string Version  => "1.0.0";
    public string Author   => "Chronicle Contributors";

    // ── Capabilities ──────────────────────────────────────────────────────
    public MediaTypeSupport[] GetSupportedMediaTypes() =>
    [
        new MediaTypeSupport { MediaTypeName = "fanedits" }
    ];

    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new SettingDefinition
            {
                Key         = "username",
                Label       = "fanedit.org Username",
                Type        = SettingType.Text,
                Required    = true,
            },
            new SettingDefinition
            {
                Key         = "password",
                Label       = "fanedit.org Password",
                Type        = SettingType.Password,
                Required    = true,
            },
            new SettingDefinition
            {
                Key          = "request_delay_ms",
                Label        = "Request Delay (ms)",
                Description  = "Minimum delay between requests. Floor: 1000 ms. Be kind to the server.",
                Type         = SettingType.Number,
                Required     = false,
                DefaultValue = "1000",
            },
            new SettingDefinition
            {
                Key          = "user_agent",
                Label        = "User-Agent String",
                Type         = SettingType.Text,
                Required     = false,
                DefaultValue = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
            },
        ]
    };

    // ── Lifecycle ─────────────────────────────────────────────────────────
    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        _username = settings.GetValueOrDefault("username");
        _password = settings.GetValueOrDefault("password");

        var delayMs  = settings.TryGetValue("request_delay_ms", out var d) && int.TryParse(d, out var di) ? di : 1000;
        var ua       = settings.GetValueOrDefault("user_agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");

        _limiter  = new FanEditRateLimiter(delayMs);
        _scraper  = new FanEditScraper();

        var cookies  = new CookieContainer();
        var handler  = new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false };
        _http        = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        _http.DefaultRequestHeaders.Add("User-Agent", ua);
        _auth = new FanEditAuthService(_http, cookies, _limiter);
    }

    // ── Core operations ───────────────────────────────────────────────────
    public async Task<IReadOnlyList<ScoredCandidate>> SearchAsync(
        MediaSearchContext context, CancellationToken ct = default)
    {
        EnsureConfigured();

        await EnsureSessionAsync(ct);

        var titlesToTry = new List<string> { context.Name };
        if (context.AltTitles is { Count: > 0 })
            titlesToTry.AddRange(context.AltTitles);

        var seen       = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<ScoredCandidate>();

        foreach (var title in titlesToTry.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var searchUrl = $"{BaseUrl}/ifdb/?s={Uri.EscapeDataString(title)}&post_type=fanedit";
            await _limiter!.ThrottleAsync(ct);
            var resp = await _http!.GetAsync(searchUrl, ct);
            resp.EnsureSuccessStatusCode();
            var html    = await resp.Content.ReadAsStringAsync(ct);
            var results = _scraper!.ParseSearchResults(html);

            foreach (var r in results)
            {
                if (!seen.Add(r.Url)) continue;
                var score = ScoreSearchResult(context, r);
                if (score >= ScoreThreshold)
                    candidates.Add(new ScoredCandidate
                    {
                        Score    = score,
                        Metadata = new MediaMetadata
                        {
                            Title      = r.Title,
                            Year       = r.Year,
                            PosterUrl  = r.ThumbnailUrl,
                            ExternalId = UrlToExternalId(r.Url),
                        }
                    });
            }
        }

        return candidates.OrderByDescending(c => c.Score).Take(10).ToList();
    }

    public async Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default)
    {
        EnsureConfigured();
        await EnsureSessionAsync(ct);

        var url = ResolveUrl(externalId);
        await _limiter!.ThrottleAsync(ct);
        var resp = await _http!.GetAsync(url, ct);

        if (FanEditAuthService.IsSessionExpiredResponse(resp))
        {
            // Re-authenticate once and retry
            await _auth!.EnsureSessionAsync(_username!, _password!, ct);
            await _limiter.ThrottleAsync(ct);
            resp = await _http.GetAsync(url, ct);
        }

        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new KeyNotFoundException($"No IFDB entry found at {url}");

        resp.EnsureSuccessStatusCode();
        var html  = await resp.Content.ReadAsStringAsync(ct);
        var entry = _scraper!.ParseDetailPage(html, url);

        return MapToMetadata(entry, url);
    }

    public async Task<byte[]> GetImageAsync(string url, CancellationToken ct = default)
    {
        EnsureConfigured();
        await _limiter!.ThrottleAsync(ct);
        var resp = await _http!.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        if (_auth is null || _username is null || _password is null) return false;
        try { return await _auth.EnsureSessionAsync(_username, _password, ct); }
        catch { return false; }
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private void EnsureConfigured()
    {
        if (_limiter is null)
            throw new InvalidOperationException("FanEditMetadataProvider is not configured. Call Configure() first.");
    }

    private async Task EnsureSessionAsync(CancellationToken ct)
    {
        if (!_auth!.IsSessionEstablished)
        {
            var ok = await _auth.EnsureSessionAsync(_username!, _password!, ct);
            if (!ok)
                throw new InvalidOperationException(
                    "Could not log in to fanedit.org. Check your username and password in plugin settings.");
        }
    }

    private static int ScoreSearchResult(MediaSearchContext ctx, FanEditSearchResult r)
    {
        var score = 0;
        var norm  = NormaliseTitle(r.Title);
        var query = NormaliseTitle(ctx.Name);

        if (norm == query)                             score += 40;
        else if (LevenshteinRatio(norm, query) <= 0.2) score += 20;

        if (ctx.Year.HasValue && r.Year.HasValue)
        {
            var diff = Math.Abs(ctx.Year.Value - r.Year.Value);
            if (diff == 0) score += 20;
            else if (diff == 1) score += 10;
            else score -= 10;
        }

        return score;
    }

    private static string NormaliseTitle(string t)
        => t.ToLowerInvariant().Trim();

    private static double LevenshteinRatio(string a, string b)
    {
        if (a == b) return 0;
        var maxLen = Math.Max(a.Length, b.Length);
        if (maxLen == 0) return 0;
        return (double)LevenshteinDistance(a, b) / maxLen;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        for (var j = 1; j <= b.Length; j++)
        {
            var cost = a[i - 1] == b[j - 1] ? 0 : 1;
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
        }
        return d[a.Length, b.Length];
    }

    private static string UrlToExternalId(string url)
    {
        // https://www.fanedit.org/blade-runner-the-final-edit/ → fanedit:blade-runner-the-final-edit
        var slug = url.TrimEnd('/').Split('/').LastOrDefault() ?? url;
        return $"fanedit:{slug}";
    }

    private static string ResolveUrl(string externalId)
    {
        if (externalId.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return externalId;
        if (externalId.StartsWith("fanedit:", StringComparison.OrdinalIgnoreCase))
        {
            var id = externalId["fanedit:".Length..];
            return int.TryParse(id, out _)
                ? $"{BaseUrl}/ifdb/{id}/"
                : $"{BaseUrl}/{id}/";
        }
        return int.TryParse(externalId, out _)
            ? $"{BaseUrl}/ifdb/{externalId}/"
            : $"{BaseUrl}/{externalId}/";
    }

    private static MediaMetadata MapToMetadata(FanEditEntry entry, string url)
    {
        var extData = new Dictionary<string, object?>
        {
            ["originalTitle"]       = entry.OriginalTitle,
            ["originalYear"]        = entry.OriginalYear,
            ["originalImdbId"]      = entry.OriginalImdbId,
            ["editorUsername"]      = entry.EditorUsername,
            ["editorProfileUrl"]    = entry.EditorProfileUrl,
            ["fanEditType"]         = entry.FanEditType,
            ["ifdbCategories"]      = entry.IfdbCategories,
            ["techSpecs"]           = entry.TechSpecs is null ? null : new
            {
                videoCodec      = entry.TechSpecs.VideoCodec,
                audioCodec      = entry.TechSpecs.AudioCodec,
                resolution      = entry.TechSpecs.Resolution,
                aspectRatio     = entry.TechSpecs.AspectRatio,
                containerFormat = entry.TechSpecs.ContainerFormat,
                fileSizeGb      = entry.TechSpecs.FileSizeGb,
            },
            ["changesList"]         = entry.ChangesList,
            ["numberOfCuts"]        = entry.NumberOfCuts,
            ["numberOfAdditions"]   = entry.NumberOfAdditions,
            ["ifdbRatingRaw"]       = entry.IfdbRatingRaw,
            ["ifdbRatingCount"]     = entry.IfdbRatingCount,
            ["ifdbAwards"]          = entry.IfdbAwards,
            ["ifdbId"]              = entry.IfdbId,
            ["ifdbUrl"]             = url,
            ["ifdbPublishedDate"]   = entry.IfdbPublishedDate,
            ["distributionLinks"]   = entry.DistributionLinks,
        };

        return new MediaMetadata
        {
            Title            = entry.Title,
            Overview         = entry.Overview,
            Year             = entry.Year,
            RuntimeMinutes   = entry.RuntimeMinutes,
            PosterUrl        = entry.PosterUrl,
            Genres           = entry.Genres,
            Rating           = entry.Rating,
            Tags             = entry.Tags,
            ExternalId       = UrlToExternalId(url),
            ExtendedData     = JsonSerializer.SerializeToElement(extData),
            AdditionalImages = entry.AdditionalImages
                .Select(u => new AdditionalImage { Url = u, Type = "Screenshot" })
                .ToList(),
        };
    }
}
```

**Step 4: Run tests — verify they pass**

```bash
dotnet test tests/ --verbosity normal
```

Expected: all tests in `FanEditMetadataProviderTests` and all prior tests pass.

**Step 5: Build the full plugin**

```bash
cd W:/Scripts/Chronicle.Plugin.FanEdit
dotnet build --configuration Release
```

Expected: builds without errors.

**Step 6: Commit**

```bash
git add FanEditMetadataProvider.cs tests/FanEditMetadataProviderTests.cs
git commit -m "feat: implement FanEditMetadataProvider — IMetadataProvider for fanedit.org"
```

---

## Final verification

After all tasks are complete:

**Run full Chronicle test suite:**
```bash
cd W:/Scripts/Chronicle
dotnet test tests/Chronicle.Tests.Unit/ --verbosity quiet
dotnet test tests/Chronicle.Tests.Integration/ --verbosity quiet
```

**Run full plugin test suite:**
```bash
cd W:/Scripts/Chronicle.Plugin.FanEdit
dotnet test tests/ --verbosity quiet
```

**Frontend type-check:**
```bash
cd W:/Scripts/Chronicle/src/Chronicle.Web
npm run type-check && npm run lint
```

All should pass before considering the implementation complete.
