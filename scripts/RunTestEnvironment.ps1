<#
.SYNOPSIS
    Starts the Chronicle development environment (API + Web frontend).

.DESCRIPTION
    Kills any running Chronicle dev processes, then launches:
      - Chronicle.API                            on http://localhost:7979
      - Chronicle.Web                             on http://localhost:8888
      - Chronicle.Service.MetadataProvider.Audiobookshelf (ABS bridge) on port 9877

    All processes run in separate console windows so you can see their logs.
    Close a window or press Ctrl+C in it to stop that process.

    Run this script from anywhere — it locates the repo root automatically.

    IMPORTANT: Run from a NON-elevated (non-Admin) shell. Windows hides
    network drive mappings from elevated processes, so H: and other mapped
    drives will be missing from the folder picker if run as Administrator.

.PARAMETER ApiOnly
    Start only the API, not the frontend or the ABS bridge.

.PARAMETER WebOnly
    Start only the frontend, not the API or the ABS bridge.

.PARAMETER NoAbsBridge
    Skip starting the AudiobookShelf metadata-provider bridge.
#>
param(
    [switch]$ApiOnly,
    [switch]$WebOnly,
    [switch]$NoAbsBridge
)

$RepoRoot   = Split-Path $PSScriptRoot -Parent
$ApiProject = Join-Path $RepoRoot "src\Chronicle.API\Chronicle.API.csproj"
$ApiDir     = Split-Path $ApiProject -Parent
$WebDir     = Join-Path $RepoRoot "src\Chronicle.Web"
$DbPath     = Join-Path $ApiDir "chronicle-dev.db"
$LogDir     = Join-Path $ApiDir "logs"
$AbsBridgeDir = Join-Path (Split-Path $RepoRoot -Parent) "Chronicle.Service.MetadataProvider.Audiobookshelf"
$Branch     = (git -C $RepoRoot rev-parse --abbrev-ref HEAD 2>$null); if (-not $Branch) { $Branch = "unknown" }
$Commit     = (git -C $RepoRoot rev-parse --short HEAD 2>$null); if (-not $Commit) { $Commit = "unknown" }

# ── Diagnostics ───────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Chronicle Dev Environment — Diagnostics" -ForegroundColor Magenta
Write-Host "  Repo root   : $RepoRoot"
Write-Host "  API project : $ApiProject"
Write-Host "  API dir     : $ApiDir"
Write-Host "  Database    : $DbPath  $(if (Test-Path $DbPath) { '[EXISTS]' } else { '[MISSING - will be created]' })"
Write-Host "  Logs        : $LogDir"
Write-Host "  Branch      : $Branch  ($Commit)"
Write-Host "  ABS bridge  : $AbsBridgeDir  $(if (Test-Path (Join-Path $AbsBridgeDir 'config.ini')) { '[config.ini found]' } else { '[config.ini MISSING - copy config.ini.example and fill it in]' })"
Write-Host ""

# ── Kill existing dev processes ───────────────────────────────────────────────
# Must happen BEFORE building/copying plugin DLLs — the running API holds file
# locks on the DLLs in the plugins/ folder, causing Copy-Item to fail.
Write-Host "Stopping any running Chronicle dev processes..." -ForegroundColor Yellow

# Only stop what this run will actually restart -- same gating as the window-closing
# and "Start API/Web/ABS" sections below. Ungated, an -ApiOnly (or -WebOnly) run would
# kill the other services too and then never bring them back, leaving them down.
if (-not $WebOnly) {
    # Kill Chronicle.API.exe (published build running as a standalone process)
    Get-Process -Name "Chronicle.API" -ErrorAction SilentlyContinue |
        ForEach-Object { Write-Host "  Stopping Chronicle.API PID $($_.Id)"; Stop-Process $_ -Force }

    # Kill dotnet run processes for Chronicle.API (dev build)
    Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match "Chronicle\.API" } |
        ForEach-Object { Write-Host "  Stopping dotnet PID $($_.ProcessId)"; Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

if (-not $ApiOnly) {
    # Kill node/vite dev server
    Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match "Chronicle\.Web" } |
        ForEach-Object { Write-Host "  Stopping node PID $($_.ProcessId)"; Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

if (-not $ApiOnly -and -not $WebOnly -and -not $NoAbsBridge) {
    # Kill the ABS metadata-provider bridge (python service.py)
    Get-CimInstance Win32_Process -Filter "Name='python.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match "Chronicle\.Service\.MetadataProvider\.Audiobookshelf" } |
        ForEach-Object { Write-Host "  Stopping ABS bridge PID $($_.ProcessId)"; Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

# Close the OUTER console windows a previous run of this script left behind. Each was
# launched via `Start-Process pwsh -ArgumentList "-NoExit", "-Command", "...dotnet run"` —
# killing the inner dotnet/node/python process above does NOT close that window, because
# -NoExit keeps the pwsh shell alive after the command inside it dies. Without this, every
# rerun orphans one more empty, idle pwsh window instead of replacing the one it made obsolete.
#
# Only close a window for a service THIS run will actually touch — same gating as the
# "Start API/Web/ABS" sections below. -NoAbsBridge (or -ApiOnly/-WebOnly) means "leave that
# one alone", so a previously-started window for it should survive, not get swept up here.
$SpawnedWindowMarkers = @()
if (-not $WebOnly) { $SpawnedWindowMarkers += $ApiDir }
if (-not $ApiOnly) { $SpawnedWindowMarkers += $WebDir }
if (-not $ApiOnly -and -not $WebOnly -and -not $NoAbsBridge) { $SpawnedWindowMarkers += $AbsBridgeDir }

if ($SpawnedWindowMarkers.Count -gt 0) {
    $SpawnedWindowPattern = ($SpawnedWindowMarkers | ForEach-Object { [regex]::Escape($_) }) -join '|'
    Get-CimInstance Win32_Process -Filter "Name='pwsh.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine -match $SpawnedWindowPattern } |
        ForEach-Object { Write-Host "  Closing previous console window (pwsh PID $($_.ProcessId))"; Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

Start-Sleep -Milliseconds 500

# ── Build & deploy plugins ────────────────────────────────────────────────────
# Plugins live in sibling directories and share Chronicle.Plugins via ProjectReference.
# They must be rebuilt whenever Chronicle.Plugins changes (e.g. interface updates)
# so that the deployed DLLs match the host's interface contract.
$PluginsDir   = Join-Path $ApiDir "plugins"
$PluginProjects = @(
    @{
        Project    = Join-Path (Split-Path $RepoRoot -Parent) "Chronicle.Plugin.TMDB\Chronicle.Plugin.TMDB.csproj"
        DllName    = "Chronicle.Plugin.TMDB.dll"
        OutputDir  = Join-Path $PluginsDir "chronicle.plugin.tmdb"
    },
    @{
        Project    = Join-Path (Split-Path $RepoRoot -Parent) "Chronicle.Plugin.MusicBrainz\Chronicle.Plugin.MusicBrainz.csproj"
        DllName    = "Chronicle.Plugin.MusicBrainz.dll"
        OutputDir  = Join-Path $PluginsDir "chronicle.plugin.musicbrainz"
    },
    @{
        Project    = Join-Path (Split-Path $RepoRoot -Parent) "Chronicle.Plugin.FileScanner\Chronicle.Plugin.FileScanner.csproj"
        DllName    = "Chronicle.Plugin.FileScanner.dll"
        OutputDir  = Join-Path $PluginsDir "chronicle.plugin.filescanner"
    },
    @{
        Project    = Join-Path (Split-Path $RepoRoot -Parent) "Chronicle.Plugin.FanEdit\Chronicle.Plugin.FanEdit.csproj"
        DllName    = "Chronicle.Plugin.FanEdit.dll"
        OutputDir  = Join-Path $PluginsDir "chronicle.plugin.fanedit"
    },
    @{
        Project    = Join-Path (Split-Path $RepoRoot -Parent) "Chronicle.Plugin.MoviesRemastered\Chronicle.Plugin.MoviesRemastered.csproj"
        DllName    = "Chronicle.Plugin.MoviesRemastered.dll"
        OutputDir  = Join-Path $PluginsDir "chronicle.plugin.moviesremastered"
    },
    @{
        Project    = Join-Path (Split-Path $RepoRoot -Parent) "Chronicle.Plugin.Simkl\Chronicle.Plugin.Simkl.csproj"
        DllName    = "Chronicle.Plugin.Simkl.dll"
        OutputDir  = Join-Path $PluginsDir "chronicle.plugin.simkl"
    },
    @{
        Project    = Join-Path (Split-Path $RepoRoot -Parent) "Chronicle.Plugin.Trakt\Chronicle.Plugin.Trakt.csproj"
        DllName    = "Chronicle.Plugin.Trakt.dll"
        OutputDir  = Join-Path $PluginsDir "chronicle.plugin.trakt"
    },
    @{
        Project    = Join-Path (Split-Path $RepoRoot -Parent) "Chronicle.Plugin.Hardcover\Chronicle.Plugin.Hardcover.csproj"
        DllName    = "Chronicle.Plugin.Hardcover.dll"
        OutputDir  = Join-Path $PluginsDir "chronicle.plugin.hardcover"
    },
    @{
        Project    = Join-Path (Split-Path $RepoRoot -Parent) "Chronicle.Plugin.FanartTV\Chronicle.Plugin.FanartTV.csproj"
        DllName    = "Chronicle.Plugin.FanartTV.dll"
        OutputDir  = Join-Path $PluginsDir "chronicle.plugin.fanarttv"
    },
    @{
        Project    = Join-Path (Split-Path $RepoRoot -Parent) "Chronicle.Plugin.Themes.Default\Chronicle.Plugin.Themes.Default.csproj"
        DllName    = "Chronicle.Plugin.Themes.Default.dll"
        OutputDir  = Join-Path $PluginsDir "chronicle.plugin.themes.default"
    },
    @{
        Project    = Join-Path (Split-Path $RepoRoot -Parent) "Chronicle.Plugin.TheTVDB\Chronicle.Plugin.TheTVDB.csproj"
        DllName    = "Chronicle.Plugin.TheTVDB.dll"
        OutputDir  = Join-Path $PluginsDir "chronicle.plugin.thetvdb"
    },
    @{
        Project    = Join-Path (Split-Path $RepoRoot -Parent) "Chronicle.Plugin.TVMaze\Chronicle.Plugin.TVMaze.csproj"
        DllName    = "Chronicle.Plugin.TVMaze.dll"
        OutputDir  = Join-Path $PluginsDir "chronicle.plugin.tvmaze"
    }
)

foreach ($plugin in $PluginProjects) {
    if (-not (Test-Path $plugin.Project)) {
        Write-Host "  [SKIP] Plugin project not found: $($plugin.Project)" -ForegroundColor DarkYellow
        continue
    }
    $projDir = Split-Path $plugin.Project -Parent

    # Restore first so stale or newly-added NuGet packages are resolved.
    # This is fast when nothing has changed (no-op if packages are current).
    Write-Host "  Restoring $($plugin.DllName)..." -ForegroundColor DarkGray -NoNewline
    $restore = dotnet restore $plugin.Project -v quiet 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host " RESTORE FAILED" -ForegroundColor Red
        Write-Host $restore
        continue
    }
    Write-Host " done" -ForegroundColor DarkGray

    Write-Host "  Building  $($plugin.DllName)..." -ForegroundColor DarkCyan -NoNewline
    $result = dotnet build $plugin.Project -c Debug --no-restore -v quiet 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host " FAILED" -ForegroundColor Red
        Write-Host $result
        continue
    }
    $srcDir = Join-Path $projDir "bin\Debug\net9.0"
    $srcDll = Join-Path $srcDir $plugin.DllName
    if (Test-Path $srcDll) {
        New-Item -ItemType Directory -Path $plugin.OutputDir -Force | Out-Null

        # Copy extra dependency DLLs (e.g. HtmlAgilityPack for FanEdit) — any DLL in the
        # build output that isn't a Chronicle assembly and isn't already in the output dir.
        Get-ChildItem $srcDir -Filter "*.dll" | Where-Object {
            $_.Name -ne $plugin.DllName -and
            $_.Name -notlike "Chronicle.*" -and
            $_.Name -notlike "Microsoft.*" -and
            $_.Name -notlike "System.*"
        } | ForEach-Object {
            Copy-Item -Path $_.FullName -Destination (Join-Path $plugin.OutputDir $_.Name) -Force
        }

        Copy-Item -Path $srcDll -Destination (Join-Path $plugin.OutputDir $plugin.DllName) -Force
        $srcManifest = Join-Path $projDir "manifest.json"
        if (-not (Test-Path $srcManifest)) {
            $srcManifest = Join-Path $srcDir "manifest.json"
        }
        if (Test-Path $srcManifest) {
            Copy-Item -Path $srcManifest -Destination (Join-Path $plugin.OutputDir "manifest.json") -Force
        }
        Write-Host " OK" -ForegroundColor Green
    } else {
        Write-Host " DLL not found at $srcDll" -ForegroundColor Red
    }
}

# ── Start API ─────────────────────────────────────────────────────────────────
if (-not $WebOnly) {
    if (-not (Test-Path $ApiProject)) {
        Write-Error "API project not found at: $ApiProject"
        exit 1
    }
    Write-Host ""
    Write-Host "Starting Chronicle API (port 7979)..." -ForegroundColor Cyan
    # Set ASPNETCORE_ENVIRONMENT=Development explicitly so appsettings.Development.json
    # is loaded (and appsettings.Production.json with its Docker PostgreSQL string is NOT).
    # --launch-profile is intentionally omitted — the 'Development' profile does not exist
    # in launchSettings.json, which caused the env var to never be set.
    # $env:NO_COLOR is explicitly cleared — if this script itself is launched from a
    # context that has it set (e.g. a tool/CI wrapper capturing plain-text output), it
    # would otherwise silently disable Serilog's AnsiConsoleTheme in the spawned window,
    # even though that window is a normal interactive console perfectly capable of color.
    Start-Process pwsh -ArgumentList "-NoExit", "-Command",
        "`$env:NO_COLOR=''; `$env:ASPNETCORE_ENVIRONMENT='Development'; cd '$ApiDir'; dotnet run" `
        -WindowStyle Normal
}

# ── Start Web ─────────────────────────────────────────────────────────────────
if (-not $ApiOnly) {
    if (-not (Test-Path $WebDir)) {
        Write-Error "Web directory not found at: $WebDir"
        exit 1
    }
    Write-Host "Starting Chronicle Web (port 8888)..." -ForegroundColor Cyan
    Start-Process pwsh -ArgumentList "-NoExit", "-Command",
        "`$env:NO_COLOR=''; cd '$WebDir'; npm run dev 2>&1" `
        -WindowStyle Normal
}

# ── Start ABS metadata-provider bridge ────────────────────────────────────────
# Standalone sibling process (Python, stdlib only) — not a Chronicle.Plugin.* DLL,
# doesn't load into the API. Starts fine even without config.ini configured yet
# (it warns and keeps running); real use needs a Chronicle API key and shared
# secret filled in there. See its own README for details.
if (-not $ApiOnly -and -not $WebOnly -and -not $NoAbsBridge) {
    if (-not (Test-Path $AbsBridgeDir)) {
        Write-Host "  [SKIP] ABS bridge directory not found: $AbsBridgeDir" -ForegroundColor DarkYellow
    } else {
        Write-Host "Starting ABS metadata-provider bridge (port 9877)..." -ForegroundColor Cyan
        Start-Process pwsh -ArgumentList "-NoExit", "-Command",
            "cd '$AbsBridgeDir'; python service.py" `
            -WindowStyle Normal
    }
}

# ── Done ──────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Chronicle dev environment starting up." -ForegroundColor Green
Write-Host "  API        : http://localhost:7979"
Write-Host "  Web        : http://localhost:8888"
Write-Host "  ABS bridge : http://localhost:9877"
Write-Host "  Logs : src\Chronicle.API\logs\"
Write-Host ""
Write-Host "Tip: For a stable background service, run .\scripts\install-service.ps1"
Write-Host "     as Administrator after publishing with .\scripts\publish-windows.ps1"
Write-Host ""
