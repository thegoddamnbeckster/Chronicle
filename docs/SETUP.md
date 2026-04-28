# Chronicle — New Machine Setup Guide

This document explains how to clone, build, and run Chronicle (and its plugins)
from scratch on a fresh Windows machine.

---

## Prerequisites

Install these before cloning anything:

| Tool | Notes |
|------|-------|
| **Git** | [git-scm.com](https://git-scm.com) |
| **.NET 9 SDK** | [dotnet.microsoft.com/download](https://dotnet.microsoft.com/download) — the project targets `net9.0` |
| **Node.js 20+** | [nodejs.org](https://nodejs.org) — for the React frontend |
| **dotnet-ef CLI** | `dotnet tool install --global dotnet-ef` |
| **Claude Code** | `npm install -g @anthropic/claude-code` (for AI-assisted dev sessions) |
| **PowerShell 7+** | For build scripts — comes with Windows 11 |

---

## Repository Layout

All repos live in the same parent directory (e.g. `W:\Scripts\` or `C:\Dev\`).
The main repo and every plugin repo are **siblings**:

```
<base>\
  Chronicle\                     ← main application
  Chronicle.Plugin.MusicBrainz\ ← MusicBrainz metadata plugin
  Chronicle.Plugin.TMDB\         ← TMDB metadata plugin
  Chronicle.Plugin.Trakt\        ← Trakt import/sync plugin
  Chronicle.Plugin.Simkl\        ← SIMKL import/sync plugin
```

---

## Step 1 — Clone Everything

```powershell
cd <base>

git clone https://github.com/thegoddamnbeckster/Chronicle.git
git clone https://github.com/thegoddamnbeckster/Chronicle.Plugin.MusicBrainz.git
git clone https://github.com/thegoddamnbeckster/Chronicle.Plugin.TMDB.git
git clone https://github.com/thegoddamnbeckster/Chronicle.Plugin.Trakt.git
git clone https://github.com/thegoddamnbeckster/Chronicle.Plugin.Simkl.git
```

---

## Step 2 — Restore & Build the Main App

```powershell
cd Chronicle
dotnet restore src\Chronicle.sln
dotnet build   src\Chronicle.sln -c Debug
```

The React frontend:

```powershell
cd src\Chronicle.Web
npm install
```

---

## Step 3 — Run the Database Migration

The first run auto-applies EF migrations.  Alternatively run manually:

```powershell
cd Chronicle\src\Chronicle.API
dotnet ef database update
```

This creates `chronicle.db` in the working directory.

---

## Step 4 — Configure Local Settings

Create `src\Chronicle.API\appsettings.Development.json`
(this file is `.gitignore`d — safe to put secrets here):

```json
{
  "Security": {
    "JwtSecret": "YOUR_RANDOM_64_CHAR_SECRET_HERE"
  },
  "GitHub": {
    "Token": "YOUR_GITHUB_PAT_HERE"
  },
  "Urls": "http://localhost:7979"
}
```

**JwtSecret** — generate any 64+ character random string.
**GitHub Token** — fine-grained PAT for the `thegoddamnbeckster` account.
Token name: **"For Claude"** — see GitHub → Settings → Developer settings.
Expires **Thu Jun 11 2026** — rotate before then.

---

## Step 5 — Configure Ports

`Chronicle\.claude\launch.json` drives VS Code launch configs and the dev
port mapping.  The defaults are:

| Service | Port |
|---------|------|
| Chronicle API | 7979 |
| Chronicle Web (dev) | 8888 |

These are read from `src\Chronicle.Web\ports.json` at runtime.

---

## Step 6 — Build and Deploy Plugins

Each plugin builds to a folder that the API loads at startup.
The API looks for plugins in a `plugins\` subfolder relative to its working directory.

For each plugin repo, run its publish script or build manually:

```powershell
# Example — TMDB plugin
cd Chronicle.Plugin.TMDB
dotnet build -c Release
# Copy output DLL + manifest.json → Chronicle\plugins\chronicle.plugin.tmdb\
```

A convenience pattern (repeat for each plugin):

```powershell
$pluginId = "chronicle.plugin.tmdb"   # change per plugin
$pluginDir = "..\Chronicle\plugins\$pluginId"
New-Item -ItemType Directory -Force $pluginDir
dotnet build -c Release
Copy-Item "bin\Release\net9.0\*.dll"  $pluginDir
Copy-Item "manifest.json"             $pluginDir
```

Plugin folder mapping:

| Repo | Plugin ID / folder |
|------|--------------------|
| Chronicle.Plugin.TMDB | `chronicle.plugin.tmdb` |
| Chronicle.Plugin.MusicBrainz | `chronicle.plugin.musicbrainz` |
| Chronicle.Plugin.Trakt | `chronicle.plugin.trakt` |
| Chronicle.Plugin.Simkl | `chronicle.plugin.simkl` |

---

## Step 7 — Run the App

**API** (from `Chronicle\src\Chronicle.API`):

```powershell
dotnet run
```

**Frontend dev server** (from `Chronicle\src\Chronicle.Web`):

```powershell
npm run dev
```

Open <http://localhost:8888>.
The first user you register is automatically an **admin**.

---

## Step 8 — Run Tests

```powershell
cd Chronicle
dotnet test src\Chronicle.sln
```

Expected baseline: **224 unit + 118 integration = 342 passing**.

---

## Configuring Plugin API Keys

After the app is running, go to **Settings → Plugins** and enter API keys:

| Plugin | Where to get key |
|--------|-----------------|
| TMDB | [themoviedb.org/settings/api](https://www.themoviedb.org/settings/api) — free |
| Trakt | [trakt.tv/oauth/applications](https://trakt.tv/oauth/applications) — create app, get Client ID + Secret |
| SIMKL | [simkl.com/settings/developer](https://simkl.com/settings/developer) — create app |
| MusicBrainz | No key required — uses anonymous API |

---

## Development Notes

- Never run the test environment script from an **elevated** PowerShell shell —
  admin mode hides network drive mappings.  Use a normal (non-admin) shell.
- `appsettings.Development.json` is `.gitignore`d.  Never commit it.
- Plugin DLLs are loaded with an isolated `PluginLoadContext`.
  `Chronicle.Plugins.dll` stays in the host — do **not** copy it into plugin output.
- The SQLite DB (`chronicle.db`) lives next to the API executable by default.
  Back it up before migrations.
- EF Core 9 InMemory enforces FK constraints — integration tests must seed all
  FK-referenced entities.

---

## Resuming a Dev Session with Claude Code

From the main `Chronicle\` directory:

```powershell
claude
```

Claude Code reads the project memory from
`C:\Users\<user>\.claude\projects\W--Scripts-Chronicle\memory\`
and will have full context of where the project was left off,
including the current bug list, architectural decisions, and backlog.

If the memory path differs on the new machine (different drive letter / username),
the memory directory will be empty on first run — Claude will rebuild it from
`docs\SETUP.md` and the git history.  Brief Claude at session start with:
"This is a fresh machine — read SETUP.md and the recent git log to get context."

---

## Project Status (as of 2026-04-28)

- **Phase 2 in progress** — inbound sync (Trakt, SIMKL), metadata enrichment, audiobook support all active
- Main branch is clean and pushed
- Active bugs tracked in `C:\Users\<user>\.claude\projects\W--Scripts-Chronicle\memory\bugs.md`
  (or search recent git log for `fix(` commits)
- Next planned features: Fanart.tv plugin, plugin update notifications, global search
