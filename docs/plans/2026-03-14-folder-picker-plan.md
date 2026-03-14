# Folder Picker Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add an in-browser Sonarr-style folder picker so users can browse to a directory instead of typing paths by hand.

**Architecture:** A new `FileSystemController` exposes `GET /api/v1/filesystem?path=` returning drive roots or immediate subdirectories. Two new React components — `FolderPickerModal` (navigable tree modal) and `PathInput` (text input + Browse button) — consume this endpoint. `PathInput` replaces the bare `<input>` on the scan page and can be dropped in anywhere in the app that accepts a directory path.

**Tech Stack:** ASP.NET Core 9 (C#), `DriveInfo` (.NET BCL), React 18 + TypeScript, CSS Modules, Axios

---

## Working directory

All commands run from:
```
W:\Scripts\Chronicle\.claude\worktrees\frosty-allen
```

Run the test suite with:
```bash
dotnet test --verbosity minimal
```

Run the TypeScript type-checker with:
```bash
cd src/Chronicle.Web && npm run type-check
```

---

## Task 1: Backend — FilesystemController + DTOs

**Files:**
- Create: `src/Chronicle.API/DTOs/FilesystemDtos.cs`
- Create: `src/Chronicle.API/Controllers/FilesystemController.cs`

---

**Step 1: Create the DTO file**

Create `src/Chronicle.API/DTOs/FilesystemDtos.cs`:

```csharp
namespace Chronicle.API.DTOs;

public record FilesystemEntryDto(string Name, string Path);

public record FilesystemListingDto(
    string? Path,
    string? Parent,
    List<FilesystemEntryDto> Directories
);
```

---

**Step 2: Create the controller**

Create `src/Chronicle.API/Controllers/FilesystemController.cs`:

```csharp
using Chronicle.API.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chronicle.API.Controllers;

[ApiController]
[Route("api/v1/filesystem")]
[Authorize]
public class FilesystemController : ControllerBase
{
    /// <summary>
    /// Returns the immediate subdirectories of <paramref name="path"/>.
    /// When <paramref name="path"/> is null or empty, returns logical drive roots
    /// (Windows: C:\, D:\, etc. — Linux/Docker: mount points such as /).
    /// Access-denied subdirectories are silently skipped rather than erroring.
    /// </summary>
    [HttpGet]
    public IActionResult List([FromQuery] string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Ok(ApiResponse<FilesystemListingDto>.Ok(GetDriveRoots()));

        var dir = new DirectoryInfo(path);

        if (!dir.Exists)
            return BadRequest(ApiResponse<FilesystemListingDto>.Fail(
                "PATH_NOT_FOUND", $"Directory not found: {path}"));

        string? parent = dir.Parent?.FullName;

        var subdirs = new List<FilesystemEntryDto>();
        try
        {
            foreach (var sub in dir.EnumerateDirectories())
            {
                try { subdirs.Add(new FilesystemEntryDto(sub.Name, sub.FullName)); }
                catch (UnauthorizedAccessException) { /* skip inaccessible entries */ }
            }
        }
        catch (UnauthorizedAccessException)
        {
            return BadRequest(ApiResponse<FilesystemListingDto>.Fail(
                "ACCESS_DENIED", $"Access denied: {path}"));
        }

        subdirs.Sort((a, b) =>
            string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        return Ok(ApiResponse<FilesystemListingDto>.Ok(
            new FilesystemListingDto(dir.FullName, parent, subdirs)));
    }

    private static FilesystemListingDto GetDriveRoots()
    {
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => new FilesystemEntryDto(d.Name, d.RootDirectory.FullName))
            .ToList();

        return new FilesystemListingDto(null, null, drives);
    }
}
```

---

**Step 3: Build to confirm no compile errors**

```bash
dotnet build src/Chronicle.API --verbosity minimal
```

Expected output: `Build succeeded.` with 0 errors.

---

**Step 4: Commit**

```bash
git add src/Chronicle.API/DTOs/FilesystemDtos.cs src/Chronicle.API/Controllers/FilesystemController.cs
git commit -m "feat(api): add GET /api/v1/filesystem endpoint for in-browser folder browsing

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>"
```

---

## Task 2: Integration Tests for FilesystemController

**Files:**
- Create: `tests/Chronicle.Tests.Integration/FilesystemTests.cs`

The integration test harness (`ChronicleApiFactory`) spins up the full ASP.NET Core pipeline with an InMemory database. Call `factory.SeedDatabase()` in the constructor and use `_factory.CreateClient()` to get an `HttpClient`.

Authentication: POST to `/api/v1/auth/register` first, then attach the returned JWT as `Authorization: Bearer {token}`.

---

**Step 1: Write the test file**

Create `tests/Chronicle.Tests.Integration/FilesystemTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Chronicle.Tests.Integration;

public class FilesystemTests : IClassFixture<ChronicleApiFactory>
{
    private readonly ChronicleApiFactory _factory;
    private static readonly JsonSerializerOptions _json =
        new() { PropertyNameCaseInsensitive = true };

    public FilesystemTests(ChronicleApiFactory factory)
    {
        factory.SeedDatabase();
        _factory = factory;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<HttpClient> AuthClientAsync()
    {
        var client = _factory.CreateClient();
        var username = $"fs_{Guid.NewGuid():N}";

        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { username, password = "Password123!" });

        var token = JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("token").GetString()!;

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        return client;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFilesystem_NoToken_Returns401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/filesystem");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetFilesystem_NoPath_ReturnsDriveRoots()
    {
        var client = await AuthClientAsync();
        var resp = await client.GetAsync("/api/v1/filesystem");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

        var data = body.RootElement.GetProperty("data");

        // At drive roots, path and parent are null
        data.GetProperty("path").ValueKind.Should().Be(JsonValueKind.Null);
        data.GetProperty("parent").ValueKind.Should().Be(JsonValueKind.Null);

        // At least one drive / mount point must exist
        var dirs = data.GetProperty("directories");
        dirs.GetArrayLength().Should().BeGreaterThan(0);

        // Each entry has non-empty name and path
        var first = dirs[0];
        first.GetProperty("name").GetString().Should().NotBeNullOrEmpty();
        first.GetProperty("path").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetFilesystem_ValidPath_ReturnsSubdirectories()
    {
        var client = await AuthClientAsync();

        // Temp directory always exists and is readable on any OS
        var tempPath = Path.GetTempPath()
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var resp = await client.GetAsync(
            $"/api/v1/filesystem?path={Uri.EscapeDataString(tempPath)}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

        var data = body.RootElement.GetProperty("data");
        data.GetProperty("path").GetString().Should().NotBeNullOrEmpty();

        // directories is always an array (may be empty if temp has no subdirs)
        data.GetProperty("directories").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task GetFilesystem_InvalidPath_Returns400()
    {
        var client = await AuthClientAsync();

        var resp = await client.GetAsync(
            "/api/v1/filesystem?path=Z%3A%5Cchronicle_test_path_that_does_not_exist");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        body.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("PATH_NOT_FOUND");
    }
}
```

---

**Step 2: Run only the new tests**

```bash
dotnet test tests/Chronicle.Tests.Integration --filter "FilesystemTests" --verbosity normal
```

Expected: 4 tests, all PASS.

---

**Step 3: Run the full test suite**

```bash
dotnet test --verbosity minimal
```

Expected: all tests pass (no regressions).

---

**Step 4: Commit**

```bash
git add tests/Chronicle.Tests.Integration/FilesystemTests.cs
git commit -m "test(api): integration tests for GET /api/v1/filesystem

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>"
```

---

## Task 3: Frontend API Module

**Files:**
- Create: `src/Chronicle.Web/src/api/filesystem.ts`

The API client (`src/Chronicle.Web/src/api/client.ts`) is an Axios instance with base URL `/api/v1` and a JWT interceptor. All other API modules in `src/Chronicle.Web/src/api/` follow the same pattern — import `client`, call `.get()` / `.post()`, throw on failure.

---

**Step 1: Create the API module**

Create `src/Chronicle.Web/src/api/filesystem.ts`:

```typescript
import client from './client'
import type { ApiResponse } from '@/types'

export interface FilesystemEntry {
  name: string
  path: string
}

export interface FilesystemListing {
  path: string | null
  parent: string | null
  directories: FilesystemEntry[]
}

export async function listDirectory(path: string): Promise<FilesystemListing> {
  const params = path ? { path } : {}
  const { data } = await client.get<ApiResponse<FilesystemListing>>('/filesystem', { params })
  if (!data.success || !data.data)
    throw new Error(data.error?.message ?? 'Failed to list directory')
  return data.data
}
```

---

**Step 2: Type-check**

```bash
cd src/Chronicle.Web && npm run type-check
```

Expected: no errors.

---

**Step 3: Commit**

```bash
git add src/Chronicle.Web/src/api/filesystem.ts
git commit -m "feat(web): add filesystem API module (listDirectory)

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>"
```

---

## Task 4: FolderPickerModal Component

**Files:**
- Create: `src/Chronicle.Web/src/components/FolderPickerModal.tsx`
- Create: `src/Chronicle.Web/src/components/FolderPickerModal.module.css`

The modal uses CSS custom properties already defined by the app themes (e.g. `--bg-card`, `--border`, `--accent`, `--text-primary`, `--text-muted`). No new CSS variables needed.

---

**Step 1: Create the CSS module**

Create `src/Chronicle.Web/src/components/FolderPickerModal.module.css`:

```css
.overlay {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.6);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
}

.modal {
  background: var(--bg-card, #1e2a2a);
  border: 1px solid var(--border, #2a3a3a);
  border-radius: 8px;
  width: min(540px, 95vw);
  max-height: 80vh;
  display: flex;
  flex-direction: column;
  color: var(--text-primary, #fff);
}

/* ── Header ──────────────────────────────────────────────────────────────── */

.header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 14px 16px;
  border-bottom: 1px solid var(--border, #2a3a3a);
}

.headerTitle {
  font-size: 15px;
  font-weight: 600;
  margin: 0;
}

.closeBtn {
  background: none;
  border: none;
  color: var(--text-muted, #888);
  font-size: 20px;
  cursor: pointer;
  padding: 0 4px;
  line-height: 1;
}

.closeBtn:hover { color: var(--text-primary, #fff); }

/* ── Path bar ─────────────────────────────────────────────────────────────── */

.pathBar {
  padding: 10px 16px;
  border-bottom: 1px solid var(--border, #2a3a3a);
}

.pathInput {
  width: 100%;
  box-sizing: border-box;
  background: var(--bg-input, #111c1c);
  border: 1px solid var(--border, #2a3a3a);
  border-radius: 4px;
  color: var(--text-primary, #fff);
  font-size: 16px; /* 16px prevents iOS auto-zoom on focus */
  padding: 6px 10px;
}

.pathInput:focus {
  outline: none;
  border-color: var(--accent, #00ff88);
}

.pathError {
  margin: 4px 0 0;
  font-size: 12px;
  color: #ff6b6b;
}

/* ── Directory list ───────────────────────────────────────────────────────── */

.dirList {
  flex: 1;
  overflow-y: auto;
  padding: 8px 0;
  min-height: 120px;
}

.dirRow {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px 16px;
  cursor: pointer;
  font-size: 14px;
  min-height: 44px; /* minimum touch target */
  box-sizing: border-box;
}

.dirRow:hover { background: var(--bg-hover, #243030); }

.dirIcon { font-size: 16px; flex-shrink: 0; }

.upRow { color: var(--text-muted, #888); }

.emptyMsg,
.loadingMsg,
.errorMsg {
  padding: 20px 16px;
  text-align: center;
  color: var(--text-muted, #888);
  font-size: 14px;
}

.errorMsg { color: #ff6b6b; }

/* ── Footer ──────────────────────────────────────────────────────────────── */

.footer {
  border-top: 1px solid var(--border, #2a3a3a);
  padding: 12px 16px;
  display: flex;
  align-items: center;
  gap: 10px;
}

.selectedPath {
  flex: 1;
  font-size: 12px;
  color: var(--text-muted, #888);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.selectBtn {
  background: var(--accent, #00ff88);
  color: #000;
  border: none;
  border-radius: 4px;
  padding: 8px 18px;
  font-weight: 600;
  cursor: pointer;
  white-space: nowrap;
}

.selectBtn:disabled { opacity: 0.4; cursor: default; }
.selectBtn:not(:disabled):hover { opacity: 0.9; }

.cancelBtn {
  background: none;
  border: 1px solid var(--border, #2a3a3a);
  color: var(--text-muted, #888);
  border-radius: 4px;
  padding: 8px 14px;
  cursor: pointer;
  white-space: nowrap;
}

.cancelBtn:hover { color: var(--text-primary, #fff); }

/* ── Mobile ──────────────────────────────────────────────────────────────── */

@media (max-width: 380px) {
  .footer { flex-wrap: wrap; }
  .selectedPath { width: 100%; order: -1; }
  .selectBtn,
  .cancelBtn { flex: 1; }
}
```

---

**Step 2: Create the component**

Create `src/Chronicle.Web/src/components/FolderPickerModal.tsx`:

```tsx
import { useState, useEffect, useCallback } from 'react'
import { listDirectory } from '@/api/filesystem'
import type { FilesystemListing } from '@/api/filesystem'
import styles from './FolderPickerModal.module.css'

interface FolderPickerModalProps {
  initialPath?: string
  onSelect: (path: string) => void
  onClose: () => void
}

export default function FolderPickerModal({
  initialPath,
  onSelect,
  onClose,
}: FolderPickerModalProps) {
  const [currentPath, setCurrentPath] = useState(initialPath ?? '')
  const [inputPath, setInputPath] = useState(initialPath ?? '')
  const [listing, setListing] = useState<FilesystemListing | null>(null)
  const [isLoading, setIsLoading] = useState(false)
  const [listError, setListError] = useState<string | null>(null)
  const [inputError, setInputError] = useState<string | null>(null)

  // Navigate to a directory (or '' for drive roots)
  const navigate = useCallback(async (path: string) => {
    setIsLoading(true)
    setListError(null)
    setInputError(null)
    try {
      const result = await listDirectory(path)
      setListing(result)
      setCurrentPath(result.path ?? '')
      setInputPath(result.path ?? '')
    } catch (err) {
      setListError(err instanceof Error ? err.message : 'Failed to load directory')
    } finally {
      setIsLoading(false)
    }
  }, [])

  // Load on mount
  useEffect(() => {
    navigate(initialPath ?? '')
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // Escape key closes the modal
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  const handlePathKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key !== 'Enter') return
    const typed = inputPath.trim()
    navigate(typed).catch(() => setInputError('Path not found'))
  }

  const handleSelect = () => {
    if (currentPath) {
      onSelect(currentPath)
      onClose()
    }
  }

  const selectedDisplay = currentPath || '(no folder selected)'

  return (
    <div
      className={styles.overlay}
      onClick={(e) => { if (e.target === e.currentTarget) onClose() }}
    >
      <div
        className={styles.modal}
        role="dialog"
        aria-modal="true"
        aria-label="Browse for Folder"
      >
        {/* Header */}
        <div className={styles.header}>
          <h2 className={styles.headerTitle}>Browse for Folder</h2>
          <button className={styles.closeBtn} onClick={onClose} aria-label="Close">
            ✕
          </button>
        </div>

        {/* Path bar */}
        <div className={styles.pathBar}>
          <input
            className={styles.pathInput}
            type="text"
            value={inputPath}
            onChange={(e) => setInputPath(e.target.value)}
            onKeyDown={handlePathKeyDown}
            placeholder="Type or paste a path, then press Enter"
            aria-label="Current path"
          />
          {inputError && <p className={styles.pathError}>{inputError}</p>}
        </div>

        {/* Directory list */}
        <div className={styles.dirList}>
          {isLoading && <p className={styles.loadingMsg}>Loading…</p>}

          {!isLoading && listError && (
            <p className={styles.errorMsg}>{listError}</p>
          )}

          {!isLoading && !listError && listing && (
            <>
              {/* Up row — shown whenever we are not at drive roots */}
              {listing.parent !== null && (
                <div
                  className={`${styles.dirRow} ${styles.upRow}`}
                  onClick={() => navigate(listing.parent!)}
                  role="button"
                  tabIndex={0}
                  onKeyDown={(e) => e.key === 'Enter' && navigate(listing.parent!)}
                >
                  <span className={styles.dirIcon}>📁</span>
                  <span>.. (Up)</span>
                </div>
              )}

              {/* Empty-state messages */}
              {listing.directories.length === 0 && listing.parent === null && (
                <p className={styles.emptyMsg}>No drives found</p>
              )}
              {listing.directories.length === 0 && listing.parent !== null && (
                <p className={styles.emptyMsg}>No subfolders</p>
              )}

              {/* Folder rows */}
              {listing.directories.map((dir) => (
                <div
                  key={dir.path}
                  className={styles.dirRow}
                  onClick={() => navigate(dir.path)}
                  role="button"
                  tabIndex={0}
                  onKeyDown={(e) => e.key === 'Enter' && navigate(dir.path)}
                >
                  <span className={styles.dirIcon}>📁</span>
                  <span>{dir.name}</span>
                </div>
              ))}
            </>
          )}
        </div>

        {/* Footer */}
        <div className={styles.footer}>
          <span className={styles.selectedPath} title={selectedDisplay}>
            {selectedDisplay}
          </span>
          <button className={styles.cancelBtn} onClick={onClose}>
            Cancel
          </button>
          <button
            className={styles.selectBtn}
            onClick={handleSelect}
            disabled={!currentPath}
          >
            Select
          </button>
        </div>
      </div>
    </div>
  )
}
```

---

**Step 3: Type-check**

```bash
cd src/Chronicle.Web && npm run type-check
```

Expected: no errors.

---

**Step 4: Commit**

```bash
git add src/Chronicle.Web/src/components/FolderPickerModal.tsx \
        src/Chronicle.Web/src/components/FolderPickerModal.module.css
git commit -m "feat(web): add FolderPickerModal component

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>"
```

---

## Task 5: PathInput Component

**Files:**
- Create: `src/Chronicle.Web/src/components/PathInput.tsx`
- Create: `src/Chronicle.Web/src/components/PathInput.module.css`

`PathInput` is a drop-in replacement for `<input type="text">` wherever a directory path is needed. It renders the text input + a Browse button side-by-side, and opens `FolderPickerModal` on click.

---

**Step 1: Create the CSS module**

Create `src/Chronicle.Web/src/components/PathInput.module.css`:

```css
.wrapper {
  display: flex;
  gap: 8px;
  align-items: stretch;
}

.input {
  flex: 1;
  min-width: 0; /* prevents flex child from overflowing */
}

.browseBtn {
  background: var(--bg-card, #1e2a2a);
  border: 1px solid var(--border, #2a3a3a);
  color: var(--text-primary, #fff);
  border-radius: 4px;
  padding: 0 14px;
  cursor: pointer;
  font-size: 14px;
  white-space: nowrap;
  display: flex;
  align-items: center;
  gap: 5px;
}

.browseBtn:hover {
  border-color: var(--accent, #00ff88);
  color: var(--accent, #00ff88);
}
```

---

**Step 2: Create the component**

Create `src/Chronicle.Web/src/components/PathInput.tsx`:

```tsx
import { useState } from 'react'
import FolderPickerModal from './FolderPickerModal'
import styles from './PathInput.module.css'

interface PathInputProps {
  value: string
  onChange: (value: string) => void
  placeholder?: string
  id?: string
  className?: string
}

export default function PathInput({
  value,
  onChange,
  placeholder,
  id,
  className,
}: PathInputProps) {
  const [showPicker, setShowPicker] = useState(false)

  return (
    <>
      <div className={styles.wrapper}>
        <input
          id={id}
          type="text"
          value={value}
          onChange={(e) => onChange(e.target.value)}
          placeholder={placeholder}
          className={[className, styles.input].filter(Boolean).join(' ')}
        />
        <button
          type="button"
          className={styles.browseBtn}
          onClick={() => setShowPicker(true)}
          aria-label="Browse for folder"
        >
          📁 Browse
        </button>
      </div>

      {showPicker && (
        <FolderPickerModal
          initialPath={value}
          onSelect={(path) => onChange(path)}
          onClose={() => setShowPicker(false)}
        />
      )}
    </>
  )
}
```

---

**Step 3: Type-check**

```bash
cd src/Chronicle.Web && npm run type-check
```

Expected: no errors.

---

**Step 4: Commit**

```bash
git add src/Chronicle.Web/src/components/PathInput.tsx \
        src/Chronicle.Web/src/components/PathInput.module.css
git commit -m "feat(web): add PathInput component (text input + folder browse button)

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>"
```

---

## Task 6: Wire PathInput into ScanPage

**Files:**
- Modify: `src/Chronicle.Web/src/pages/scan/ScanPage.tsx`

The scan page currently uses a bare `<input type="text">` for the "Directory path" field. Replace it with `<PathInput>`. The label's `htmlFor="scan-path"` already matches the `id` being passed through, so label click-to-focus still works.

---

**Step 1: Add the import**

In `src/Chronicle.Web/src/pages/scan/ScanPage.tsx`, add one import after the existing imports (before the `styles` import):

```tsx
import PathInput from '@/components/PathInput'
```

The full import block at the top of the file should then look like:

```tsx
import { useState, useEffect, useRef } from 'react'
import { useQuery, useMutation } from '@tanstack/react-query'
import { getScanStatus, getScanProgress, previewScan, importDirect } from '@/api/scan'
import type { ScanProgress } from '@/api/scan'
import { getMediaTypes } from '@/api/media'
import { useBackgroundActivity } from '@/contexts/BackgroundActivityContext'
import type { ScannedFile, MediaTypeOption } from '@/types'
import PathInput from '@/components/PathInput'
import styles from './ScanPage.module.css'
```

---

**Step 2: Replace the bare `<input>` with `<PathInput>`**

Find this block in the JSX (inside the `step === 'configure'` section):

```tsx
<div className={styles.field}>
  <label className={styles.label} htmlFor="scan-path">Directory path</label>
  <input
    id="scan-path"
    className={styles.textInput}
    type="text"
    placeholder="C:\Movies or /mnt/media/movies"
    value={path}
    onChange={(e) => setPath(e.target.value)}
  />
</div>
```

Replace with:

```tsx
<div className={styles.field}>
  <label className={styles.label} htmlFor="scan-path">Directory path</label>
  <PathInput
    id="scan-path"
    className={styles.textInput}
    placeholder="C:\Movies or /mnt/media/movies"
    value={path}
    onChange={setPath}
  />
</div>
```

---

**Step 3: Type-check**

```bash
cd src/Chronicle.Web && npm run type-check
```

Expected: no errors.

---

**Step 4: Run full test suite**

```bash
dotnet test --verbosity minimal
```

Expected: all tests pass.

---

**Step 5: Commit**

```bash
git add src/Chronicle.Web/src/pages/scan/ScanPage.tsx
git commit -m "feat(web): replace scan page path input with PathInput browse component

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>"
```

---

## Task 7: Push branch

```bash
git push
```

This updates the existing `claude/frosty-allen` remote branch.

---

## Manual smoke test (after Tasks 1–6)

Start the API and frontend:

```bash
# Terminal 1
cd src/Chronicle.API && dotnet run

# Terminal 2
cd src/Chronicle.Web && npm run dev
```

Open `http://localhost:3000`, log in, navigate to **File Scan**.

1. Click **Browse** next to "Directory path" — the folder picker modal opens
2. Drive roots appear (e.g. `C:\`, `D:\` on Windows or `/` on Linux)
3. Click a drive/folder — the list updates to show that folder's subdirectories
4. The path bar updates to reflect the current path
5. Click **.. (Up)** — navigates back to the parent; at drive roots the Up row is absent
6. Type a path in the path bar and press Enter — navigates directly to that path
7. Paste a UNC path (`\\NAS\Media`) and press Enter — navigates if accessible
8. Click **Select** — the modal closes and the scan page's path field shows the selected path
9. Click **Cancel** or press Escape — modal closes, path unchanged
10. Type a nonexistent path and press Enter — inline error shown, list unchanged
