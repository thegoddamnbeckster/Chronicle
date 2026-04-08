# Enrichment Drill-Down — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Close five small gaps between the existing EnrichmentDrillDownPage and the approved design.

**Architecture:** The page and most backend infrastructure already exist. Changes are additive: extend `ResetScope` with a `Skipped` case, then fix three UI behaviour gaps in `EnrichmentDrillDownPage.tsx`.

**Tech Stack:** C# / ASP.NET Core (backend), React 18 + TypeScript (frontend), existing `enrichment.ts` API client.

---

## Existing file map

| Purpose | Path |
|---|---|
| Enrichment service | `src/Chronicle.Services/MetadataEnrichmentService.cs` |
| Enrichment controller | `src/Chronicle.API/Controllers/EnrichmentController.cs` |
| Reset scope model | `src/Chronicle.Core/Models/` (look for `ResetScope`) |
| API client | `src/Chronicle.Web/src/api/enrichment.ts` |
| Drill-down page | `src/Chronicle.Web/src/pages/settings/EnrichmentDrillDownPage.tsx` |
| Unit tests | `tests/Chronicle.Tests.Unit/` |
| Integration tests | `tests/Chronicle.Tests.Integration/` |

---

## Task 1 — Add `Skipped` to `ResetScope` (backend)

**Files:**
- Modify: wherever `ResetScope` is defined (search for `enum ResetScope`)
- Modify: `src/Chronicle.Services/MetadataEnrichmentService.cs` — `ResetAsync`
- Modify: `src/Chronicle.API/Controllers/EnrichmentController.cs` — reset endpoint

**Step 1: Find `ResetScope`**

```bash
grep -rn "ResetScope" src/
```

**Step 2: Write the failing test**

In the existing enrichment unit test file (search for `ResetAsync` tests), add:

```csharp
[Fact]
public async Task ResetAsync_Skipped_ResetsSkippedRowsToPending()
{
    // Arrange — seed one Skipped enrichment row
    var item = await SeedMediaItemAsync();
    db.MediaEnrichments.Add(new MediaEnrichment
    {
        MediaItemId = item.Id,
        PluginId    = "tmdb",
        Status      = EnrichmentStatus.Skipped,
    });
    await db.SaveChangesAsync();

    // Act
    await service.ResetAsync("tmdb", ResetScope.Skipped);

    // Assert
    var row = await db.MediaEnrichments.SingleAsync(e => e.MediaItemId == item.Id);
    Assert.Equal(EnrichmentStatus.Pending, row.Status);
}
```

**Step 3: Run test to confirm it fails**

```bash
cd tests/Chronicle.Tests.Unit
dotnet test --filter "ResetAsync_Skipped" -v n
```

Expected: compile error — `ResetScope.Skipped` does not exist.

**Step 4: Add `Skipped` to `ResetScope`**

Find the enum and add the value:

```csharp
public enum ResetScope
{
    Single,
    Failed,
    Exhausted,
    NotFound,
    Skipped,   // ← add this
    All,
}
```

**Step 5: Handle `Skipped` in `MetadataEnrichmentService.ResetAsync`**

Find the switch/if block that maps `ResetScope` to a status filter and add:

```csharp
ResetScope.Skipped => EnrichmentStatus.Skipped,
```

Mirror the pattern used by `ResetScope.Failed`.

**Step 6: Accept `"skipped"` in the controller**

Find the reset endpoint's scope mapping (likely a string → ResetScope conversion) and add:

```csharp
"skipped" => ResetScope.Skipped,
```

**Step 7: Run the test**

```bash
dotnet test --filter "ResetAsync_Skipped" -v n
```

Expected: PASS.

**Step 8: Run full test suite**

```bash
cd tests && dotnet test -v n
```

Expected: all passing.

**Step 9: Commit**

```bash
git add src/ tests/
git commit -m "feat(enrichment): add Skipped to ResetScope for bulk reset support"
```

---

## Task 2 — Extend API client for `skipped` scope

**Files:**
- Modify: `src/Chronicle.Web/src/api/enrichment.ts`

**Step 1: Find `resetEnrichment` in `enrichment.ts`**

The current type is likely `'failed' | 'exhausted' | 'notfound' | 'all'`. Add `'skipped'`:

```typescript
export function resetEnrichment(
  pluginId: string,
  scope: 'failed' | 'exhausted' | 'notfound' | 'skipped' | 'all'
): Promise<void> {
  // existing body unchanged
}
```

**Step 2: Verify TypeScript compiles**

```bash
cd src/Chronicle.Web && npm run type-check
```

Expected: no errors.

**Step 3: Commit**

```bash
git add src/Chronicle.Web/src/api/enrichment.ts
git commit -m "feat(enrichment): add skipped scope to resetEnrichment API client"
```

---

## Task 3 — Fix `onChanged` to also refresh stats

**Files:**
- Modify: `src/Chronicle.Web/src/pages/settings/EnrichmentDrillDownPage.tsx`

**Context:** Currently `onChanged` is passed as `load` (line ~406), so per-item actions (skip, reset, fix match) only reload the item list. Tab counts are stale until the 10-second poll fires.

**Step 1: Add a combined `refresh` callback in `EnrichmentDrillDownPage`**

After the `loadStats` definition (around line 306), add:

```typescript
const refresh = useCallback(() => {
  load()
  loadStats()
}, [load, loadStats])
```

**Step 2: Pass `refresh` as `onChanged` to every `ItemCard`**

Find (around line 406):
```tsx
onChanged={load}
```
Replace with:
```tsx
onChanged={refresh}
```

**Step 3: Verify TypeScript compiles**

```bash
cd src/Chronicle.Web && npm run type-check
```

**Step 4: Commit**

```bash
git add src/Chronicle.Web/src/pages/settings/EnrichmentDrillDownPage.tsx
git commit -m "fix(enrichment): refresh tab counts immediately after per-item actions"
```

---

## Task 4 — Fix Match for all statuses and hierarchy levels

**Files:**
- Modify: `src/Chronicle.Web/src/pages/settings/EnrichmentDrillDownPage.tsx`

**Context:** Fix Match is currently blocked when `item.status === 'Skipped'` and when `item.hierarchyLevel > 0` (line ~232). On the drill-down page both restrictions should be lifted — a failed episode should be directly fixable.

**Step 1: Find the Fix Match button condition**

```
item.hierarchyLevel === 0 && item.status !== 'Skipped'
```

**Step 2: Replace with a condition that allows Fix Match for all actionable statuses**

The only status where Fix Match makes no sense is `Pending` (nothing has been attempted yet):

```tsx
{item.status !== 'Pending' && (
  <button
    className={styles.actionBtnPrimary}
    onClick={() => setFixOpen(v => !v)}
  >
    ✎ Fix Match
  </button>
)}
```

**Step 3: Verify TypeScript compiles**

```bash
cd src/Chronicle.Web && npm run type-check
```

**Step 4: Commit**

```bash
git add src/Chronicle.Web/src/pages/settings/EnrichmentDrillDownPage.tsx
git commit -m "fix(enrichment): allow Fix Match for all hierarchy levels and statuses on drill-down page"
```

---

## Task 5 — Add Refresh action for Completed items; allow Skipped bulk reset

**Files:**
- Modify: `src/Chronicle.Web/src/pages/settings/EnrichmentDrillDownPage.tsx`

**Step 1: Add Refresh button for Completed items**

Find the actions section in `ItemCard`. After the existing Reset & Retry button block, add:

```tsx
{item.status === 'Completed' && (
  <button className={styles.actionBtn} onClick={handleReset} disabled={resetting}>
    {resetting ? 'Refreshing…' : '↺ Refresh'}
  </button>
)}
```

The existing `handleReset` calls `resetEnrichmentItem` which resets to Pending — exactly what re-enrichment needs. Only the label changes.

**Step 2: Enable bulk reset for Skipped tab**

Find the bulk reset button condition (around line 384):

```tsx
{activeStatus !== 'All' && activeStatus !== 'Completed' && activeStatus !== 'Skipped' && (
```

Remove `activeStatus !== 'Skipped'`:

```tsx
{activeStatus !== 'All' && activeStatus !== 'Completed' && activeStatus !== 'Pending' && (
```

**Step 3: Handle `skipped` scope in `handleBulkReset`**

Find the scope mapping in `handleBulkReset` (around line 329):

```typescript
const scope =
  activeStatus === 'Failed'    ? 'failed'    :
  activeStatus === 'Exhausted' ? 'exhausted' :
  activeStatus === 'NotFound'  ? 'notfound'  : 'all'
```

Add the skipped case:

```typescript
const scope =
  activeStatus === 'Failed'    ? 'failed'    :
  activeStatus === 'Exhausted' ? 'exhausted' :
  activeStatus === 'NotFound'  ? 'notfound'  :
  activeStatus === 'Skipped'   ? 'skipped'   : 'all'
```

**Step 4: Extend auto-refresh to cover NotFound and Skipped tabs**

Find `isLive` (around line 314):

```typescript
const isLive = activeStatus === 'Pending' || activeStatus === 'Failed' || activeStatus === 'Exhausted' || activeStatus === 'All'
```

Replace with:

```typescript
const isLive = activeStatus !== 'Completed'
```

This enables the 10-second poll on all tabs except Completed (which is stable unless the user explicitly refreshes an item).

**Step 5: Verify TypeScript compiles and lint passes**

```bash
cd src/Chronicle.Web && npm run type-check && npm run lint
```

**Step 6: Commit**

```bash
git add src/Chronicle.Web/src/pages/settings/EnrichmentDrillDownPage.tsx
git commit -m "feat(enrichment): add Refresh for Completed items; enable bulk reset for Skipped"
```

---

## Task 6 — Verify and push

**Step 1: Run full backend test suite**

```bash
cd tests && dotnet test -v n
```

Expected: all passing.

**Step 2: Run frontend checks**

```bash
cd src/Chronicle.Web && npm run type-check && npm run lint
```

Expected: no errors.

**Step 3: Manual smoke test** (if API is running)

1. Navigate to Settings → Background Tasks
2. Click any non-zero status count → confirm drill-down opens
3. On Failed tab: confirm Fix Match appears on all items including episodes; Reset All button visible
4. On Skipped tab: confirm Reset All button visible; clicking resets items to Pending
5. On Completed tab: confirm Refresh button appears per item; no Reset All button
6. Take any per-item action → confirm tab counts update immediately (don't wait 10 seconds)

**Step 4: Push**

```bash
git push
```
