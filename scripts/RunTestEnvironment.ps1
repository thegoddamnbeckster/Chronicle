<#
.SYNOPSIS
    Starts the Chronicle development environment (API + Web frontend).

.DESCRIPTION
    Kills any running Chronicle dev processes, then launches:
      - Chronicle.API   on http://localhost:7979
      - Chronicle.Web   on http://localhost:8888

    Both processes run in separate console windows so you can see their logs.
    Close either window or press Ctrl+C in it to stop that process.

    Run this script from anywhere — it locates the repo root automatically.

    IMPORTANT: Run from a NON-elevated (non-Admin) shell. Windows hides
    network drive mappings from elevated processes, so H: and other mapped
    drives will be missing from the folder picker if run as Administrator.

.PARAMETER ApiOnly
    Start only the API, not the frontend.

.PARAMETER WebOnly
    Start only the frontend, not the API.
#>
param(
    [switch]$ApiOnly,
    [switch]$WebOnly
)

$RepoRoot   = Split-Path $PSScriptRoot -Parent
$ApiProject = Join-Path $RepoRoot "src\Chronicle.API\Chronicle.API.csproj"
$ApiDir     = Split-Path $ApiProject -Parent
$WebDir     = Join-Path $RepoRoot "src\Chronicle.Web"
$DbPath     = Join-Path $ApiDir "chronicle-dev.db"
$LogDir     = Join-Path $ApiDir "logs"
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
Write-Host ""

# ── Kill existing dev processes ───────────────────────────────────────────────
# Must happen BEFORE building/copying plugin DLLs — the running API holds file
# locks on the DLLs in the plugins/ folder, causing Copy-Item to fail.
Write-Host "Stopping any running Chronicle dev processes..." -ForegroundColor Yellow

# Kill Chronicle.API.exe (published build running as a standalone process)
Get-Process -Name "Chronicle.API" -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host "  Stopping Chronicle.API PID $($_.Id)"; Stop-Process $_ -Force }

# Kill dotnet run processes for Chronicle.API (dev build)
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -match "Chronicle\.API" } |
    ForEach-Object { Write-Host "  Stopping dotnet PID $($_.ProcessId)"; Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

# Kill node/vite dev server
Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -match "Chronicle\.Web" } |
    ForEach-Object { Write-Host "  Stopping node PID $($_.ProcessId)"; Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

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
        OutputDir  = Join-Path $PluginsDir "tmdb"
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
        Project    = Join-Path (Split-Path $RepoRoot -Parent) "Chronicle.Plugin.Simkl\Chronicle.Plugin.Simkl.csproj"
        DllName    = "Chronicle.Plugin.Simkl.dll"
        OutputDir  = Join-Path $PluginsDir "chronicle.plugin.simkl"
    },
    @{
        Project    = Join-Path (Split-Path $RepoRoot -Parent) "Chronicle.Plugin.Trakt\Chronicle.Plugin.Trakt.csproj"
        DllName    = "Chronicle.Plugin.Trakt.dll"
        OutputDir  = Join-Path $PluginsDir "chronicle.plugin.trakt"
    }
)

foreach ($plugin in $PluginProjects) {
    if (-not (Test-Path $plugin.Project)) {
        Write-Host "  [SKIP] Plugin project not found: $($plugin.Project)" -ForegroundColor DarkYellow
        continue
    }
    Write-Host "  Building $($plugin.DllName)..." -ForegroundColor DarkCyan -NoNewline
    $result = dotnet build $plugin.Project -c Debug --no-restore -v quiet 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host " FAILED" -ForegroundColor Red
        Write-Host $result
        continue
    }
    $srcDll = Join-Path (Split-Path $plugin.Project -Parent) "bin\Debug\net9.0\$($plugin.DllName)"
    if (Test-Path $srcDll) {
        New-Item -ItemType Directory -Path $plugin.OutputDir -Force | Out-Null
        Copy-Item -Path $srcDll -Destination (Join-Path $plugin.OutputDir $plugin.DllName) -Force
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
    Start-Process pwsh -ArgumentList "-NoExit", "-Command",
        "`$env:ASPNETCORE_ENVIRONMENT='Development'; cd '$ApiDir'; dotnet run 2>&1" `
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
        "cd '$WebDir'; npm run dev 2>&1" `
        -WindowStyle Normal
}

# ── Done ──────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Chronicle dev environment starting up." -ForegroundColor Green
Write-Host "  API  : http://localhost:7979"
Write-Host "  Web  : http://localhost:8888"
Write-Host "  Logs : src\Chronicle.API\logs\"
Write-Host ""
Write-Host "Tip: For a stable background service, run .\scripts\install-service.ps1"
Write-Host "     as Administrator after publishing with .\scripts\publish-windows.ps1"
Write-Host ""
