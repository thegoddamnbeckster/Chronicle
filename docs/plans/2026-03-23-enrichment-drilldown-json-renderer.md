# Enrichment Drill-Down & Smart JSON Renderer — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a clickable enrichment drill-down page that shows why each item failed matching, and replace the raw JSON fallback in plugin metadata boxes with a smart recursive tree renderer.

**Architecture:** Two independent features sharing one session. Backend adds `DiagnosticsJson` to enrichment rows (one migration, populated in `EnrichOneAsync`), plus a new paginated items endpoint. Frontend adds `<JsonTree>` (replaces `JSON.stringify` fallback), a new page at `/settings/enrichment/:pluginId`, and turns count cells into links.

**Tech Stack:** .NET 9 / EF Core 9 / SQLite, React 18 / TypeScript / CSS Modules, React Query, React Router v6

---

## Part 1 — Backend: DiagnosticsJson + Items Endpoint

### Task 1: Add DiagnosticsJson to the model

**Files:**
- Modify: `src/Chronicle.Core/Models/MediaItemEnrichmentStatus.cs`

**Step 1: Add the property**

Open `src/Chronicle.Core/Models/MediaItemEnrichmentStatus.cs`. Add one property after `ErrorMessage`:

```csharp
public string? ErrorMessage { get; set; }
/// <summary>
/// JSON blob capturing search diagnostics from the last enrichment attempt.
/// Null for items that have never been attempted or were enriched before this
/// feature was added. Populated by MetadataEnrichmentService.EnrichOneAsync.
/// </summary>
public string? DiagnosticsJson { get; set; }
```

**Step 2: Build to confirm no errors**

```
cd src/Chronicle.API && dotnet build --nologo -v quiet
```
Expected: `Build succeeded. 0 Error(s)`

**Step 3: Commit**

```
git add src/Chronicle.Core/Models/MediaItemEnrichmentStatus.cs
git commit -m "feat(enrichment): add DiagnosticsJson property to MediaItemEnrichmentStatus"
```

---

### Task 2: Add EF Core migration

**Files:**
- Create: `src/Chronicle.Data/Migrations/20260323000000_AddEnrichmentDiagnostics.cs` (generated)

**Step 1: Generate migration**

```
cd src/Chronicle.API && dotnet ef migrations add AddEnrichmentDiagnostics --project ../Chronicle.Data --startup-project . --output-dir ../Chronicle.Data/Migrations
```
Expected: `Build succeeded` and a new migration file is created.

**Step 2: Inspect the generated migration**

Open the generated `.cs` file and verify it contains:
```csharp
migrationBuilder.AddColumn<string>(
    name: "DiagnosticsJson",
    table: "media_item_enrichment_status",
    type: "TEXT",
    nullable: true);
```

**Step 3: Build and confirm**

```
cd src/Chronicle.API && dotnet build --nologo -v quiet
```

**Step 4: Commit**

```
git add src/Chronicle.Data/Migrations/
git commit -m "feat(enrichment): migration AddEnrichmentDiagnostics"
```

---

### Task 3: Populate DiagnosticsJson in EnrichOneAsync

**Files:**
- Modify: `src/Chronicle.Services/MetadataEnrichmentService.cs`

**Context:** `EnrichOneAsync` (lines 159–412) runs one item through one plugin. The `searchQuery` local variable is set before calling `provider.SearchAsync`. The `result` variable holds the search response. We need to capture candidates + scores + failure reason after the search and before `SaveChangesAsync`.

**Step 1: Add diagnostics data classes at the top of the file (private nested records)**

Add these private records just before the closing brace of `MetadataEnrichmentService`:

```csharp
// ── Diagnostics DTOs (serialised to DiagnosticsJson) ─────────────────────

private sealed record EnrichDiagnostics(
    string SearchQuery,
    int CandidatesReturned,
    string FailureReason,
    List<EnrichCandidate> TopCandidates,
    EnrichScannerSignals? ScannerSignals);

private sealed record EnrichCandidate(
    string? Title,
    int? Year,
    string? ExternalId,
    int TitleScore,
    int YearScore,
    int TotalScore);

private sealed record EnrichScannerSignals(
    string? FolderPath,
    bool HasNfo,
    bool HasLocalPoster,
    double? ConfidenceScore);
```

**Step 2: Add a static scoring helper**

Add this private static method before `NormalizeMediaTypeName`:

```csharp
/// <summary>
/// Scores a search candidate against a query name and optional year.
/// Title match: 0–60 pts (exact=60, contains=30). Year exact: 40 pts.
/// </summary>
private static (int title, int year, int total) ScoreCandidate(
    string queryName, int? queryYear, MediaMetadata candidate)
{
    int titleScore = 0;
    var cn = (candidate.Title ?? string.Empty).Trim();
    var qn = queryName.Trim();
    if (string.Equals(cn, qn, StringComparison.OrdinalIgnoreCase))
        titleScore = 60;
    else if (cn.Contains(qn, StringComparison.OrdinalIgnoreCase)
          || qn.Contains(cn, StringComparison.OrdinalIgnoreCase))
        titleScore = 30;

    int yearScore = 0;
    if (queryYear.HasValue && candidate.Year.HasValue && queryYear == candidate.Year)
        yearScore = 40;

    return (titleScore, yearScore, titleScore + yearScore);
}
```

**Step 3: Add a helper that reads scanner signals from metadata_json**

```csharp
private static EnrichScannerSignals? ReadScannerSignals(MediaItem? item)
{
    if (item?.MetadataJson is null) return null;
    try
    {
        using var doc = JsonDocument.Parse(item.MetadataJson);
        if (!doc.RootElement.TryGetProperty("fileScanner", out var fs)) return null;
        string? folder = fs.TryGetProperty("folderPath", out var fp) ? fp.GetString() : null;
        bool hasNfo    = fs.TryGetProperty("nfoPosterUrl", out var npo) && npo.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(npo.GetString());
        bool hasPoster = fs.TryGetProperty("localPosterPath", out var lp) && lp.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(lp.GetString());
        return new EnrichScannerSignals(folder, hasNfo, hasPoster, null);
    }
    catch { return null; }
}
```

**Step 4: Refactor the search call to capture candidates before GetByIdAsync overwrites them**

Find the block in `EnrichOneAsync` starting at `result = await provider.SearchAsync(...)` (around line 358). Replace it and the follow-up `GetByIdAsync` block with:

```csharp
    var searchResult = await provider.SearchAsync(searchQuery, mediaTypeName, ct);

    // Capture candidates for diagnostics BEFORE GetByIdAsync might overwrite result
    var rawCandidates = searchResult?.Results?.Take(5).ToList()
                        ?? (searchResult is not null ? new List<MediaMetadata> { searchResult } : new List<MediaMetadata>());

    result = searchResult;

    // SearchAsync returns only index fields; fetch full entity for poster / extended data.
    if (result is not null && !string.IsNullOrEmpty(result.ExternalId))
    {
        try
        {
            var fullResult = await provider.GetByIdAsync(result.ExternalId, ct);
            if (fullResult is not null && !string.IsNullOrEmpty(fullResult.ExternalId))
                result = fullResult;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Follow-up GetByIdAsync failed for ExternalId={ExternalId}; keeping search result",
                result.ExternalId);
        }
    }
```

**Step 5: Build diagnostics and store them just before `await db.SaveChangesAsync(ct)`**

Find the block just before `await db.SaveChangesAsync(ct)` at line 412 and add:

```csharp
        // ── Capture diagnostics ────────────────────────────────────────────────
        try
        {
            var queryName  = row.MediaItem?.Name ?? string.Empty;
            var queryYear  = row.MediaItem?.Year;
            var candidates = rawCandidates
                .Select(c =>
                {
                    var (ts, ys, tot) = ScoreCandidate(queryName, queryYear, c);
                    return new EnrichCandidate(c.Title, c.Year, c.ExternalId, ts, ys, tot);
                })
                .OrderByDescending(c => c.TotalScore)
                .ToList();

            var failureReason = row.Status switch
            {
                EnrichmentStatus.NotFound  => "No results returned by the provider for this search query.",
                EnrichmentStatus.Failed    => row.ErrorMessage ?? "Provider call threw an exception.",
                EnrichmentStatus.Exhausted => "Maximum retries reached with no successful match.",
                EnrichmentStatus.Completed => "Matched successfully.",
                _ => string.Empty
            };

            var diag = new EnrichDiagnostics(
                searchQuery,
                rawCandidates.Count,
                failureReason,
                candidates,
                ReadScannerSignals(row.MediaItem));

            row.DiagnosticsJson = JsonSerializer.Serialize(diag, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
        catch (Exception diagEx)
        {
            // Diagnostics are best-effort — never let a failure here block enrichment.
            logger.LogWarning(diagEx, "Failed to build enrichment diagnostics for item {ItemId}", row.MediaItemId);
        }

        await db.SaveChangesAsync(ct);
```

Note: `rawCandidates` must be declared before the `result is null` branching block, as shown in Step 4. Ensure the variable is in scope here.

**Step 6: Add `using System.Text.Json;` if not already present at top**

Check line 1 — `using System.Text.Json;` is already there. No change needed.

**Step 7: Build**

```
cd src/Chronicle.API && dotnet build --nologo -v quiet
```
Expected: `0 Error(s)`

**Step 8: Run unit tests**

```
dotnet test tests/Chronicle.Tests.Unit/Chronicle.Tests.Unit.csproj --nologo -v quiet
```
Expected: `Passed! - Failed: 0, Passed: 165`

**Step 9: Commit**

```
git add src/Chronicle.Services/MetadataEnrichmentService.cs
git commit -m "feat(enrichment): persist DiagnosticsJson on every enrichment attempt"
```

---

### Task 4: Add DTOs and the items endpoint

**Files:**
- Modify: `src/Chronicle.API/DTOs/EnrichmentDTOs.cs`
- Modify: `src/Chronicle.Services/IMetadataEnrichmentService.cs`
- Modify: `src/Chronicle.Services/MetadataEnrichmentService.cs`
- Modify: `src/Chronicle.API/Controllers/EnrichmentController.cs`

**Step 1: Add DTOs to `EnrichmentDTOs.cs`**

Append to the existing file:

```csharp
using System.Text.Json;

public record EnrichmentItemDto(
    int EnrichmentId,
    int MediaItemId,
    string Name,
    int? Year,
    string MediaType,
    int HierarchyLevel,
    string? PosterUrl,
    string? ExternalId,
    string Status,
    string? ErrorMessage,
    int RetryCount,
    int MaxRetries,
    string? LastAttemptedAt,
    JsonElement? Diagnostics,
    JsonElement? FileScannerMetadata
);

public record EnrichmentItemsResult(
    List<EnrichmentItemDto> Items,
    int Total,
    int Page,
    int PageSize
);
```

**Step 2: Add `GetItemsAsync` to `IMetadataEnrichmentService.cs`**

Open `src/Chronicle.Services/IMetadataEnrichmentService.cs` and add:

```csharp
Task<EnrichmentItemsResult> GetItemsAsync(
    string pluginId,
    string? status,
    int page,
    int pageSize,
    string? search,
    CancellationToken ct);
```

where `EnrichmentItemsResult` is the new DTO — add a `using Chronicle.API.DTOs;` or define an equivalent model in Services. **Simpler approach:** define a plain service model in `Chronicle.Services`:

Instead, define a service-layer result type in `src/Chronicle.Services/EnrichmentModels.cs` (new file):

```csharp
using Chronicle.Core.Models;

namespace Chronicle.Services;

public record EnrichmentItemResult(
    int EnrichmentId,
    int MediaItemId,
    string Name,
    int? Year,
    string MediaType,
    int HierarchyLevel,
    string? PosterUrl,
    string? ExternalId,
    EnrichmentStatus Status,
    string? ErrorMessage,
    int RetryCount,
    int MaxRetries,
    DateTime? LastAttemptedAt,
    string? DiagnosticsJson,
    string? FileScannerMetadataJson
);

public record PagedEnrichmentItems(
    List<EnrichmentItemResult> Items,
    int Total,
    int Page,
    int PageSize
);
```

Add `GetItemsAsync` to `IMetadataEnrichmentService`:

```csharp
Task<PagedEnrichmentItems> GetItemsAsync(
    string pluginId,
    string? status,
    int page,
    int pageSize,
    string? search,
    CancellationToken ct);
```

**Step 3: Implement `GetItemsAsync` in `MetadataEnrichmentService`**

Add the method:

```csharp
public async Task<PagedEnrichmentItems> GetItemsAsync(
    string pluginId, string? status, int page, int pageSize, string? search,
    CancellationToken ct)
{
    await using var scope = scopeFactory.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

    IQueryable<MediaItemEnrichmentStatus> query = db.EnrichmentStatuses
        .Include(x => x.MediaItem)
            .ThenInclude(m => m!.MediaType)
        .Where(x => x.PluginId == pluginId);

    if (!string.IsNullOrEmpty(status) &&
        Enum.TryParse<EnrichmentStatus>(status, ignoreCase: true, out var parsedStatus))
        query = query.Where(x => x.Status == parsedStatus);

    if (!string.IsNullOrEmpty(search))
        query = query.Where(x => x.MediaItem != null &&
                                 x.MediaItem.Name.Contains(search));

    var total = await query.CountAsync(ct);

    var rows = await query
        .OrderBy(x => x.MediaItem != null ? x.MediaItem.Name : string.Empty)
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .ToListAsync(ct);

    var items = rows.Select(row =>
    {
        // Extract the fileScanner section from metadata_json for the card
        string? scannerJson = null;
        if (row.MediaItem?.MetadataJson is { } mj)
        {
            try
            {
                using var doc = JsonDocument.Parse(mj);
                if (doc.RootElement.TryGetProperty("fileScanner", out var fs))
                    scannerJson = fs.GetRawText();
            }
            catch { /* ignore parse errors */ }
        }

        return new EnrichmentItemResult(
            row.Id,
            row.MediaItemId,
            row.MediaItem?.Name ?? "(unknown)",
            row.MediaItem?.Year,
            row.MediaItem?.MediaType?.DisplayName ?? row.MediaItem?.MediaType?.Name ?? "Unknown",
            row.MediaItem?.HierarchyLevel ?? 0,
            row.MediaItem?.PosterUrl,
            row.ExternalId,
            row.Status,
            row.ErrorMessage,
            row.RetryCount,
            row.MaxRetries,
            row.LastAttemptedAt,
            row.DiagnosticsJson,
            scannerJson);
    }).ToList();

    return new PagedEnrichmentItems(items, total, page, pageSize);
}
```

**Step 4: Add `GET /{pluginId}/items` to `EnrichmentController`**

Add the endpoint:

```csharp
[HttpGet("{pluginId}/items")]
public async Task<IActionResult> GetItems(
    string pluginId,
    [FromQuery] string? status,
    [FromQuery] int page = 1,
    [FromQuery] int pageSize = 50,
    [FromQuery] string? search = null,
    CancellationToken ct = default)
{
    if (page < 1) page = 1;
    if (pageSize is < 1 or > 200) pageSize = 50;

    var result = await enrichmentSvc.GetItemsAsync(pluginId, status, page, pageSize, search, ct);

    var items = result.Items.Select(r =>
    {
        JsonElement? diag = null;
        if (r.DiagnosticsJson is not null)
        {
            try { diag = JsonSerializer.Deserialize<JsonElement>(r.DiagnosticsJson); }
            catch { /* ignore */ }
        }

        JsonElement? scanner = null;
        if (r.FileScannerMetadataJson is not null)
        {
            try { scanner = JsonSerializer.Deserialize<JsonElement>(r.FileScannerMetadataJson); }
            catch { /* ignore */ }
        }

        return new
        {
            enrichmentId    = r.EnrichmentId,
            mediaItemId     = r.MediaItemId,
            name            = r.Name,
            year            = r.Year,
            mediaType       = r.MediaType,
            hierarchyLevel  = r.HierarchyLevel,
            posterUrl       = r.PosterUrl,
            externalId      = r.ExternalId,
            status          = r.Status.ToString(),
            errorMessage    = r.ErrorMessage,
            retryCount      = r.RetryCount,
            maxRetries      = r.MaxRetries,
            lastAttemptedAt = r.LastAttemptedAt,
            diagnostics     = diag,
            fileScannerMetadata = scanner,
        };
    });

    return Ok(new
    {
        success = true,
        data = new
        {
            items,
            total     = result.Total,
            page      = result.Page,
            pageSize  = result.PageSize,
            totalPages = (int)Math.Ceiling(result.Total / (double)result.PageSize),
        }
    });
}
```

Add `using System.Text.Json;` to controller usings if not present.

**Step 5: Build**

```
cd src/Chronicle.API && dotnet build --nologo -v quiet
```
Expected: `0 Error(s)`

**Step 6: Run all tests**

```
dotnet test tests/Chronicle.Tests.Unit/Chronicle.Tests.Unit.csproj --nologo -v quiet
dotnet test tests/Chronicle.Tests.Integration/Chronicle.Tests.Integration.csproj --nologo -v quiet
```
Expected: `Passed! 165` + `Passed! 100`

**Step 7: Commit**

```
git add src/Chronicle.Services/EnrichmentModels.cs \
        src/Chronicle.Services/IMetadataEnrichmentService.cs \
        src/Chronicle.Services/MetadataEnrichmentService.cs \
        src/Chronicle.API/DTOs/EnrichmentDTOs.cs \
        src/Chronicle.API/Controllers/EnrichmentController.cs
git commit -m "feat(enrichment): GET /enrichment/{pluginId}/items paginated endpoint"
```

---

## Part 2 — Frontend: Enrichment Drill-Down Page

### Task 5: Add API client function

**Files:**
- Modify: `src/Chronicle.Web/src/api/enrichment.ts`

**Step 1: Add types and function**

Append to `enrichment.ts`:

```typescript
export interface EnrichmentCandidate {
  title: string | null
  year: number | null
  externalId: string | null
  titleScore: number
  yearScore: number
  totalScore: number
}

export interface EnrichmentDiagnostics {
  searchQuery: string
  candidatesReturned: number
  failureReason: string
  topCandidates: EnrichmentCandidate[]
  scannerSignals: {
    folderPath: string | null
    hasNfo: boolean
    hasLocalPoster: boolean
    confidenceScore: number | null
  } | null
}

export interface EnrichmentItem {
  enrichmentId: number
  mediaItemId: number
  name: string
  year: number | null
  mediaType: string
  hierarchyLevel: number
  posterUrl: string | null
  externalId: string | null
  status: string
  errorMessage: string | null
  retryCount: number
  maxRetries: number
  lastAttemptedAt: string | null
  diagnostics: EnrichmentDiagnostics | null
  fileScannerMetadata: Record<string, unknown> | null
}

export interface EnrichmentItemsPage {
  items: EnrichmentItem[]
  total: number
  page: number
  pageSize: number
  totalPages: number
}

export async function getEnrichmentItems(
  pluginId: string,
  status?: string,
  page = 1,
  pageSize = 50,
  search?: string,
): Promise<EnrichmentItemsPage> {
  const params: Record<string, string | number> = { page, pageSize }
  if (status) params.status = status
  if (search) params.search = search
  const { data } = await client.get(
    `/enrichment/${encodeURIComponent(pluginId)}/items`,
    { params },
  )
  return data.data
}

export async function resetEnrichmentItem(
  pluginId: string,
  mediaItemId: number,
): Promise<void> {
  await client.post(`/enrichment/${encodeURIComponent(pluginId)}/reset`, {
    scope: 'single',
    mediaItemId,
  })
}

export async function skipEnrichmentItem(
  pluginId: string,
  mediaItemId: number,
): Promise<void> {
  await client.post(
    `/enrichment/${encodeURIComponent(pluginId)}/items/${mediaItemId}/skip`,
  )
}
```

**Step 2: Type-check**

```
cd src/Chronicle.Web && npm run type-check
```
Expected: no errors.

**Step 3: Commit**

```
git add src/Chronicle.Web/src/api/enrichment.ts
git commit -m "feat(enrichment): add getEnrichmentItems / reset / skip API client functions"
```

---

### Task 6: Create the drill-down page

**Files:**
- Create: `src/Chronicle.Web/src/pages/settings/EnrichmentDrillDownPage.tsx`
- Create: `src/Chronicle.Web/src/pages/settings/EnrichmentDrillDownPage.module.css`

**Step 1: Create the CSS file**

`src/Chronicle.Web/src/pages/settings/EnrichmentDrillDownPage.module.css`:

```css
.page {
  padding: 24px;
  max-width: 980px;
}

.header {
  display: flex;
  align-items: center;
  gap: 16px;
  margin-bottom: 24px;
}

.backLink {
  font-size: 0.85rem;
  color: var(--text-muted);
  text-decoration: none;
}
.backLink:hover { color: var(--text); }

.title {
  font-size: 1.5rem;
  font-weight: 600;
  margin: 0;
  flex: 1;
}

/* ── Status tabs ───────────────────────────────────────────── */

.tabs {
  display: flex;
  gap: 4px;
  flex-wrap: wrap;
  margin-bottom: 16px;
}

.tab {
  padding: 5px 14px;
  border-radius: 4px;
  border: 1px solid var(--border);
  background: transparent;
  color: var(--text-muted);
  font-size: 0.83rem;
  cursor: pointer;
  white-space: nowrap;
}
.tab:hover { background: var(--bg-hover); color: var(--text); }
.tabActive {
  background: var(--accent);
  border-color: var(--accent);
  color: var(--accent-fg);
}

/* ── Toolbar ───────────────────────────────────────────────── */

.toolbar {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 20px;
  flex-wrap: wrap;
}

.searchInput {
  flex: 1;
  min-width: 200px;
  padding: 7px 12px;
  border-radius: 4px;
  border: 1px solid var(--border);
  background: var(--bg-input);
  color: var(--text);
  font-size: 0.88rem;
}

.bulkBtn {
  padding: 6px 14px;
  border-radius: 4px;
  border: 1px solid var(--border);
  background: transparent;
  color: var(--text-muted);
  font-size: 0.83rem;
  cursor: pointer;
}
.bulkBtn:hover { background: var(--bg-hover); color: var(--text); }
.bulkBtn:disabled { opacity: 0.4; cursor: not-allowed; }

/* ── Item cards ────────────────────────────────────────────── */

.cards {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.card {
  background: var(--bg-card);
  border: 1px solid var(--border);
  border-radius: 6px;
  padding: 16px;
  display: grid;
  grid-template-columns: 70px 1fr;
  gap: 16px;
}

.poster {
  width: 70px;
  height: 100px;
  object-fit: cover;
  border-radius: 3px;
  background: var(--bg-secondary);
  flex-shrink: 0;
}

.posterPlaceholder {
  width: 70px;
  height: 100px;
  border-radius: 3px;
  background: var(--bg-secondary);
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 1.5rem;
  color: var(--text-muted);
}

.body {
  display: flex;
  flex-direction: column;
  gap: 10px;
  min-width: 0;
}

.itemName {
  font-size: 1.05rem;
  font-weight: 600;
  margin: 0;
}

.itemMeta {
  font-size: 0.8rem;
  color: var(--text-muted);
}

/* ── Diagnostic sections ───────────────────────────────────── */

.diagSection {
  border-top: 1px solid var(--border);
  padding-top: 10px;
}

.diagTitle {
  font-size: 0.7rem;
  font-weight: 700;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--text-muted);
  margin: 0 0 8px;
}

.signalGrid {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.signal {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  font-size: 0.8rem;
  padding: 3px 8px;
  border-radius: 3px;
  background: var(--bg-secondary);
}

.signalYes { color: #6fcf97; }
.signalNo  { color: var(--text-muted); }

.searchLine {
  font-size: 0.83rem;
  margin: 0 0 6px;
}

.searchQuery {
  font-family: monospace;
  background: var(--bg-secondary);
  padding: 1px 5px;
  border-radius: 3px;
}

.failureReason {
  font-size: 0.83rem;
  color: #eb5757;
  margin: 4px 0 0;
}

/* Candidate score rows */
.candidates {
  display: flex;
  flex-direction: column;
  gap: 4px;
  margin-top: 6px;
}

.candidate {
  display: flex;
  align-items: center;
  gap: 10px;
  font-size: 0.82rem;
  padding: 4px 8px;
  border-radius: 3px;
  background: var(--bg-secondary);
}

.candidateName { flex: 1; font-weight: 500; }

.scoreBar {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 0.75rem;
  color: var(--text-muted);
}

.scorePill {
  padding: 1px 7px;
  border-radius: 10px;
  font-size: 0.72rem;
  font-weight: 600;
}
.scoreHigh   { background: #1a4d2a; color: #6fcf97; }
.scoreMedium { background: #4d3a1a; color: #f2c94c; }
.scoreLow    { background: var(--bg-secondary); color: var(--text-muted); }

.errorMsg {
  font-size: 0.82rem;
  color: #eb5757;
  background: rgba(235, 87, 87, 0.08);
  border-left: 3px solid #eb5757;
  border-radius: 3px;
  padding: 6px 10px;
  white-space: pre-wrap;
  word-break: break-word;
}

/* ── Card actions ──────────────────────────────────────────── */

.actions {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
  margin-top: 4px;
}

.actionBtn {
  padding: 5px 12px;
  border-radius: 4px;
  border: 1px solid var(--border);
  background: transparent;
  color: var(--text);
  font-size: 0.82rem;
  cursor: pointer;
}
.actionBtn:hover { background: var(--bg-hover); }
.actionBtn:disabled { opacity: 0.4; cursor: not-allowed; }

.actionBtnPrimary {
  padding: 5px 12px;
  border-radius: 4px;
  border: none;
  background: var(--accent);
  color: var(--accent-fg);
  font-size: 0.82rem;
  cursor: pointer;
}
.actionBtnPrimary:disabled { opacity: 0.4; cursor: not-allowed; }

/* ── Pagination ────────────────────────────────────────────── */

.pagination {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 12px;
  padding: 24px 0 8px;
}

.pageBtn {
  padding: 5px 14px;
  border-radius: 4px;
  border: 1px solid var(--border);
  background: transparent;
  color: var(--text);
  font-size: 0.85rem;
  cursor: pointer;
}
.pageBtn:hover:not(:disabled) { background: var(--bg-hover); }
.pageBtn:disabled { opacity: 0.4; cursor: not-allowed; }

.pageInfo {
  font-size: 0.85rem;
  color: var(--text-muted);
}

.empty {
  color: var(--text-muted);
  padding: 40px 0;
  text-align: center;
}

.loading {
  color: var(--text-muted);
  padding: 40px 0;
  text-align: center;
}

.totalCount {
  font-size: 0.83rem;
  color: var(--text-muted);
  margin-left: auto;
}
```

**Step 2: Create the page component**

`src/Chronicle.Web/src/pages/settings/EnrichmentDrillDownPage.tsx`:

```typescript
import { useState, useEffect, useCallback, useRef } from 'react'
import { Link, useParams, useSearchParams, useNavigate } from 'react-router-dom'
import {
  getEnrichmentItems,
  getEnrichmentStats,
  resetEnrichmentItem,
  skipEnrichmentItem,
  resetEnrichment,
  type EnrichmentItem,
  type EnrichmentStats,
} from '@/api/enrichment'
import { refreshMediaForPlugin } from '@/api/media'
import styles from './EnrichmentDrillDownPage.module.css'

// ── Helpers ──────────────────────────────────────────────────────────────────

const STATUS_LABELS: Record<string, string> = {
  All: 'All',
  Pending: 'Pending',
  Completed: 'Completed',
  Failed: 'Failed',
  Exhausted: 'Exhausted',
  NotFound: 'Not Found',
  Skipped: 'Skipped',
}

function scoreClass(total: number): string {
  if (total >= 80) return styles.scoreHigh
  if (total >= 50) return styles.scoreMedium
  return styles.scoreLow
}

function fmtDate(iso: string | null): string {
  if (!iso) return 'Never'
  return new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
}

// ── Item card ─────────────────────────────────────────────────────────────────

interface ItemCardProps {
  item: EnrichmentItem
  pluginId: string
  onChanged: () => void
}

function ItemCard({ item, pluginId, onChanged }: ItemCardProps) {
  const [resetting, setResetting] = useState(false)
  const [skipping, setSkipping]   = useState(false)
  const [fixing, setFixing]       = useState(false)
  const [fixInput, setFixInput]   = useState('')
  const [fixOpen, setFixOpen]     = useState(false)
  const navigate = useNavigate()

  const diag = item.diagnostics
  const scanner = item.fileScannerMetadata

  async function handleReset() {
    setResetting(true)
    try { await resetEnrichmentItem(pluginId, item.mediaItemId); onChanged() }
    finally { setResetting(false) }
  }

  async function handleSkip() {
    setSkipping(true)
    try { await skipEnrichmentItem(pluginId, item.mediaItemId); onChanged() }
    finally { setSkipping(false) }
  }

  async function handleFixMatch() {
    if (!fixInput.trim()) return
    setFixing(true)
    try {
      await refreshMediaForPlugin(item.mediaItemId, pluginId, fixInput.trim())
      setFixOpen(false)
      setFixInput('')
      onChanged()
    } finally { setFixing(false) }
  }

  return (
    <div className={styles.card}>
      {/* Poster */}
      {item.posterUrl
        ? <img src={item.posterUrl} alt={item.name} className={styles.poster} />
        : <div className={styles.posterPlaceholder}>🎬</div>
      }

      <div className={styles.body}>
        {/* Title row */}
        <div>
          <h3 className={styles.itemName}>
            {item.name}{item.year ? ` (${item.year})` : ''}
          </h3>
          <p className={styles.itemMeta}>
            {item.mediaType}
            {item.hierarchyLevel > 0 ? ` · Level ${item.hierarchyLevel}` : ''}
            {item.externalId ? ` · ${item.externalId}` : ''}
            {' · '}Status: <strong>{STATUS_LABELS[item.status] ?? item.status}</strong>
            {' · '}Last attempt: {fmtDate(item.lastAttemptedAt)}
            {item.retryCount > 0 ? ` · Retries: ${item.retryCount}/${item.maxRetries}` : ''}
          </p>
        </div>

        {/* Scanner signals */}
        {(diag?.scannerSignals || scanner) && (
          <div className={styles.diagSection}>
            <p className={styles.diagTitle}>Scanner Signals</p>
            <div className={styles.signalGrid}>
              {scanner?.folderPath && (
                <span className={styles.signal}>
                  <span className={styles.signalYes}>✓</span>
                  Folder: <em>{String(scanner.folderPath)}</em>
                </span>
              )}
              {diag?.scannerSignals && (
                <>
                  <span className={styles.signal}>
                    <span className={diag.scannerSignals.hasNfo ? styles.signalYes : styles.signalNo}>
                      {diag.scannerSignals.hasNfo ? '✓' : '✗'}
                    </span>
                    NFO sidecar
                  </span>
                  <span className={styles.signal}>
                    <span className={diag.scannerSignals.hasLocalPoster ? styles.signalYes : styles.signalNo}>
                      {diag.scannerSignals.hasLocalPoster ? '✓' : '✗'}
                    </span>
                    Local poster
                  </span>
                </>
              )}
              {scanner?.filePaths && Array.isArray(scanner.filePaths) && (
                <span className={styles.signal}>
                  <span className={styles.signalYes}>✓</span>
                  {(scanner.filePaths as string[]).length} file(s)
                </span>
              )}
            </div>
          </div>
        )}

        {/* Enrichment diagnostics */}
        {diag && (
          <div className={styles.diagSection}>
            <p className={styles.diagTitle}>Enrichment Diagnostics</p>
            {diag.searchQuery && (
              <p className={styles.searchLine}>
                Searched: <code className={styles.searchQuery}>{diag.searchQuery}</code>
                {' — '}{diag.candidatesReturned} candidate(s) returned
              </p>
            )}
            {diag.failureReason && item.status !== 'Completed' && (
              <p className={styles.failureReason}>{diag.failureReason}</p>
            )}
            {diag.topCandidates.length > 0 && (
              <div className={styles.candidates}>
                <p className={styles.diagTitle}>Top Candidates</p>
                {diag.topCandidates.map((c, i) => (
                  <div key={i} className={styles.candidate}>
                    <span className={styles.candidateName}>
                      {c.title ?? '(no title)'}{c.year ? ` (${c.year})` : ''}
                    </span>
                    {c.externalId && (
                      <code style={{ fontSize: '0.75rem', color: 'var(--text-muted)' }}>
                        {c.externalId}
                      </code>
                    )}
                    <div className={styles.scoreBar}>
                      <span>title {c.titleScore}pt</span>
                      <span>year {c.yearScore}pt</span>
                      <span className={`${styles.scorePill} ${scoreClass(c.totalScore)}`}>
                        {c.totalScore}/100
                      </span>
                    </div>
                  </div>
                ))}
              </div>
            )}
          </div>
        )}

        {/* Error message */}
        {item.errorMessage && (
          <div className={styles.diagSection}>
            <p className={styles.diagTitle}>Error</p>
            <div className={styles.errorMsg}>{item.errorMessage}</div>
          </div>
        )}

        {/* Fix Match inline panel */}
        {fixOpen && (
          <div className={styles.diagSection}>
            <p className={styles.diagTitle}>Fix Match — enter an ID or URL</p>
            <div style={{ display: 'flex', gap: 8 }}>
              <input
                type="text"
                autoFocus
                style={{ flex: 1, padding: '5px 9px', borderRadius: 4, border: '1px solid var(--border)', background: 'var(--bg-input)', color: 'var(--text)', fontSize: '0.88rem' }}
                placeholder="e.g. movie:550 or https://..."
                value={fixInput}
                onChange={e => setFixInput(e.target.value)}
                onKeyDown={e => {
                  if (e.key === 'Enter' && fixInput.trim()) handleFixMatch()
                  if (e.key === 'Escape') { setFixOpen(false); setFixInput('') }
                }}
              />
              <button
                className={styles.actionBtnPrimary}
                onClick={handleFixMatch}
                disabled={fixing || !fixInput.trim()}
              >
                {fixing ? 'Applying…' : 'Apply'}
              </button>
            </div>
          </div>
        )}

        {/* Actions */}
        <div className={styles.actions}>
          {item.hierarchyLevel === 0 && item.status !== 'Skipped' && (
            <button
              className={styles.actionBtnPrimary}
              onClick={() => setFixOpen(v => !v)}
            >
              ✎ Fix Match
            </button>
          )}
          {item.status !== 'Skipped' && (
            <button className={styles.actionBtn} onClick={handleSkip} disabled={skipping}>
              {skipping ? 'Skipping…' : '⊘ Skip'}
            </button>
          )}
          {item.status !== 'Completed' && item.status !== 'Pending' && (
            <button className={styles.actionBtn} onClick={handleReset} disabled={resetting}>
              {resetting ? 'Resetting…' : '↺ Reset & Retry'}
            </button>
          )}
          <button
            className={styles.actionBtn}
            onClick={() => navigate(`/media/${item.mediaItemId}`)}
          >
            View in Library →
          </button>
        </div>
      </div>
    </div>
  )
}

// ── Main page ─────────────────────────────────────────────────────────────────

export default function EnrichmentDrillDownPage() {
  const { pluginId = '' } = useParams<{ pluginId: string }>()
  const [searchParams, setSearchParams] = useSearchParams()

  const activeStatus = searchParams.get('status') ?? 'All'
  const [search, setSearch]     = useState('')
  const [debouncedSearch, setDebouncedSearch] = useState('')
  const [page, setPage]         = useState(1)
  const [loading, setLoading]   = useState(true)
  const [items, setItems]       = useState<EnrichmentItem[]>([])
  const [total, setTotal]       = useState(0)
  const [totalPages, setTotalPages] = useState(1)
  const [stats, setStats]       = useState<EnrichmentStats | null>(null)
  const [bulkWorking, setBulkWorking] = useState(false)
  const debounceRef = useRef<ReturnType<typeof setTimeout>>()

  // Debounce search input
  useEffect(() => {
    clearTimeout(debounceRef.current)
    debounceRef.current = setTimeout(() => {
      setDebouncedSearch(search)
      setPage(1)
    }, 300)
    return () => clearTimeout(debounceRef.current)
  }, [search])

  const load = useCallback(async () => {
    setLoading(true)
    try {
      const statusParam = activeStatus === 'All' ? undefined : activeStatus
      const res = await getEnrichmentItems(pluginId, statusParam, page, 25, debouncedSearch || undefined)
      setItems(res.items)
      setTotal(res.total)
      setTotalPages(res.totalPages)
    } finally {
      setLoading(false)
    }
  }, [pluginId, activeStatus, page, debouncedSearch])

  // Load plugin stats for tab counts
  useEffect(() => {
    getEnrichmentStats().then(all => {
      const s = all.find(s => s.pluginId === pluginId)
      setStats(s ?? null)
    }).catch(() => {})
  }, [pluginId])

  useEffect(() => { load() }, [load])

  function setStatus(s: string) {
    setSearchParams(s === 'All' ? {} : { status: s })
    setPage(1)
  }

  async function handleBulkReset() {
    setBulkWorking(true)
    try {
      const scope = activeStatus === 'Exhausted' ? 'exhausted' : 'all'
      await resetEnrichment(pluginId, scope)
      await load()
    } finally { setBulkWorking(false) }
  }

  const statusCounts = stats ? {
    All: stats.pending + stats.completed + stats.failed + stats.exhausted + stats.notFound + stats.skipped,
    Pending: stats.pending,
    Completed: stats.completed,
    Failed: stats.failed,
    Exhausted: stats.exhausted,
    NotFound: stats.notFound,
    Skipped: stats.skipped,
  } : {} as Record<string, number>

  const pluginDisplayName = stats?.pluginName ?? pluginId

  return (
    <div className={styles.page}>
      <div className={styles.header}>
        <Link to="/settings/background-tasks" className={styles.backLink}>
          ← Background Tasks
        </Link>
        <h1 className={styles.title}>Enrichment — {pluginDisplayName}</h1>
      </div>

      {/* Status tabs */}
      <div className={styles.tabs}>
        {Object.entries(STATUS_LABELS).map(([key, label]) => (
          <button
            key={key}
            className={`${styles.tab} ${activeStatus === key ? styles.tabActive : ''}`}
            onClick={() => setStatus(key)}
          >
            {label}{statusCounts[key] != null ? ` (${statusCounts[key]})` : ''}
          </button>
        ))}
      </div>

      {/* Toolbar */}
      <div className={styles.toolbar}>
        <input
          type="text"
          className={styles.searchInput}
          placeholder="Search by name…"
          value={search}
          onChange={e => setSearch(e.target.value)}
        />
        <span className={styles.totalCount}>{total} item(s)</span>
        {activeStatus !== 'All' && activeStatus !== 'Completed' && activeStatus !== 'Skipped' && (
          <button
            className={styles.bulkBtn}
            onClick={handleBulkReset}
            disabled={bulkWorking || total === 0}
          >
            {bulkWorking ? 'Resetting…' : `↺ Reset All ${STATUS_LABELS[activeStatus] ?? activeStatus}`}
          </button>
        )}
      </div>

      {/* Cards */}
      {loading ? (
        <p className={styles.loading}>Loading…</p>
      ) : items.length === 0 ? (
        <p className={styles.empty}>No items in this category.</p>
      ) : (
        <div className={styles.cards}>
          {items.map(item => (
            <ItemCard
              key={item.enrichmentId}
              item={item}
              pluginId={pluginId}
              onChanged={load}
            />
          ))}
        </div>
      )}

      {/* Pagination */}
      {totalPages > 1 && (
        <div className={styles.pagination}>
          <button className={styles.pageBtn} disabled={page <= 1} onClick={() => setPage(p => p - 1)}>
            ← Prev
          </button>
          <span className={styles.pageInfo}>Page {page} of {totalPages}</span>
          <button className={styles.pageBtn} disabled={page >= totalPages} onClick={() => setPage(p => p + 1)}>
            Next →
          </button>
        </div>
      )}
    </div>
  )
}
```

**Step 3: Type-check**

```
cd src/Chronicle.Web && npm run type-check
```
Expected: no errors.

---

### Task 7: Register route and wire up links

**Files:**
- Modify: `src/Chronicle.Web/src/App.tsx`
- Modify: `src/Chronicle.Web/src/pages/settings/BackgroundTasksPage.tsx`

**Step 1: Add route to `App.tsx`**

Add the import near the other settings imports:

```typescript
import EnrichmentDrillDownPage from '@/pages/settings/EnrichmentDrillDownPage'
```

Add the route inside the authenticated layout `<Route>` block, after the `background-tasks` route:

```tsx
<Route path="settings/enrichment/:pluginId" element={<EnrichmentDrillDownPage />} />
```

**Step 2: Make count cells clickable in `BackgroundTasksPage.tsx`**

Add this import at the top:

```typescript
import { Link } from 'react-router-dom'
```

In the `EnrichmentSection` component, find the `<tbody>` rows and replace the count cells. Change:

```tsx
<td className={styles.enrichTd}>{s.pending}</td>
<td className={styles.enrichTd}>{s.completed}</td>
<td className={styles.enrichTd}>{s.failed}</td>
<td className={styles.enrichTd}>{s.exhausted}</td>
<td className={styles.enrichTd}>{s.notFound}</td>
<td className={styles.enrichTd}>{s.skipped}</td>
```

to:

```tsx
{(['pending','completed','failed','exhausted','notFound','skipped'] as const).map(field => {
  const statusMap: Record<string, string> = {
    pending: 'Pending', completed: 'Completed', failed: 'Failed',
    exhausted: 'Exhausted', notFound: 'NotFound', skipped: 'Skipped',
  }
  const count = s[field]
  return (
    <td key={field} className={styles.enrichTd}>
      {count > 0 ? (
        <Link
          to={`/settings/enrichment/${encodeURIComponent(s.pluginId)}?status=${statusMap[field]}`}
          style={{ color: 'var(--accent)', textDecoration: 'none', fontWeight: 600 }}
        >
          {count}
        </Link>
      ) : (
        <span style={{ color: 'var(--text-muted)' }}>0</span>
      )}
    </td>
  )
})}
```

**Step 3: Type-check and lint**

```
cd src/Chronicle.Web && npm run type-check && npm run lint
```
Expected: no errors.

**Step 4: Commit**

```
git add src/Chronicle.Web/src/pages/settings/EnrichmentDrillDownPage.tsx \
        src/Chronicle.Web/src/pages/settings/EnrichmentDrillDownPage.module.css \
        src/Chronicle.Web/src/App.tsx \
        src/Chronicle.Web/src/pages/settings/BackgroundTasksPage.tsx
git commit -m "feat(enrichment): drill-down page with diagnostic cards, status tabs, pagination"
```

---

## Part 3 — Smart JSON Renderer

### Task 8: Create `<JsonTree>` component

**Files:**
- Create: `src/Chronicle.Web/src/components/JsonTree.tsx`
- Create: `src/Chronicle.Web/src/components/JsonTree.module.css`

**Step 1: Create the CSS**

`src/Chronicle.Web/src/components/JsonTree.module.css`:

```css
.tree {
  display: flex;
  flex-direction: column;
  gap: 3px;
  font-size: 0.82rem;
}

.node {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.row {
  display: flex;
  align-items: flex-start;
  gap: 6px;
  min-width: 0;
}

.key {
  color: var(--text-muted);
  white-space: nowrap;
  flex-shrink: 0;
  font-size: 0.78rem;
}

.keyId {
  font-family: monospace;
  color: var(--text-muted);
}

.collapseBtn {
  background: none;
  border: none;
  padding: 0 2px;
  cursor: pointer;
  color: var(--text-muted);
  font-size: 0.75rem;
  flex-shrink: 0;
  line-height: 1;
}
.collapseBtn:hover { color: var(--text); }

.children {
  padding-left: 16px;
  border-left: 1px solid var(--border);
  margin-left: 4px;
  display: flex;
  flex-direction: column;
  gap: 3px;
}

/* ── Leaf value types ─────────────────────── */

.valueStr { color: var(--text); word-break: break-word; }
.valueNull { color: var(--text-muted); font-style: italic; }
.valueNum  { color: var(--text); }

.idBadge {
  font-family: monospace;
  font-size: 0.78rem;
  padding: 1px 6px;
  border-radius: 3px;
  background: var(--bg-secondary);
  color: var(--text-muted);
  word-break: break-all;
}

.boolTrue {
  padding: 1px 7px;
  border-radius: 3px;
  font-size: 0.75rem;
  font-weight: 600;
  background: rgba(111, 207, 151, 0.12);
  color: #6fcf97;
}

.boolFalse {
  padding: 1px 7px;
  border-radius: 3px;
  font-size: 0.75rem;
  font-weight: 600;
  background: var(--bg-secondary);
  color: var(--text-muted);
}

.link {
  color: var(--accent);
  text-decoration: none;
  word-break: break-all;
}
.link:hover { text-decoration: underline; }

.thumbnail {
  max-width: 130px;
  max-height: 130px;
  width: auto;
  height: auto;
  object-fit: contain;
  border-radius: 3px;
  cursor: zoom-in;
  display: block;
  margin-top: 2px;
}

.inlineList {
  color: var(--text);
  word-break: break-word;
}

.arrayIndex {
  font-size: 0.72rem;
  color: var(--text-muted);
  margin-right: 4px;
}

.sectionLabel {
  font-size: 0.72rem;
  font-weight: 600;
  letter-spacing: 0.05em;
  text-transform: uppercase;
  color: var(--text-muted);
  margin-bottom: 2px;
}
```

**Step 2: Create the component**

`src/Chronicle.Web/src/components/JsonTree.tsx`:

```typescript
import { useState } from 'react'
import { isImageUrl, toLabel } from '@/utils/imageExtractor'
import styles from './JsonTree.module.css'

// ── Helpers ──────────────────────────────────────────────────────────────────

/** Keys whose name signals an identifier value (rendered as monospace badge). */
function isIdKey(key: string): boolean {
  const k = key.toLowerCase()
  return k === 'id' || k === 'mbid' || k === 'uuid' || k === 'gid'
    || k.endsWith('id') || k.endsWith('_id') || k.endsWith('uuid')
    || k.endsWith('mbid')
}

function isUrl(value: unknown): value is string {
  return typeof value === 'string' &&
    (value.startsWith('http://') || value.startsWith('https://'))
}

/**
 * Decide whether an array of objects should start collapsed.
 * Collapse if it has more than 3 items OR any item has more than 4 keys.
 */
function shouldCollapseArray(arr: unknown[]): boolean {
  if (arr.length > 3) return true
  return arr.some(item => typeof item === 'object' && item !== null && Object.keys(item).length > 4)
}

function shouldCollapseObject(obj: Record<string, unknown>): boolean {
  return Object.keys(obj).length > 3
}

// ── Component ────────────────────────────────────────────────────────────────

export interface JsonTreeProps {
  data: unknown
  depth?: number
  /** Route image clicks to the page-level lightbox. */
  onImageClick?: (url: string) => void
}

export function JsonTree({ data, depth = 0, onImageClick }: JsonTreeProps) {
  if (data === null || data === undefined) {
    return <span className={styles.valueNull}>—</span>
  }

  if (typeof data === 'boolean') {
    return (
      <span className={data ? styles.boolTrue : styles.boolFalse}>
        {data ? 'Yes' : 'No'}
      </span>
    )
  }

  if (typeof data === 'number') {
    return <span className={styles.valueNum}>{data}</span>
  }

  if (typeof data === 'string') {
    if (isImageUrl(data)) {
      return (
        <img
          src={data}
          alt=""
          className={styles.thumbnail}
          onClick={() => onImageClick ? onImageClick(data) : window.open(data, '_blank')}
          onError={e => { e.currentTarget.style.display = 'none' }}
        />
      )
    }
    if (isUrl(data)) {
      return (
        <a href={data} target="_blank" rel="noopener noreferrer" className={styles.link}>
          {data}
        </a>
      )
    }
    return <span className={styles.valueStr}>{data}</span>
  }

  if (Array.isArray(data)) {
    return <JsonArray arr={data} depth={depth} onImageClick={onImageClick} />
  }

  if (typeof data === 'object') {
    return (
      <JsonObject
        obj={data as Record<string, unknown>}
        depth={depth}
        onImageClick={onImageClick}
      />
    )
  }

  return <span className={styles.valueStr}>{String(data)}</span>
}

// ── Object renderer ───────────────────────────────────────────────────────────

function JsonObject({
  obj,
  depth,
  onImageClick,
}: {
  obj: Record<string, unknown>
  depth: number
  onImageClick?: (url: string) => void
}) {
  const [collapsed, setCollapsed] = useState(() => depth > 0 && shouldCollapseObject(obj))
  const entries = Object.entries(obj).filter(([, v]) => v !== null && v !== undefined)

  if (entries.length === 0) return <span className={styles.valueNull}>—</span>

  return (
    <div className={styles.node}>
      {depth > 0 && (
        <button className={styles.collapseBtn} onClick={() => setCollapsed(c => !c)}>
          {collapsed ? '▶' : '▼'} {collapsed ? `${entries.length} field(s)…` : ''}
        </button>
      )}
      {!collapsed && (
        <div className={depth > 0 ? styles.children : styles.tree}>
          {entries.map(([key, value]) => (
            <ObjectRow
              key={key}
              propKey={key}
              value={value}
              depth={depth}
              onImageClick={onImageClick}
            />
          ))}
        </div>
      )}
    </div>
  )
}

function ObjectRow({
  propKey,
  value,
  depth,
  onImageClick,
}: {
  propKey: string
  value: unknown
  depth: number
  onImageClick?: (url: string) => void
}) {
  const isNested = (typeof value === 'object' && value !== null) || Array.isArray(value)

  // ID-keyed leaf values get badge rendering
  const leafIsId = isIdKey(propKey) && !isNested

  return (
    <div className={styles.node}>
      <div className={styles.row}>
        <span className={leafIsId ? styles.keyId : styles.key}>
          {toLabel(propKey)}
        </span>
        {!isNested && (
          leafIsId
            ? <span className={styles.idBadge}>{String(value)}</span>
            : <JsonTree data={value} depth={depth + 1} onImageClick={onImageClick} />
        )}
      </div>
      {isNested && (
        <JsonTree data={value} depth={depth + 1} onImageClick={onImageClick} />
      )}
    </div>
  )
}

// ── Array renderer ────────────────────────────────────────────────────────────

function JsonArray({
  arr,
  depth,
  onImageClick,
}: {
  arr: unknown[]
  depth: number
  onImageClick?: (url: string) => void
}) {
  const [collapsed, setCollapsed] = useState(
    () => depth > 0 && shouldCollapseArray(arr),
  )

  if (arr.length === 0) return <span className={styles.valueNull}>(empty)</span>

  // All primitive → inline
  const allPrimitive = arr.every(
    item => item === null || item === undefined || (typeof item !== 'object' && !Array.isArray(item)),
  )
  if (allPrimitive) {
    return (
      <span className={styles.inlineList}>
        {arr.map(String).join(', ')}
      </span>
    )
  }

  return (
    <div className={styles.node}>
      <button className={styles.collapseBtn} onClick={() => setCollapsed(c => !c)}>
        {collapsed ? '▶' : '▼'} {collapsed ? `${arr.length} item(s)…` : `${arr.length} items`}
      </button>
      {!collapsed && (
        <div className={styles.children}>
          {arr.map((item, i) => (
            <div key={i} className={styles.node}>
              <span className={styles.arrayIndex}>#{i + 1}</span>
              <JsonTree data={item} depth={depth + 1} onImageClick={onImageClick} />
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
```

**Step 3: Type-check**

```
cd src/Chronicle.Web && npm run type-check
```
Expected: no errors.

---

### Task 9: Wire `<JsonTree>` into `PluginMetadataBox`

**Files:**
- Modify: `src/Chronicle.Web/src/components/PluginMetadataBox.tsx`

**Step 1: Add import at top of `PluginMetadataBox.tsx`**

```typescript
import { JsonTree } from './JsonTree'
```

**Step 2: Replace the `typeof value === 'object'` branch in `renderValue`**

Find this block (around line 169):

```typescript
    if (typeof value === 'object' && value !== null) {
      // Pretty-print nested objects rather than showing [object Object]
      return (
        <pre className={styles.value} style={{ whiteSpace: 'pre-wrap', fontSize: '0.75rem', margin: 0 }}>
          {JSON.stringify(value, null, 2)}
        </pre>
      )
    }
```

Replace with:

```typescript
    if (typeof value === 'object' && value !== null) {
      return (
        <div className={styles.value}>
          <JsonTree
            data={value}
            depth={0}
            onImageClick={onImageClick
              ? (url) => {
                  // Find this image's position in the page-level allImages array
                  // by locating it in the imageEntries list and offsetting by imageStartIndex
                  const localIdx = imageEntries.findIndex(img => img.url === url)
                  if (localIdx >= 0) onImageClick(imageStartIndex + localIdx)
                  else window.open(url, '_blank')
                }
              : undefined
            }
          />
        </div>
      )
    }
```

**Step 3: Type-check and lint**

```
cd src/Chronicle.Web && npm run type-check && npm run lint
```
Expected: no errors.

**Step 4: Commit everything**

```
git add src/Chronicle.Web/src/components/JsonTree.tsx \
        src/Chronicle.Web/src/components/JsonTree.module.css \
        src/Chronicle.Web/src/components/PluginMetadataBox.tsx
git commit -m "feat(ui): JsonTree smart recursive renderer replaces raw JSON.stringify fallback"
```

---

## Final verification

**Step 1: Full backend build + all tests**

```
cd src/Chronicle.API && dotnet build -c Release --nologo -v quiet
dotnet test tests/Chronicle.Tests.Unit/Chronicle.Tests.Unit.csproj --nologo -v quiet
dotnet test tests/Chronicle.Tests.Integration/Chronicle.Tests.Integration.csproj --nologo -v quiet
```
Expected: `0 Error(s)`, `Passed! 165`, `Passed! 100`

**Step 2: Frontend full check**

```
cd src/Chronicle.Web && npm run type-check && npm run lint
```
Expected: no errors.

**Step 3: Smoke test manually**

1. Open Background Tasks → enrichment table: verify count numbers are now links (zero counts grey, non-zero counts are blue/accent links)
2. Click a "Not Found" count → verify navigation to `/settings/enrichment/{pluginId}?status=NotFound`
3. On the drill-down page: verify status tabs, name search, pagination, card layout
4. Run enrichment for one plugin → verify new enrichment rows get `diagnostics_json` populated
5. Open a media detail page with MusicBrainz data → open Extended Data → verify tree rendering with collapse toggles, ID badges, links instead of raw JSON

**Step 4: Final commit (if any loose changes)**

```
git status
```
If clean, done. If any stragglers:
```
git add -A && git commit -m "chore: final cleanup for enrichment drill-down and JsonTree"
```
