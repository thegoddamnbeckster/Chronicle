# Diagnostic Footer Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a persistent app footer with copyright/version info and a collapsible diagnostics panel, visibility controlled by a per-user preference.

**Architecture:** A new `AppFooter` React component is added to Layout and always rendered. A `GET /api/v1/diagnostics` endpoint (no auth) provides environment info. A `showDiagnostics` flag in the user's preferences JSON controls whether the fold tab appears — defaulting to `true` for admins, `false` for regular users.

**Tech Stack:** React 18 + TypeScript + CSS Modules, ASP.NET Core 9, EF Core 9 (SQLite), Axios

---

### Task 1: Add `PreferencesJson` to User model + migration

**Files:**
- Modify: `src/Chronicle.Core/Models/User.cs`
- Modify: `src/Chronicle.Data/ChronicleDbContext.cs` (model config)
- Create: migration via `dotnet ef migrations add`

**Step 1: Add property to User model**

In `src/Chronicle.Core/Models/User.cs`, add after `IsAdmin`:
```csharp
public string PreferencesJson { get; set; } = "{}";
```

**Step 2: Add column config in DbContext**

In `src/Chronicle.Data/ChronicleDbContext.cs`, inside `OnModelCreating` in the `users` entity block, add:
```csharp
entity.Property(u => u.PreferencesJson)
    .HasColumnName("preferences_json")
    .HasDefaultValue("{}");
```

**Step 3: Create migration**

```bash
cd W:\Scripts\Chronicle\.claude\worktrees\epic-perlman\src\Chronicle.API
$env:EF_DESIGN_TIME = "1"
dotnet ef migrations add AddUserPreferences --project ../Chronicle.Data --startup-project . --no-build
```
Expected: new migration file in `src/Chronicle.Data/Migrations/`

**Step 4: Apply migration**

```bash
dotnet ef database update --no-build
```
Expected: `Done.`

**Step 5: Commit**

```bash
git add src/Chronicle.Core/Models/User.cs src/Chronicle.Data/
git commit -m "feat(data): add preferences_json column to users table"
```

---

### Task 2: Add preferences read/write helpers to UserService

**Files:**
- Modify: `src/Chronicle.Services/UserService.cs`

**Step 1: Add a `UserPreferences` record to Chronicle.Core**

Create `src/Chronicle.Core/Models/UserPreferences.cs`:
```csharp
namespace Chronicle.Core.Models;

public class UserPreferences
{
    public bool? ShowDiagnostics { get; set; }
}
```

**Step 2: Add preference methods to IUserService interface**

In `src/Chronicle.Services/IUserService.cs`, add:
```csharp
Task<UserPreferences> GetPreferencesAsync(int userId);
Task UpdatePreferencesAsync(int userId, UserPreferences patch);
```

**Step 3: Implement in UserService**

Add using: `using System.Text.Json;`

Add method bodies:
```csharp
public async Task<UserPreferences> GetPreferencesAsync(int userId)
{
    var user = await _dbContext.Users.FindAsync(userId)
        ?? throw new UserNotFoundException(userId.ToString());
    try { return JsonSerializer.Deserialize<UserPreferences>(user.PreferencesJson) ?? new(); }
    catch { return new(); }
}

public async Task UpdatePreferencesAsync(int userId, UserPreferences patch)
{
    var user = await _dbContext.Users.FindAsync(userId)
        ?? throw new UserNotFoundException(userId.ToString());
    UserPreferences current;
    try { current = JsonSerializer.Deserialize<UserPreferences>(user.PreferencesJson) ?? new(); }
    catch { current = new(); }
    if (patch.ShowDiagnostics.HasValue) current.ShowDiagnostics = patch.ShowDiagnostics;
    user.PreferencesJson = JsonSerializer.Serialize(current);
    user.UpdatedAt = DateTime.UtcNow;
    await _dbContext.SaveChangesAsync();
}
```

**Step 4: Expose `ShowDiagnostics` in the UserDto / login response**

In `src/Chronicle.API/Controllers/AuthController.cs` (and/or `UsersController.cs`), find `UserDto` and add:
```csharp
public bool ShowDiagnostics { get; init; }
```

Update the mapping where `UserDto` is constructed to populate it:
```csharp
ShowDiagnostics = prefs.ShowDiagnostics ?? user.IsAdmin
```
(Admins default to `true`, regular users default to `false`)

**Step 5: Commit**

```bash
git add src/Chronicle.Core/ src/Chronicle.Services/ src/Chronicle.API/
git commit -m "feat(services): add user preferences read/write with showDiagnostics flag"
```

---

### Task 3: Add `PATCH /api/v1/users/me/preferences` endpoint

**Files:**
- Modify: `src/Chronicle.API/Controllers/UsersController.cs`

**Step 1: Add the endpoint**

```csharp
[HttpPatch("me/preferences")]
[Authorize]
public async Task<IActionResult> PatchMyPreferences([FromBody] PatchPreferencesRequest req)
{
    var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    var patch = new UserPreferences { ShowDiagnostics = req.ShowDiagnostics };
    await _userService.UpdatePreferencesAsync(userId, patch);
    var prefs = await _userService.GetPreferencesAsync(userId);
    var user = await _userService.GetByIdAsync(userId);
    return Ok(ApiResponse<object>.Ok(new
    {
        showDiagnostics = prefs.ShowDiagnostics ?? (user?.IsAdmin ?? false)
    }));
}

public record PatchPreferencesRequest(bool? ShowDiagnostics);
```

**Step 2: Verify GET /api/v1/users/me returns ShowDiagnostics**

Check that `UsersController.GetMe()` calls the updated UserDto mapping that includes `ShowDiagnostics`. If not, update the mapping there too.

**Step 3: Commit**

```bash
git add src/Chronicle.API/Controllers/UsersController.cs
git commit -m "feat(api): add PATCH /api/v1/users/me/preferences endpoint"
```

---

### Task 4: Create `GET /api/v1/diagnostics` endpoint

**Files:**
- Create: `src/Chronicle.API/Controllers/DiagnosticsController.cs`

**Step 1: Create the controller**

```csharp
using Microsoft.AspNetCore.Mvc;
using Chronicle.API.DTOs;
using System.Diagnostics;
using System.Reflection;

namespace Chronicle.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class DiagnosticsController : ControllerBase
{
    private readonly IConfiguration _config;

    public DiagnosticsController(IConfiguration config)
    {
        _config = config;
    }

    [HttpGet]
    public IActionResult Get()
    {
        var apiDir = AppContext.BaseDirectory;
        var repoRoot = FindRepoRoot(apiDir) ?? apiDir;
        var dbPath = GetDbPath();
        var logsPath = Path.Combine(apiDir, "logs");
        var (branch, commitHash) = GetGitInfo(repoRoot);
        var apiProjectPath = Path.Combine(repoRoot, "src", "Chronicle.API", "Chronicle.API.csproj");

        // Read ports from ports.json
        var portsFile = Path.Combine(repoRoot, "ports.json");
        int apiPort = 8080, webPort = 3000;
        if (System.IO.File.Exists(portsFile))
        {
            try
            {
                var ports = System.Text.Json.JsonSerializer.Deserialize<PortsConfig>(
                    System.IO.File.ReadAllText(portsFile));
                if (ports != null) { apiPort = ports.Api; webPort = ports.Web; }
            }
            catch { }
        }

        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";
        // Strip metadata suffix (e.g. "1.0.0+abc123" → "1.0.0")
        if (version.Contains('+')) version = version[..version.IndexOf('+')];

        return Ok(ApiResponse<DiagnosticsDto>.Ok(new DiagnosticsDto(
            RepoRoot: repoRoot,
            ApiProjectPath: apiProjectPath,
            ApiDir: apiDir,
            DbPath: dbPath,
            DbExists: System.IO.File.Exists(dbPath),
            LogsPath: logsPath,
            Branch: branch,
            CommitHash: commitHash,
            ApiUrl: $"http://localhost:{apiPort}",
            WebUrl: $"http://localhost:{webPort}",
            Version: version
        )));
    }

    private string GetDbPath()
    {
        var cs = _config.GetConnectionString("DefaultConnection") ?? "";
        // EF SQLite connection string: "Data Source=path/to/file.db"
        var match = System.Text.RegularExpressions.Regex.Match(cs, @"Data Source=([^;]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var path = match.Groups[1].Value.Trim();
            return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        }
        return Path.Combine(AppContext.BaseDirectory, "chronicle.db");
    }

    private static string? FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                System.IO.File.Exists(Path.Combine(dir.FullName, ".git"))) // worktree uses a file
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static (string branch, string hash) GetGitInfo(string repoRoot)
    {
        try
        {
            static string Run(string args, string cwd)
            {
                var psi = new ProcessStartInfo("git", args)
                {
                    WorkingDirectory = cwd,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi)!;
                var output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(3000);
                return output;
            }
            var branch = Run("rev-parse --abbrev-ref HEAD", repoRoot);
            var hash   = Run("rev-parse --short HEAD", repoRoot);
            return (branch, hash);
        }
        catch { return ("unknown", "unknown"); }
    }

    private record PortsConfig(int Api, int Web);
}

public record DiagnosticsDto(
    string RepoRoot,
    string ApiProjectPath,
    string ApiDir,
    string DbPath,
    bool DbExists,
    string LogsPath,
    string Branch,
    string CommitHash,
    string ApiUrl,
    string WebUrl,
    string Version
);
```

**Step 2: Build to verify**

```bash
cd W:\Scripts\Chronicle\.claude\worktrees\epic-perlman
dotnet build src/Chronicle.API/Chronicle.API.csproj --no-restore -c Debug --nologo 2>&1 | tail -5
```
Expected: `Build succeeded.`

**Step 3: Commit**

```bash
git add src/Chronicle.API/Controllers/DiagnosticsController.cs
git commit -m "feat(api): add GET /api/v1/diagnostics endpoint"
```

---

### Task 5: Create frontend API clients

**Files:**
- Create: `src/Chronicle.Web/src/api/diagnostics.ts`
- Modify: `src/Chronicle.Web/src/api/auth.ts` (add showDiagnostics to UserInfo)

**Step 1: Create diagnostics.ts**

```typescript
import client from './client'

export interface DiagnosticsInfo {
  repoRoot: string
  apiProjectPath: string
  apiDir: string
  dbPath: string
  dbExists: boolean
  logsPath: string
  branch: string
  commitHash: string
  apiUrl: string
  webUrl: string
  version: string
}

export async function getDiagnostics(): Promise<DiagnosticsInfo> {
  const res = await client.get<{ success: true; data: DiagnosticsInfo }>('/diagnostics')
  return res.data.data
}
```

**Step 2: Add showDiagnostics + updatePreferences to users API**

Create `src/Chronicle.Web/src/api/users.ts`:
```typescript
import client from './client'

export async function updateMyPreferences(prefs: { showDiagnostics?: boolean }): Promise<void> {
  await client.patch('/users/me/preferences', prefs)
}
```

**Step 3: Update UserInfo type**

In `src/Chronicle.Web/src/api/auth.ts`, find the `UserInfo` interface and add:
```typescript
showDiagnostics: boolean
```

**Step 4: Commit**

```bash
git add src/Chronicle.Web/src/api/diagnostics.ts src/Chronicle.Web/src/api/users.ts src/Chronicle.Web/src/api/auth.ts
git commit -m "feat(web): add diagnostics and user preferences API clients"
```

---

### Task 6: Create AppFooter component

**Files:**
- Create: `src/Chronicle.Web/src/components/layout/AppFooter.tsx`
- Create: `src/Chronicle.Web/src/components/layout/AppFooter.module.css`

**Step 1: Create AppFooter.module.css**

```css
/* ── Outer wrapper ─────────────────────────────────────────────── */
.footer {
  background: var(--bg-secondary);
  border-top: 1px solid var(--border);
  flex-shrink: 0;
}

/* ── Always-visible bar ────────────────────────────────────────── */
.bar {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 10px;
  padding: 8px 24px;
  position: relative;
}

.symbol {
  font-size: 0.9rem;
  color: var(--accent);
  opacity: 0.7;
}

.copyright {
  font-size: 0.75rem;
  color: var(--text-muted);
}

.version {
  font-size: 0.72rem;
  color: var(--text-muted);
  opacity: 0.7;
  font-family: monospace;
}

.diagTab {
  position: absolute;
  right: 16px;
  top: 50%;
  transform: translateY(-50%);
  font-size: 0.7rem;
  color: var(--text-muted);
  background: none;
  border: 1px solid var(--border);
  border-radius: 3px;
  padding: 2px 8px;
  cursor: pointer;
  opacity: 0.6;
  white-space: nowrap;
}
.diagTab:hover { opacity: 1; }

/* ── Diagnostics panel ─────────────────────────────────────────── */
.panel {
  border-top: 1px solid var(--border);
  padding: 14px 24px;
  font-family: 'Consolas', 'Courier New', monospace;
  font-size: 0.78rem;
  color: var(--text-secondary);
  background: var(--bg-primary);
}

.panelTitle {
  color: var(--accent);
  font-weight: 600;
  margin-bottom: 8px;
  font-size: 0.8rem;
}

.diagRow {
  display: flex;
  gap: 0;
  line-height: 1.8;
}

.diagKey {
  color: var(--text-muted);
  min-width: 14ch;
}

.diagVal {
  color: var(--text-primary);
  word-break: break-all;
}

.exists { color: #6fcf97; margin-left: 6px; font-size: 0.72rem; }
.missing { color: #eb5757; margin-left: 6px; font-size: 0.72rem; }

.loading { color: var(--text-muted); font-style: italic; }
.error   { color: #eb5757; }
```

**Step 2: Create AppFooter.tsx**

```tsx
import { useState } from 'react'
import { getDiagnostics, DiagnosticsInfo } from '../../api/diagnostics'
import styles from './AppFooter.module.css'

interface AppFooterProps {
  showDiagnostics: boolean
  version?: string
}

export default function AppFooter({ showDiagnostics, version }: AppFooterProps) {
  const [open, setOpen] = useState(false)
  const [diag, setDiag] = useState<DiagnosticsInfo | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function toggle() {
    if (!open && !diag) {
      setLoading(true)
      setError(null)
      try {
        setDiag(await getDiagnostics())
      } catch {
        setError('Failed to load diagnostics.')
      } finally {
        setLoading(false)
      }
    }
    setOpen(o => !o)
  }

  const year = new Date().getFullYear()

  return (
    <footer className={styles.footer}>
      {showDiagnostics && open && (
        <div className={styles.panel}>
          <div className={styles.panelTitle}>Chronicle Dev Environment — Diagnostics</div>
          {loading && <div className={styles.loading}>Loading…</div>}
          {error   && <div className={styles.error}>{error}</div>}
          {diag && (
            <>
              <DiagRow label="Repo root"    value={diag.repoRoot} />
              <DiagRow label="API project"  value={diag.apiProjectPath} />
              <DiagRow label="API dir"      value={diag.apiDir} />
              <div className={styles.diagRow}>
                <span className={styles.diagKey}>Database</span>
                <span className={styles.diagVal}>
                  {diag.dbPath}
                  {diag.dbExists
                    ? <span className={styles.exists}>[EXISTS]</span>
                    : <span className={styles.missing}>[MISSING]</span>}
                </span>
              </div>
              <DiagRow label="Logs"         value={diag.logsPath} />
              <DiagRow label="Branch"       value={`${diag.branch}  (${diag.commitHash})`} />
              <DiagRow label="API"          value={diag.apiUrl} />
              <DiagRow label="Web"          value={diag.webUrl} />
            </>
          )}
        </div>
      )}
      <div className={styles.bar}>
        <span className={styles.symbol}>◆</span>
        <span className={styles.copyright}>© {year} Chronicle</span>
        {version && <span className={styles.version}>· {version}</span>}
        {showDiagnostics && (
          <button className={styles.diagTab} onClick={toggle}>
            {open ? '▼' : '▲'} Diagnostics
          </button>
        )}
      </div>
    </footer>
  )
}

function DiagRow({ label, value }: { label: string; value: string }) {
  return (
    <div className={styles.diagRow}>
      <span className={styles.diagKey}>{label}</span>
      <span className={styles.diagVal}>{value}</span>
    </div>
  )
}
```

**Step 3: Commit**

```bash
git add src/Chronicle.Web/src/components/layout/AppFooter.tsx src/Chronicle.Web/src/components/layout/AppFooter.module.css
git commit -m "feat(web): add AppFooter component with diagnostics fold"
```

---

### Task 7: Wire AppFooter into Layout + fetch version on load

**Files:**
- Modify: `src/Chronicle.Web/src/components/layout/Layout.tsx`
- Modify: `src/Chronicle.Web/src/App.tsx` (or wherever layout state lives)

**Step 1: Read diagnostics version for the footer bar**

The simplest approach: fetch diagnostics once at app load and store version in state, OR just display the version from the user's auth response. Since the version is in the diagnostics endpoint, fetch it once in Layout on mount (no-op if fetch fails).

In `Layout.tsx`:
```tsx
import { useState, useEffect } from 'react'
import AppFooter from './AppFooter'
import { getDiagnostics } from '../../api/diagnostics'
import { useAuth } from '../../hooks/useAuth'

// Inside the Layout component:
const { user } = useAuth()
const [version, setVersion] = useState<string | undefined>()

useEffect(() => {
  getDiagnostics().then(d => setVersion(`${d.version} · ${d.commitHash} · ${d.branch}`)).catch(() => {})
}, [])

// In the JSX, after the existing <main> close tag:
<AppFooter
  showDiagnostics={user?.showDiagnostics ?? false}
  version={version}
/>
```

**Step 2: Ensure layout flex column fills viewport**

In the Layout CSS module, make sure the layout wrapper is `min-height: 100vh; display: flex; flex-direction: column;` and the `<main>` has `flex: 1`. This ensures the footer sticks to the bottom when content is short.

**Step 3: Commit**

```bash
git add src/Chronicle.Web/src/components/layout/Layout.tsx
git commit -m "feat(web): wire AppFooter into Layout"
```

---

### Task 8: Add showDiagnostics toggle to Preferences page

**Files:**
- Modify: `src/Chronicle.Web/src/pages/preferences/PreferencesPage.tsx`

**Step 1: Add the toggle section**

In `PreferencesPage.tsx`, add a new section after the theme section:

```tsx
import { updateMyPreferences } from '../../api/users'
import { useAuth } from '../../hooks/useAuth'

// Inside component:
const { user, refreshUser } = useAuth()  // may need to add refreshUser to useAuth
const [diagEnabled, setDiagEnabled] = useState(user?.showDiagnostics ?? false)
const [diagSaving, setDiagSaving] = useState(false)

async function handleDiagToggle(value: boolean) {
  setDiagEnabled(value)
  setDiagSaving(true)
  try {
    await updateMyPreferences({ showDiagnostics: value })
    // Optionally refresh user context so footer updates immediately
  } finally {
    setDiagSaving(false)
  }
}
```

Add JSX section:
```tsx
<section className={styles.section}>
  <h2 className={styles.sectionTitle}>Developer Tools</h2>
  <div className={styles.settingRow}>
    <div>
      <div className={styles.settingLabel}>Show Diagnostic Footer</div>
      <div className={styles.settingDesc}>
        Displays a collapsible panel with environment info — useful for debugging.
      </div>
    </div>
    <button
      className={diagEnabled ? styles.toggleOn : styles.toggle}
      onClick={() => handleDiagToggle(!diagEnabled)}
      disabled={diagSaving}
      aria-pressed={diagEnabled}
    >
      {diagEnabled ? 'On' : 'Off'}
    </button>
  </div>
</section>
```

Note: check what CSS classes/styles the PreferencesPage already uses and reuse them.

**Step 2: Update useAuth to reload user after preferences change**

The `showDiagnostics` flag needs to be read from the user object in the auth context. Check `src/Chronicle.Web/src/hooks/useAuth.ts` — if the user is stored there, add a way to re-fetch `/api/v1/users/me` after updating preferences so the footer reacts immediately.

**Step 3: Commit**

```bash
git add src/Chronicle.Web/src/pages/preferences/PreferencesPage.tsx
git commit -m "feat(web): add showDiagnostics toggle to Preferences page"
```

---

### Task 9: Build verification + type-check

**Step 1: Backend build**

```bash
cd W:\Scripts\Chronicle\.claude\worktrees\epic-perlman
dotnet build src/Chronicle.API/Chronicle.API.csproj --no-restore -c Debug --nologo 2>&1 | tail -5
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

**Step 2: Frontend type-check**

```bash
cd W:\Scripts\Chronicle\.claude\worktrees\epic-perlman\src\Chronicle.Web
npm run type-check 2>&1 | tail -10
```
Expected: no errors

**Step 3: Frontend lint**

```bash
npm run lint 2>&1 | tail -10
```
Expected: no errors or warnings

**Step 4: Backend tests**

```bash
cd W:\Scripts\Chronicle\.claude\worktrees\epic-perlman
dotnet test tests/Chronicle.Tests.Unit --no-build --verbosity minimal 2>&1 | tail -10
dotnet test tests/Chronicle.Tests.Integration --no-build --verbosity minimal 2>&1 | tail -10
```
Expected: all pass

---

### Task 10: Visual verification

**Step 1:** Navigate to `http://localhost:3000` and verify:
- Footer bar is visible at the bottom of every page
- Shows ◆ symbol, copyright, version string
- In dark-teal theme, footer is non-intrusive
- "▲ Diagnostics" tab appears only when `showDiagnostics = true`

**Step 2:** Click "▲ Diagnostics" and verify:
- Panel expands above the bar
- Shows all diagnostic rows in monospace
- DB path shows `[EXISTS]` or `[MISSING]` in the correct color
- Branch + hash match `git log --oneline -1`

**Step 3:** Go to Settings → Preferences, toggle "Show Diagnostic Footer" off
- Footer tab disappears immediately (or on next navigation)

**Step 4: Final commit**

```bash
git add -A
git commit -m "feat(web): diagnostic footer — visual verification complete"
```
