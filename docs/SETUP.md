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
| **PowerShell 7+** | For build scripts — comes with Windows 11; [download for Windows 10](https://github.com/PowerShell/PowerShell/releases) |

---

## Repository Layout

All repos live in the same parent directory (e.g. `W:\Scripts\` or `C:\Dev\`).
The main repo and every plugin repo are **siblings**:

```
<base>\
  Chronicle\                        ← main application
  Chronicle.Plugin.TMDB\            ← TMDB metadata plugin
  Chronicle.Plugin.MusicBrainz\     ← MusicBrainz metadata plugin
  Chronicle.Plugin.Trakt\           ← Trakt import/sync plugin
  Chronicle.Plugin.Simkl\           ← SIMKL import/sync plugin
  Chronicle.Plugin.FanEdit\         ← FanEdit (IFDB) metadata plugin
  Chronicle.Plugin.Hardcover\       ← Hardcover book/audiobook plugin
```

---

## Step 1 — Clone Everything

```powershell
cd <base>

git clone https://github.com/thegoddamnbeckster/Chronicle.git
git clone https://github.com/thegoddamnbeckster/Chronicle.Plugin.TMDB.git
git clone https://github.com/thegoddamnbeckster/Chronicle.Plugin.MusicBrainz.git
git clone https://github.com/thegoddamnbeckster/Chronicle.Plugin.Trakt.git
git clone https://github.com/thegoddamnbeckster/Chronicle.Plugin.Simkl.git
git clone https://github.com/thegoddamnbeckster/Chronicle.Plugin.FanEdit.git
git clone https://github.com/thegoddamnbeckster/Chronicle.Plugin.Hardcover.git
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

The first run auto-applies EF migrations. Alternatively run manually:

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
**GitHub Token** — a fine-grained PAT for the `thegoddamnbeckster` account with read access to repository contents. Used for plugin catalog lookups. Create one at GitHub → Settings → Developer settings → Fine-grained tokens.

---

## Step 5 — Configure Ports

`Chronicle\.claude\launch.json` drives VS Code launch configs and the dev
port mapping. The defaults are:

| Service | Port |
|---------|------|
| Chronicle API | 7979 |
| Chronicle Web (dev) | 8888 |

These are read from `src\Chronicle.Web\ports.json` at runtime.

---

## Step 6 — Build and Deploy Plugins

Each plugin must be built and its DLL + `manifest.json` copied into the Chronicle plugins directory before it will appear in the UI. The plugins directory lives at:

```
Chronicle\src\Chronicle.API\plugins\{plugin-id}\
```

Build and deploy each plugin:

```powershell
# Run from the plugin repo directory
$pluginId  = "chronicle.plugin.tmdb"   # change per plugin
$pluginDir = "..\Chronicle\src\Chronicle.API\plugins\$pluginId"
New-Item -ItemType Directory -Force $pluginDir
dotnet build -c Release
Copy-Item "bin\Release\net9.0\*.dll" $pluginDir
Copy-Item "manifest.json"            $pluginDir
```

Plugin folder mapping:

| Repo | Plugin ID (= folder name) |
|------|--------------------------|
| Chronicle.Plugin.TMDB | `chronicle.plugin.tmdb` |
| Chronicle.Plugin.MusicBrainz | `chronicle.plugin.musicbrainz` |
| Chronicle.Plugin.Trakt | `chronicle.plugin.trakt` |
| Chronicle.Plugin.Simkl | `chronicle.plugin.simkl` |
| Chronicle.Plugin.FanEdit | `chronicle.plugin.fanedit` |
| Chronicle.Plugin.Hardcover | `hardcover` |

> **Important:** Do **not** copy `Chronicle.Plugins.dll` or `Chronicle.Core.dll` into plugin directories — the host provides them. The plugin `.csproj` files all set `<Private>false</Private>` on the Chronicle.Plugins project reference to prevent this.

A convenience script is available in the main repo:

```powershell
# From Chronicle\
.\scripts\Deploy-Plugin.ps1 -PluginId chronicle.plugin.tmdb -PluginDir "..\Chronicle.Plugin.TMDB"
```

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
dotnet test tests\Chronicle.Tests.Unit\Chronicle.Tests.Unit.csproj
dotnet test tests\Chronicle.Tests.Integration\Chronicle.Tests.Integration.csproj
```

Expected baseline: **264 unit + 123 integration = 387 passing**.

---

## Step 9 — Configuring Plugin API Keys

After the app is running, go to **Settings → Plugins** and enter API keys:

| Plugin | Auth type | Where to get credentials |
|--------|-----------|--------------------------|
| TMDB | API key | [themoviedb.org/settings/api](https://www.themoviedb.org/settings/api) — free |
| MusicBrainz | None | No key required — uses the anonymous API |
| Trakt | OAuth (Client ID + Secret) | [trakt.tv/oauth/applications](https://trakt.tv/oauth/applications) — create an app |
| SIMKL | Client ID + PIN auth | [simkl.com/settings/developer](https://simkl.com/settings/developer) — create an app |
| FanEdit | Username + Password | Your registered [fanedit.org](https://www.fanedit.org) account |
| Hardcover | API token | [hardcover.app/settings](https://hardcover.app/settings) — under API section |

For Trakt and SIMKL, after saving plugin settings, complete the OAuth flow from **Settings → Import**.

---

## Development Notes

- Never run the test environment script from an **elevated** PowerShell shell —
  admin mode hides network drive mappings. Use a normal (non-admin) shell.
- `appsettings.Development.json` is `.gitignore`d. Never commit it.
- Plugin DLLs are loaded with an isolated `PluginLoadContext`.
  `Chronicle.Plugins.dll` stays in the host — do **not** copy it into plugin output directories.
- The SQLite DB (`chronicle.db`) lives next to the API executable by default.
  Back it up before running EF migrations.
- EF Core 9 InMemory enforces FK constraints — integration tests must seed all
  FK-referenced entities.
- The hot-deploy script `scripts\Deploy-Plugin.ps1` builds and reloads a plugin
  without restarting the API. Run it from a non-elevated PowerShell shell.

---

## Project Status (as of 2026-06-06)

- **Phase 2 in progress** — inbound sync (Trakt, SIMKL), metadata enrichment, audiobook support, fan edit support all active
- Main branch is clean and pushed
- Next planned features: Fanart.tv plugin, plugin update notifications
