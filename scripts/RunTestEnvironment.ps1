<#
.SYNOPSIS
    Starts the Chronicle development environment (API + Web frontend).

.DESCRIPTION
    Kills any running Chronicle dev processes, then launches:
      - Chronicle.API   on http://localhost:8080
      - Chronicle.Web   on http://localhost:3000

    Both processes run in separate console windows so you can see their logs.
    Close either window or press Ctrl+C in it to stop that process.

    Run this script from anywhere — it locates the repo root automatically.

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
$WebDir     = Join-Path $RepoRoot "src\Chronicle.Web"

# ── Kill existing dev processes ───────────────────────────────────────────────
Write-Host "Stopping any running Chronicle dev processes..." -ForegroundColor Yellow

# Kill dotnet run processes for Chronicle.API
Get-Process -Name "dotnet" -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowTitle -match "Chronicle" -or $_.CommandLine -match "Chronicle.API" } |
    ForEach-Object { Write-Host "  Stopping dotnet PID $($_.Id)"; Stop-Process $_ -Force }

# Kill node/vite dev server
Get-Process -Name "node" -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowTitle -match "vite" -or $_.CommandLine -match "Chronicle.Web" } |
    ForEach-Object { Write-Host "  Stopping node PID $($_.Id)"; Stop-Process $_ -Force }

Start-Sleep -Milliseconds 500

# ── Start API ─────────────────────────────────────────────────────────────────
if (-not $WebOnly) {
    if (-not (Test-Path $ApiProject)) {
        Write-Error "API project not found at: $ApiProject"
        exit 1
    }
    Write-Host ""
    Write-Host "Starting Chronicle API (port 8080)..." -ForegroundColor Cyan
    # Set ASPNETCORE_ENVIRONMENT=Development explicitly so appsettings.Development.json
    # is loaded (and appsettings.Production.json with its Docker PostgreSQL string is NOT).
    # --launch-profile is intentionally omitted — the 'Development' profile does not exist
    # in launchSettings.json, which caused the env var to never be set.
    Start-Process pwsh -ArgumentList "-NoExit", "-Command",
        "`$env:ASPNETCORE_ENVIRONMENT='Development'; cd '$RepoRoot'; dotnet run --project '$ApiProject' 2>&1" `
        -WindowStyle Normal
}

# ── Start Web ─────────────────────────────────────────────────────────────────
if (-not $ApiOnly) {
    if (-not (Test-Path $WebDir)) {
        Write-Error "Web directory not found at: $WebDir"
        exit 1
    }
    Write-Host "Starting Chronicle Web (port 3000)..." -ForegroundColor Cyan
    Start-Process pwsh -ArgumentList "-NoExit", "-Command",
        "cd '$WebDir'; npm run dev 2>&1" `
        -WindowStyle Normal
}

# ── Done ──────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Chronicle dev environment starting up." -ForegroundColor Green
Write-Host "  API  : http://localhost:8080"
Write-Host "  Web  : http://localhost:3000"
Write-Host "  Logs : src\Chronicle.API\logs\"
Write-Host ""
Write-Host "Tip: For a stable background service, run .\scripts\install-service.ps1"
Write-Host "     as Administrator after publishing with .\scripts\publish-windows.ps1"
Write-Host ""
