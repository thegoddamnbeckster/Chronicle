# Metadata Assignment — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Implement the four Metadata Assignment features from docs/METADATA_ASSIGNMENT.md: physical file icons, plugin metadata folds, background tasks grouped by plugin, and the Metadata Assignment settings page.

**Architecture:** Four independent features implemented in order. Physical file indicators require a small backend DTO change + frontend icons. Plugin metadata folds require extending UserPreferences (C# + TypeScript) and wrapping existing PluginMetadataBox components. Background task grouping is frontend-only. Metadata Assignment page is a new settings page backed by a new app_settings key.

**Tech Stack:** .NET 9 / ASP.NET Core (backend), React 18 + TypeScript / CSS Modules (frontend), existing `app_settings` k/v store, `preferences_json` on users table.

---

## Key files (read these before touching anything)

| File | Purpose |
|---|---|
| `src/Chronicle.Core/Models/UserPreferences.cs` | UserPreferences class — currently only has `ShowDiagnostics` |
| `src/Chronicle.Services/UserService.cs` | `UpdatePreferencesAsync` — hardcoded field merge |
| `src/Chronicle.API/Controllers/UsersController.cs` | Preferences PATCH endpoint |
| `src/Chronicle.Web/src/api/users.ts` | `updateMyPreferences` — currently typed for `showDiagnostics` only |
| `src/Chronicle.Web/src/types/index.ts` | `MediaItem`, `FileScannerMeta` types |
| `src/Chronicle.Web/src/pages/library/LibraryPage.tsx` | Library card rendering (inlined, no separate component) |
| `src/Chronicle.Web/src/pages/media/MediaDetailPage.tsx` | Plugin metadata box rendering (lines 353–390) |
| `src/Chronicle.Web/src/pages/settings/BackgroundTasksPage.tsx` | Background task list |
| `src/Chronicle.Web/src/api/settings.ts` | `getAppSettings`, `putAppSetting` |
| `src/Chronicle.API/Controllers/SettingsController.cs` | App settings endpoints |
| `src/Chronicle.Web/src/App.tsx` | Route definitions |

---

## Phase 1 — Physical File Indicators

### Task 1: Add `hasPhysicalFile` / `hasMetadataOnly` to MediaItem API response (backend)

**Files:**
- Read first: grep for `MediaItemDto` or however the MediaItem API response is built
- Modify: wherever `MediaItem` API response is constructed (likely `MediaController.cs` or a mapping helper)
- Test: `tests/Chronicle.Tests.Unit/` or `tests/Chronicle.Tests.Integration/`

**Context:** `fileScannerMeta` is already in the API response for each MediaItem. The library page loads only root items (`rootOnly: true`). For root items (movies, TV shows), we need to know if any descendant has a physical file. A TV show's `fileScannerMeta` is null — episodes have the scanner data, not the show root. We need computed booleans.

**Logic:**
- `hasPhysicalFile = true` if this item's own `metadata_json` contains `"fileScanner"` AND has a `filePaths` or `filePath` key with a non-null value, OR if any descendant item does
- `hasMetadataOnly = true` if this item OR any descendant lacks file scanner data
- For leaf items (no children): exactly one of the two is true
- For parent items with all-file children: `hasPhysicalFile=true`, `hasMetadataOnly=false`
- For parent items with no-file children: `hasPhysicalFile=false`, `hasMetadataOnly=true`
- For parent items with mixed children: both `true`

**Step 1: Find how MediaItem DTO is constructed**

```bash
grep -rn "fileScannerMeta\|FileScannerMeta\|fileScanner" src/Chronicle.API/ --include="*.cs" | head -30
grep -rn "class MediaItemDto\|MapToDto\|new MediaItemDto" src/ --include="*.cs" | head -20
```

**Step 2: Write failing integration test**

Find the existing MediaController integration tests. Add:

```csharp
[Fact]
public async Task GetMedia_LeafItemWithFileScanner_HasPhysicalFileTrue()
{
    // Arrange — create a movie with fileScanner metadata
    var client = factory.CreateClient();
    await AuthenticateAsync(client);
    // Create media item with metadata_json containing fileScanner data
    // (look at existing test seeding patterns for how to do this)
    
    // Act
    var response = await client.GetAsync("/api/v1/media/{id}");
    
    // Assert
    var body = await response.Content.ReadFromJsonAsync<JsonElement>();
    Assert.True(body.GetProperty("data").GetProperty("hasPhysicalFile").GetBoolean());
    Assert.False(body.GetProperty("data").GetProperty("hasMetadataOnly").GetBoolean());
}
```

**Step 3: Run test to confirm it fails**

```bash
dotnet test tests/Chronicle.Tests.Integration/ --filter "HasPhysicalFile" -v n 2>&1 | tail -10
```

Expected: FAIL — property not found.

**Step 4: Add the properties**

In the MediaItem DTO mapping (find it first with grep), add:

```csharp
// Compute from metadata_json
bool hasFileScanner = HasFileScannerData(item.MetadataJson);
bool childrenHaveFile = false;
bool childrenMissFile = false;

if (item.Children?.Any() == true)
{
    childrenHaveFile = item.Children.Any(c => HasFileScannerData(c.MetadataJson));
    childrenMissFile = item.Children.Any(c => !HasFileScannerData(c.MetadataJson));
}

HasPhysicalFile  = hasFileScanner || childrenHaveFile,
HasMetadataOnly  = !hasFileScanner || childrenMissFile,
```

Add a static helper:

```csharp
private static bool HasFileScannerData(string? metadataJson)
{
    if (string.IsNullOrEmpty(metadataJson)) return false;
    // Quick string check — avoids full JSON parse on every item
    if (!metadataJson.Contains("\"fileScanner\"")) return false;
    try
    {
        var doc = JsonDocument.Parse(metadataJson);
        if (!doc.RootElement.TryGetProperty("fileScanner", out var fs)) return false;
        // Has filePaths array with at least one entry, or filePath string
        if (fs.TryGetProperty("filePaths", out var fp) && fp.GetArrayLength() > 0) return true;
        if (fs.TryGetProperty("filePath", out var f) && f.ValueKind != JsonValueKind.Null) return true;
        return false;
    }
    catch { return false; }
}
```

Also add these to the `MediaItem` TypeScript type in `src/Chronicle.Web/src/types/index.ts`:

```typescript
hasPhysicalFile?: boolean | null
hasMetadataOnly?: boolean | null
```

**Step 5: Run the test**

```bash
dotnet test tests/Chronicle.Tests.Integration/ --filter "HasPhysicalFile" -v n 2>&1 | tail -10
```

Expected: PASS.

**Step 6: Run full unit tests**

```bash
dotnet test tests/Chronicle.Tests.Unit/Chronicle.Tests.Unit.csproj 2>&1 | tail -5
```

Expected: all passing (or only the known pre-existing flaky tests failing).

**Step 7: Commit**

```bash
git add src/ tests/
git commit -m "feat(media): add hasPhysicalFile and hasMetadataOnly to MediaItem API response"
```

---

### Task 2: Physical file icons — library cards and media detail header (frontend)

**Files:**
- Modify: `src/Chronicle.Web/src/pages/library/LibraryPage.tsx`
- Modify: `src/Chronicle.Web/src/pages/media/MediaDetailPage.tsx`
- Modify: `src/Chronicle.Web/src/pages/library/LibraryPage.module.css`
- Modify: `src/Chronicle.Web/src/pages/media/MediaDetailPage.module.css`

**Context:** Library cards are rendered inline in LibraryPage.tsx starting around line 537. The card has a `.poster` div with an image. The detail page has a header with title/year/chips.

**Icons:**
- Physical file: `💾` or a disk SVG — use a simple inline SVG
- Metadata only: `☁` or cloud SVG
- Both shown when parent has mixed children

Use a small inline SVG so there's no icon library dependency:

```tsx
// HDD icon (physical file present)
const IconHdd = () => (
  <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor" aria-label="Has physical file">
    <path d="M4 6a2 2 0 0 1 2-2h12a2 2 0 0 1 2 2v4H4V6zm0 6h16v6a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2v-6zm10 3a1 1 0 1 0 2 0 1 1 0 0 0-2 0z"/>
  </svg>
)

// Cloud icon (metadata only, no physical file)
const IconCloud = () => (
  <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor" aria-label="Metadata only">
    <path d="M17.5 9A5.5 5.5 0 0 0 6.6 7.6 4 4 0 1 0 5 15h12.5a3.5 3.5 0 0 0 0-7z"/>
  </svg>
)
```

**Step 1: Add icons to LibraryPage.tsx**

In LibraryPage.tsx, define the two icon components at the top of the file (after imports). Then in the card's poster div, add an icon overlay. Find the `.poster` div (around line 573) and add the indicator after the img/placeholder:

```tsx
{/* Physical file indicator */}
<div className={styles.fileIndicator}>
  {entry.mediaItem.hasPhysicalFile && <span className={styles.fileIcon}><IconHdd /></span>}
  {entry.mediaItem.hasMetadataOnly && <span className={styles.metaIcon}><IconCloud /></span>}
</div>
```

**Step 2: Add CSS for the indicators**

In `LibraryPage.module.css`, add:

```css
.poster {
  position: relative; /* ensure overlay positions correctly — check if already set */
}

.fileIndicator {
  position: absolute;
  bottom: 4px;
  right: 4px;
  display: flex;
  gap: 2px;
}

.fileIcon,
.metaIcon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 20px;
  height: 20px;
  border-radius: 4px;
  background: rgba(0, 0, 0, 0.65);
  color: #fff;
}

.fileIcon { color: #6fcf97; }   /* green for physical */
.metaIcon { color: #56ccf2; }   /* blue for cloud */
```

**Step 3: Add icon to media detail page header**

In `MediaDetailPage.tsx`, find the `metaRow` div (around line 327) and add after the chips:

```tsx
<div className={styles.fileIndicator}>
  {item.hasPhysicalFile && <span className={styles.fileIcon} title="Has physical file"><IconHdd /></span>}
  {item.hasMetadataOnly && <span className={styles.metaIcon} title="Metadata only (no physical file)"><IconCloud /></span>}
</div>
```

Add matching CSS in `MediaDetailPage.module.css`.

**Step 4: Verify TypeScript compiles**

```bash
cd src/Chronicle.Web && npm run type-check 2>&1 | tail -5
```

**Step 5: Commit**

```bash
git add src/Chronicle.Web/
git commit -m "feat(ui): add physical file and metadata-only icons to library cards and media detail"
```

---

## Phase 2 — Plugin Metadata Folds

### Task 3: Extend UserPreferences for fold state (backend)

**Files:**
- Modify: `src/Chronicle.Core/Models/UserPreferences.cs`
- Modify: `src/Chronicle.Services/UserService.cs` — `UpdatePreferencesAsync`
- Read first: `src/Chronicle.API/Controllers/UsersController.cs` — find the PATCH preferences endpoint to understand the request DTO

**Step 1: Update `UserPreferences.cs`**

Current content:
```csharp
public class UserPreferences
{
    public bool? ShowDiagnostics { get; set; }
}
```

New content:
```csharp
public class UserPreferences
{
    public bool? ShowDiagnostics { get; set; }
    public bool? DefaultFoldsOpen { get; set; }
    /// <summary>
    /// Per-fold open/closed state. Keys: "media.{id}.{pluginId}", "backgroundTasks.{pluginId}".
    /// Values: true = open, false = closed.
    /// </summary>
    public Dictionary<string, bool>? Folds { get; set; }
}
```

**Step 2: Update `UpdatePreferencesAsync`**

Find the merge block. After the existing `ShowDiagnostics` merge line, add:

```csharp
if (patch.DefaultFoldsOpen.HasValue)
    current.DefaultFoldsOpen = patch.DefaultFoldsOpen;

if (patch.Folds is { Count: > 0 })
{
    current.Folds ??= new Dictionary<string, bool>();
    foreach (var (key, value) in patch.Folds)
        current.Folds[key] = value;
}
```

**Important:** The Folds merge is additive — incoming keys are merged into existing dict, not replacing it. This allows a single PATCH to update one fold key without clobbering others.

**Step 3: Write a unit test**

Find the UserService tests. Add:

```csharp
[Fact]
public async Task UpdatePreferencesAsync_FoldsMerges_DoesNotReplaceExistingKeys()
{
    // Arrange
    var user = await SeedUserAsync();
    // Set initial folds
    await service.UpdatePreferencesAsync(user.Id, new UserPreferences
    {
        Folds = new() { { "media.1.tmdb", true } }
    });

    // Act — update a different key
    await service.UpdatePreferencesAsync(user.Id, new UserPreferences
    {
        Folds = new() { { "media.2.tmdb", false } }
    });

    // Assert — both keys present
    var prefs = await service.GetPreferencesAsync(user.Id);
    Assert.True(prefs.Folds!.ContainsKey("media.1.tmdb"));
    Assert.True(prefs.Folds!.ContainsKey("media.2.tmdb"));
    Assert.True(prefs.Folds["media.1.tmdb"]);
    Assert.False(prefs.Folds["media.2.tmdb"]);
}
```

**Step 4: Run test**

```bash
dotnet test tests/Chronicle.Tests.Unit/ --filter "FoldsMerges" -v n 2>&1 | tail -10
```

Expected: PASS.

**Step 5: Commit**

```bash
git add src/ tests/
git commit -m "feat(prefs): add DefaultFoldsOpen and Folds to UserPreferences with additive merge"
```

---

### Task 4: Plugin metadata folds on MediaDetailPage (frontend)

**Files:**
- Modify: `src/Chronicle.Web/src/api/users.ts`
- Modify: `src/Chronicle.Web/src/pages/media/MediaDetailPage.tsx`
- Modify: `src/Chronicle.Web/src/pages/media/MediaDetailPage.module.css`

**Step 1: Update `users.ts` API client**

```typescript
export async function updateMyPreferences(
  prefs: {
    showDiagnostics?: boolean
    defaultFoldsOpen?: boolean
    folds?: Record<string, boolean>
  }
): Promise<void> {
  await client.patch('/users/me/preferences', prefs)
}
```

**Step 2: Add a `useFold` hook in `MediaDetailPage.tsx`**

At the top of the file (or in a separate `src/hooks/useFold.ts`), add a hook that reads/writes a single fold's state:

```typescript
/**
 * Manages the open/closed state of a named fold.
 * Defaults to `defaultOpen`. Persists changes to the server via preferences API.
 */
function useFold(key: string, defaultOpen: boolean) {
  const [isOpen, setIsOpen] = useState(defaultOpen)

  useEffect(() => {
    // Load initial state from server preferences on mount
    // (In practice, user preferences are loaded at app level; for now, default is fine)
    // Future: read from loaded preferences context
  }, [key])

  function toggle() {
    const next = !isOpen
    setIsOpen(next)
    // Fire-and-forget — don't block UI on save
    updateMyPreferences({ folds: { [key]: next } }).catch(() => {})
  }

  return { isOpen, toggle }
}
```

Note: For the initial state, the user's saved preferences would ideally be loaded from the auth context. For now, default to open (`true`) and persist on first interaction. A full preferences context is out of scope for this task.

**Step 3: Wrap each PluginMetadataBox in a fold**

In `MediaDetailPage.tsx`, find where PluginMetadataBox is rendered (around line 374). Wrap it:

```tsx
{(() => {
  const pluginIds = new Set([
    ...Object.keys(item.pluginMetadata ?? {}),
    ...Object.keys(item.enrichmentStatuses ?? {}),
  ])
  return Array.from(pluginIds).map(pluginId => {
    const plugin = plugins.find(p => p.pluginId === pluginId)
    if (plugin?.supportedMediaTypes?.length) {
      const itemType = (item.mediaTypeInternalName ?? item.mediaTypeName).toLowerCase()
      const supported = plugin.supportedMediaTypes.some(t => t.toLowerCase() === itemType)
      if (!supported) return null
    }
    const metadata = item.pluginMetadata?.[pluginId]
    const enrichStatus = item.enrichmentStatuses?.[pluginId]

    return (
      <PluginFold
        key={`${mediaId}-${pluginId}`}
        foldKey={`media.${mediaId}.${pluginId}`}
        label={plugin?.name ?? pluginId}
        iconUrl={plugin?.iconUrl}
      >
        <PluginMetadataBox
          mediaId={mediaId}
          pluginId={pluginId}
          pluginName={plugin?.name ?? pluginId}
          iconUrl={plugin?.iconUrl}
          fixMatchHint={plugin?.fixMatchHint}
          metadata={metadata}
          enrichmentStatus={enrichStatus}
          externalIds={item.externalIds}
          refreshLogs={item.refreshLogs}
          onImageClick={(localIdx) => setLightboxIdx((pluginImageOffsets.get(pluginId) ?? 0) + localIdx)}
          imageStartIndex={pluginImageOffsets.get(pluginId) ?? 0}
        />
      </PluginFold>
    )
  })
})()}
```

**Step 4: Create `PluginFold` component**

Add inline in MediaDetailPage.tsx (or as a new file `src/components/PluginFold.tsx`):

```tsx
interface PluginFoldProps {
  foldKey: string
  label: string
  iconUrl?: string | null
  children: React.ReactNode
}

function PluginFold({ foldKey, label, iconUrl, children }: PluginFoldProps) {
  const { isOpen, toggle } = useFold(foldKey, true)

  return (
    <div className={styles.pluginFold}>
      <button className={styles.pluginFoldHeader} onClick={toggle} aria-expanded={isOpen}>
        {iconUrl && <img src={iconUrl} alt="" className={styles.pluginFoldIcon} />}
        <span className={styles.pluginFoldLabel}>{label}</span>
        <span className={`${styles.pluginFoldChevron} ${isOpen ? styles.pluginFoldChevronOpen : ''}`}>
          ›
        </span>
      </button>
      {isOpen && <div className={styles.pluginFoldBody}>{children}</div>}
    </div>
  )
}
```

**Step 5: Add CSS**

In `MediaDetailPage.module.css`:

```css
.pluginFold {
  margin-bottom: 8px;
}

.pluginFoldHeader {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  background: var(--bg-card);
  border: 1px solid var(--border);
  border-radius: 6px;
  padding: 8px 12px;
  cursor: pointer;
  text-align: left;
  font-weight: 600;
  font-size: 0.85rem;
  color: var(--text);
}

.pluginFoldHeader:hover {
  background: var(--bg-hover, var(--border));
}

.pluginFoldIcon {
  width: 16px;
  height: 16px;
  object-fit: contain;
}

.pluginFoldLabel {
  flex: 1;
}

.pluginFoldChevron {
  display: inline-block;
  transform: rotate(0deg);
  transition: transform 0.15s;
}

.pluginFoldChevronOpen {
  transform: rotate(90deg);
}

.pluginFoldBody {
  border: 1px solid var(--border);
  border-top: none;
  border-radius: 0 0 6px 6px;
}
```

**Step 6: Verify TypeScript**

```bash
cd src/Chronicle.Web && npm run type-check 2>&1 | tail -5
```

**Step 7: Commit**

```bash
git add src/Chronicle.Web/ src/Chronicle.Core/ src/Chronicle.Services/ tests/
git commit -m "feat(ui): wrap plugin metadata boxes in collapsible folds with persisted state"
```

---

## Phase 3 — Background Tasks Grouped by Plugin

### Task 5: Group background tasks by plugin in BackgroundTasksPage (frontend only)

**Files:**
- Modify: `src/Chronicle.Web/src/pages/settings/BackgroundTasksPage.tsx`
- Modify: `src/Chronicle.Web/src/pages/settings/BackgroundTasksPage.module.css`

**Context:** The `BackgroundTask` type already has `pluginId`, `pluginName`, `pluginIconUrl`. The current "Scheduled Tasks" fold renders them as a flat list. We need to group by plugin. Tasks with `pluginId === null` are system tasks. Each plugin group gets its own fold, default closed, state persisted.

**Step 1: Replace the flat task list with grouped folds**

Find the "Scheduled Tasks" section (around line 673 in BackgroundTasksPage.tsx). Replace the flat `tasks.map(task => ...)` with:

```tsx
{/* Group tasks by plugin. null pluginId = "System". */}
{(() => {
  // Build ordered list of groups: system first, then plugins alphabetically
  const groups = new Map<string | null, BackgroundTask[]>()
  for (const task of tasks) {
    const key = task.pluginId  // null for system tasks
    if (!groups.has(key)) groups.set(key, [])
    groups.get(key)!.push(task)
  }

  return Array.from(groups.entries()).map(([pluginId, groupTasks]) => {
    const pluginName = groupTasks[0].pluginName ?? 'System'
    const iconUrl    = groupTasks[0].pluginIconUrl
    const foldKey    = `backgroundTasks.${pluginId ?? 'system'}`

    return (
      <PluginTaskGroup
        key={pluginId ?? 'system'}
        foldKey={foldKey}
        pluginName={pluginName}
        iconUrl={iconUrl}
      >
        {groupTasks.map(task => (
          <TaskCard
            key={task.taskId}
            task={task}
            isRunning={task.isRunning || runningIds.has(task.taskId)}
            isEditing={editingId === task.taskId}
            onRunNow={() => handleRunNow(task.taskId)}
            onEdit={() => setEditingId(task.taskId)}
            onSave={(cron, enabled) => handleSave(task.taskId, cron, enabled)}
            onCancelEdit={() => setEditingId(null)}
            onToggle={() => updateBackgroundTask(task.taskId, { isEnabled: !task.isEnabled }).then(load)}
          />
        ))}
      </PluginTaskGroup>
    )
  })
})()}
```

Note: `TaskCard` is the existing per-task card JSX (currently inlined in the map). Extract it into a `TaskCard` component within the same file.

**Step 2: Create `PluginTaskGroup` component (inline in BackgroundTasksPage.tsx)**

```tsx
interface PluginTaskGroupProps {
  foldKey: string
  pluginName: string
  iconUrl: string | null
  children: React.ReactNode
}

function PluginTaskGroup({ foldKey, pluginName, iconUrl, children }: PluginTaskGroupProps) {
  const storageKey = `chronicle_fold_${foldKey}`
  const [isOpen, setIsOpen] = useState(() => {
    // Default: closed (as per design). Persist in localStorage for BG Tasks page.
    const saved = localStorage.getItem(storageKey)
    return saved === null ? false : saved === 'true'
  })

  function toggle() {
    const next = !isOpen
    setIsOpen(next)
    localStorage.setItem(storageKey, String(next))
  }

  return (
    <div className={styles.pluginGroup}>
      <button className={styles.pluginGroupHeader} onClick={toggle} aria-expanded={isOpen}>
        {iconUrl && <img src={iconUrl} alt="" className={styles.pluginGroupIcon} />}
        <span className={styles.pluginGroupName}>{pluginName}</span>
        <span className={`${styles.pluginGroupChevron} ${isOpen ? styles.pluginGroupChevronOpen : ''}`}>
          ›
        </span>
      </button>
      {isOpen && <div className={styles.pluginGroupBody}>{children}</div>}
    </div>
  )
}
```

Note: For background tasks page we use `localStorage` for fold state (simpler than server preferences for this page; the design allows either mechanism).

**Step 3: Add CSS in `BackgroundTasksPage.module.css`**

```css
.pluginGroup {
  margin-bottom: 12px;
  border: 1px solid var(--border);
  border-radius: 8px;
  overflow: hidden;
}

.pluginGroupHeader {
  display: flex;
  align-items: center;
  gap: 10px;
  width: 100%;
  padding: 12px 16px;
  background: var(--bg-card);
  border: none;
  cursor: pointer;
  text-align: left;
  font-weight: 600;
  font-size: 0.9rem;
  color: var(--text);
}

.pluginGroupHeader:hover { background: var(--bg-hover, var(--border)); }

.pluginGroupIcon { width: 18px; height: 18px; object-fit: contain; }

.pluginGroupName { flex: 1; }

.pluginGroupChevron {
  display: inline-block;
  transform: rotate(0deg);
  transition: transform 0.15s;
}

.pluginGroupChevronOpen { transform: rotate(90deg); }

.pluginGroupBody {
  border-top: 1px solid var(--border);
  padding: 12px;
  display: flex;
  flex-direction: column;
  gap: 12px;
}
```

**Step 4: Verify TypeScript and lint**

```bash
cd src/Chronicle.Web && npm run type-check && npm run lint 2>&1 | tail -5
```

**Step 5: Commit**

```bash
git add src/Chronicle.Web/
git commit -m "feat(ui): group background tasks by plugin with collapsible folds"
```

---

## Phase 4 — Metadata Assignment Page

### Task 6: Backend — metadata assignment API endpoints

**Files:**
- Modify: `src/Chronicle.API/Controllers/SettingsController.cs`
- Read first: the existing `GET /settings/app` and `PUT /settings/app/{key}` endpoints to understand the pattern

**Context:** Assignment config is stored in `app_settings` under key `metadata_assignment.config` as a JSON blob. Format: `{ "movies": { "title": ["tmdb"], "poster_url": ["tmdb", "fileScanner"] }, "tv": { ... } }`.

The new endpoints wrap the existing app_settings store with assignment-specific logic.

**Step 1: Define the field map per media type**

This is static data — it defines which fields are assignable for each media type:

```csharp
private static readonly Dictionary<string, string[]> AssignableFields = new()
{
    ["movies"]    = ["title", "overview", "year", "poster_url", "backdrop_url", "runtime_minutes", "rating", "genres", "cast", "directors", "tags"],
    ["tv"]        = ["title", "overview", "year", "poster_url", "backdrop_url", "runtime_minutes", "rating", "genres", "cast", "directors", "tags"],
    ["music"]     = ["title", "overview", "poster_url", "rating", "genres", "tags"],
    ["albums"]    = ["title", "overview", "year", "poster_url", "rating", "genres", "tags"],
    ["tracks"]    = ["title", "year", "runtime_minutes", "tags"],
    ["books"]     = ["title", "overview", "year", "poster_url", "rating", "genres", "tags"],
    ["audiobooks"]= ["title", "overview", "year", "poster_url", "runtime_minutes", "rating", "genres", "tags"],
};
```

**Step 2: Add GET endpoint**

```csharp
[HttpGet("metadata-assignment")]
[Authorize]
public async Task<IActionResult> GetMetadataAssignment()
{
    var setting = await _db.AppSettings.FindAsync("metadata_assignment.config");
    Dictionary<string, Dictionary<string, string[]>> assignments;
    
    if (setting?.Value is not null)
        assignments = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string[]>>>(setting.Value)
                      ?? new();
    else
        assignments = new();

    // Get installed + enabled plugins from registry
    var plugins = _pluginRegistry.GetMetadataProviders()
        .Select(p => new { p.PluginId, p.Name })
        .ToList();

    return Ok(new
    {
        success   = true,
        data      = new
        {
            assignments,
            assignableFields = AssignableFields,
            availablePlugins = plugins,
        },
    });
}
```

**Step 3: Add PUT endpoint**

```csharp
[HttpPut("metadata-assignment")]
[Authorize(Roles = "Admin")]
public async Task<IActionResult> PutMetadataAssignment(
    [FromBody] MetadataAssignmentRequest request)
{
    if (request.Assignments is null)
        return BadRequest(new { success = false, error = new { message = "assignments required" } });

    var json     = JsonSerializer.Serialize(request.Assignments);
    var existing = await _db.AppSettings.FindAsync("metadata_assignment.config");

    if (existing is null)
        _db.AppSettings.Add(new AppSetting { Key = "metadata_assignment.config", Value = json });
    else
        existing.Value = json;

    await _db.SaveChangesAsync();
    return Ok(new { success = true });
}

public class MetadataAssignmentRequest
{
    public Dictionary<string, Dictionary<string, string[]>>? Assignments { get; set; }
}
```

**Step 4: Write a unit or integration test**

```csharp
[Fact]
public async Task MetadataAssignment_RoundTrip_SavesAndReturnsConfig()
{
    var client = factory.CreateClient();
    await AuthenticateAsAdminAsync(client);

    var config = new { assignments = new { movies = new { title = new[] { "tmdb" } } } };

    var putResp = await client.PutAsJsonAsync("/api/v1/settings/metadata-assignment", config);
    Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

    var getResp = await client.GetAsync("/api/v1/settings/metadata-assignment");
    Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);

    var body = await getResp.Content.ReadFromJsonAsync<JsonElement>();
    Assert.Equal("tmdb", body
        .GetProperty("data")
        .GetProperty("assignments")
        .GetProperty("movies")
        .GetProperty("title")[0]
        .GetString());
}
```

**Step 5: Run tests**

```bash
dotnet test tests/Chronicle.Tests.Integration/ --filter "MetadataAssignment" -v n 2>&1 | tail -10
dotnet test tests/Chronicle.Tests.Unit/Chronicle.Tests.Unit.csproj 2>&1 | tail -5
```

**Step 6: Commit**

```bash
git add src/ tests/
git commit -m "feat(settings): add GET/PUT metadata-assignment endpoints"
```

---

### Task 7: Metadata Assignment page (frontend)

**Files:**
- Create: `src/Chronicle.Web/src/pages/settings/MetadataAssignmentPage.tsx`
- Create: `src/Chronicle.Web/src/pages/settings/MetadataAssignmentPage.module.css`
- Modify: `src/Chronicle.Web/src/api/settings.ts` — add assignment API functions
- Modify: `src/Chronicle.Web/src/App.tsx` — add route
- Modify: nav/sidebar to add Settings → Metadata Assignment link (find where other settings links live)

**Step 1: Add API functions to `settings.ts`**

```typescript
export interface MetadataAssignmentConfig {
  assignments: Record<string, Record<string, string[]>>
  assignableFields: Record<string, string[]>
  availablePlugins: { pluginId: string; name: string }[]
}

export async function getMetadataAssignment(): Promise<MetadataAssignmentConfig> {
  const res = await client.get<{ success: true; data: MetadataAssignmentConfig }>('/settings/metadata-assignment')
  return res.data.data
}

export async function putMetadataAssignment(
  assignments: Record<string, Record<string, string[]>>
): Promise<void> {
  await client.put('/settings/metadata-assignment', { assignments })
}
```

**Step 2: Create `MetadataAssignmentPage.tsx`**

```tsx
import { useState, useEffect } from 'react'
import { getMetadataAssignment, putMetadataAssignment, type MetadataAssignmentConfig } from '@/api/settings'
import styles from './MetadataAssignmentPage.module.css'

const FIELD_LABELS: Record<string, string> = {
  title:           'Title',
  overview:        'Description',
  year:            'Year',
  poster_url:      'Poster Image',
  backdrop_url:    'Backdrop Image',
  runtime_minutes: 'Runtime',
  rating:          'Rating',
  genres:          'Genres',
  cast:            'Cast',
  directors:       'Directors',
  tags:            'Tags',
}

export default function MetadataAssignmentPage() {
  const [config, setConfig]       = useState<MetadataAssignmentConfig | null>(null)
  const [assignments, setAssignments] = useState<Record<string, Record<string, string[]>>>({})
  const [saving, setSaving]       = useState(false)
  const [saved, setSaved]         = useState(false)
  const [error, setError]         = useState<string | null>(null)

  useEffect(() => {
    getMetadataAssignment().then(cfg => {
      setConfig(cfg)
      setAssignments(cfg.assignments)
    }).catch(e => setError(String(e)))
  }, [])

  function movePlugin(mediaType: string, field: string, pluginId: string, direction: 'up' | 'down') {
    setAssignments(prev => {
      const list = [...(prev[mediaType]?.[field] ?? [])]
      const idx = list.indexOf(pluginId)
      if (idx === -1) return prev
      const swapIdx = direction === 'up' ? idx - 1 : idx + 1
      if (swapIdx < 0 || swapIdx >= list.length) return prev
      ;[list[idx], list[swapIdx]] = [list[swapIdx], list[idx]]
      return { ...prev, [mediaType]: { ...(prev[mediaType] ?? {}), [field]: list } }
    })
  }

  async function handleSave() {
    setSaving(true)
    setError(null)
    try {
      await putMetadataAssignment(assignments)
      setSaved(true)
      setTimeout(() => setSaved(false), 2000)
    } catch (e) {
      setError(String(e))
    } finally {
      setSaving(false)
    }
  }

  if (!config) return <div className={styles.page}><p>Loading…</p></div>

  const mediaTypes = Object.keys(config.assignableFields)

  return (
    <div className={styles.page}>
      <div className={styles.header}>
        <h1 className={styles.title}>Metadata Assignment</h1>
        <p className={styles.subtitle}>
          Control which plugin's data is used for each field. The first plugin in each list is
          the primary source; the rest are fallbacks in order.
        </p>
        <button className={styles.saveBtn} onClick={handleSave} disabled={saving}>
          {saving ? 'Saving…' : saved ? 'Saved ✓' : 'Save Changes'}
        </button>
        {error && <p className={styles.error}>{error}</p>}
      </div>

      {mediaTypes.map(mediaType => (
        <section key={mediaType} className={styles.section}>
          <div className={styles.sectionHeader}>
            <h2 className={styles.sectionTitle}>{mediaType.charAt(0).toUpperCase() + mediaType.slice(1)}</h2>
          </div>

          <div className={styles.table}>
            <div className={styles.tableHead}>
              <div className={styles.colField}>Field</div>
              <div className={styles.colPlugins}>Plugin Priority</div>
            </div>

            {config.assignableFields[mediaType].map(field => {
              const currentOrder = assignments[mediaType]?.[field]
                ?? config.availablePlugins.map(p => p.pluginId)
              const availablePlugins = config.availablePlugins

              return (
                <div key={field} className={styles.row}>
                  <div className={styles.colField}>
                    {FIELD_LABELS[field] ?? field}
                  </div>

                  <div className={styles.colPlugins}>
                    {currentOrder.map((pluginId, idx) => {
                      const plugin = availablePlugins.find(p => p.pluginId === pluginId)
                      if (!plugin) return null
                      return (
                        <div key={pluginId} className={styles.pluginRow}>
                          <span className={styles.pluginName}>{plugin.name}</span>
                          <div className={styles.arrows}>
                            <button
                              className={styles.arrowBtn}
                              onClick={() => movePlugin(mediaType, field, pluginId, 'up')}
                              disabled={idx === 0}
                              title="Move up"
                            >↑</button>
                            <button
                              className={styles.arrowBtn}
                              onClick={() => movePlugin(mediaType, field, pluginId, 'down')}
                              disabled={idx === currentOrder.length - 1}
                              title="Move down"
                            >↓</button>
                          </div>
                        </div>
                      )
                    })}
                  </div>
                </div>
              )
            })}
          </div>
        </section>
      ))}
    </div>
  )
}
```

**Step 3: Create `MetadataAssignmentPage.module.css`**

```css
.page { padding: 24px; max-width: 900px; }

.header { margin-bottom: 24px; }

.title { font-size: 1.5rem; font-weight: 700; margin-bottom: 6px; }

.subtitle { color: var(--text-muted); margin-bottom: 16px; font-size: 0.9rem; }

.saveBtn {
  padding: 8px 20px;
  background: var(--accent);
  color: #fff;
  border: none;
  border-radius: 6px;
  font-size: 0.9rem;
  cursor: pointer;
  font-weight: 600;
}

.saveBtn:disabled { opacity: 0.6; cursor: default; }

.error { color: #eb5757; margin-top: 8px; font-size: 0.85rem; }

.section {
  margin-bottom: 32px;
  border: 1px solid var(--border);
  border-radius: 8px;
  overflow: hidden;
}

.sectionHeader {
  background: var(--bg-sidebar, var(--bg-card));
  border-bottom: 2px solid var(--accent);
  padding: 12px 16px;
}

.sectionTitle { font-size: 1rem; font-weight: 700; margin: 0; text-transform: uppercase; letter-spacing: 0.05em; }

.table { width: 100%; }

.tableHead {
  display: grid;
  grid-template-columns: 160px 1fr;
  padding: 8px 16px;
  background: var(--bg-card);
  font-size: 0.75rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--text-muted);
  border-bottom: 1px solid var(--border);
}

.row {
  display: grid;
  grid-template-columns: 160px 1fr;
  padding: 10px 16px;
  border-bottom: 1px solid var(--border);
  align-items: center;
}

.row:last-child { border-bottom: none; }

.colField { font-weight: 500; font-size: 0.9rem; }

.colPlugins { display: flex; flex-direction: column; gap: 4px; }

.pluginRow {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 4px 8px;
  background: var(--bg-card);
  border: 1px solid var(--border);
  border-radius: 4px;
}

.pluginName { flex: 1; font-size: 0.85rem; }

.arrows { display: flex; gap: 4px; }

.arrowBtn {
  width: 24px;
  height: 24px;
  border: 1px solid var(--border);
  border-radius: 4px;
  background: transparent;
  cursor: pointer;
  font-size: 0.8rem;
  display: flex;
  align-items: center;
  justify-content: center;
  color: var(--text);
}

.arrowBtn:disabled { opacity: 0.3; cursor: default; }

.arrowBtn:not(:disabled):hover { background: var(--border); }
```

**Step 4: Add route to `App.tsx`**

Find where other settings routes are defined and add:

```tsx
import MetadataAssignmentPage from './pages/settings/MetadataAssignmentPage'
// ...
<Route path="/settings/metadata-assignment" element={<MetadataAssignmentPage />} />
```

**Step 5: Add nav link**

Find where Settings nav links are defined (likely in `Layout.tsx` or a sidebar component). Add:

```tsx
<NavLink to="/settings/metadata-assignment">Metadata Assignment</NavLink>
```

**Step 6: Verify TypeScript and lint**

```bash
cd src/Chronicle.Web && npm run type-check && npm run lint 2>&1 | tail -5
```

**Step 7: Run all unit tests**

```bash
dotnet test tests/Chronicle.Tests.Unit/Chronicle.Tests.Unit.csproj 2>&1 | tail -5
```

**Step 8: Commit**

```bash
git add src/
git commit -m "feat(settings): add Metadata Assignment page with per-field plugin priority"
```

---

## Task 8 — Verify and push

**Step 1: Full unit test run**

```bash
dotnet test tests/Chronicle.Tests.Unit/Chronicle.Tests.Unit.csproj 2>&1 | tail -5
```

**Step 2: Frontend checks**

```bash
cd src/Chronicle.Web && npm run type-check && npm run lint 2>&1 | tail -5
```

**Step 3: Push**

```bash
git push
```
