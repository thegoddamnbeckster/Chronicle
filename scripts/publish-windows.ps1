# Chronicle — Windows Publish Script
# Builds a self-contained Windows x64 executable package
# Usage: .\scripts\publish-windows.ps1 [-Version "0.1.0"] [-OutputDir ".\publish"]

param(
    [string]$Version = "0.1.0",
    [string]$OutputDir = ".\publish"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path $PSScriptRoot -Parent

Write-Host "Chronicle v$Version — Windows Publish" -ForegroundColor Cyan
Write-Host "Output: $OutputDir" -ForegroundColor Cyan
Write-Host ""

# ── 1. Clean output directory ─────────────────────────────────────────────────
if (Test-Path $OutputDir) {
    Write-Host "Cleaning previous publish..." -ForegroundColor Yellow
    Remove-Item $OutputDir -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDir | Out-Null

# ── 2. Run tests ──────────────────────────────────────────────────────────────
Write-Host "Running tests..." -ForegroundColor Yellow
& dotnet test "$Root\src\Chronicle.sln" --verbosity quiet
if ($LASTEXITCODE -ne 0) {
    Write-Error "Tests failed. Aborting publish."
    exit 1
}
Write-Host "Tests passed." -ForegroundColor Green

# ── 3. Publish backend ────────────────────────────────────────────────────────
Write-Host "Publishing Chronicle.API..." -ForegroundColor Yellow
& dotnet publish "$Root\src\Chronicle.API\Chronicle.API.csproj" `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output "$OutputDir\api" `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:Version=$Version
if ($LASTEXITCODE -ne 0) { Write-Error "Backend publish failed."; exit 1 }

Write-Host "Backend published." -ForegroundColor Green

# ── 4. Build frontend (if Node is available) ──────────────────────────────────
$nodeAvailable = Get-Command node -ErrorAction SilentlyContinue
if ($nodeAvailable) {
    Write-Host "Building React frontend..." -ForegroundColor Yellow
    Push-Location "$Root\src\Chronicle.Web"
    & npm install --silent
    if ($LASTEXITCODE -ne 0) { Write-Error "npm install failed."; exit 1 }
    & npm run build
    if ($LASTEXITCODE -ne 0) { Write-Error "Frontend build failed."; exit 1 }
    Pop-Location
    # Copy built frontend into API's wwwroot
    $wwwroot = "$OutputDir\api\wwwroot"
    New-Item -ItemType Directory -Path $wwwroot -Force | Out-Null
    Copy-Item -Path "$Root\src\Chronicle.Web\dist\*" -Destination $wwwroot -Recurse
    Write-Host "Frontend built and copied." -ForegroundColor Green
} else {
    Write-Host "Node.js not found — skipping frontend build." -ForegroundColor Yellow
    Write-Host "Install Node.js and re-run to include the web UI." -ForegroundColor Yellow
}

# ── 5. Copy config template ───────────────────────────────────────────────────
$configTemplate = "$OutputDir\appsettings.json"
Copy-Item "$Root\src\Chronicle.API\appsettings.json" $configTemplate
# Reset the JWT secret placeholder in the output copy
(Get-Content $configTemplate) `
    -replace '"JwtSecret": ".*?"', '"JwtSecret": "CHANGE_THIS_TO_A_RANDOM_SECRET_AT_LEAST_32_CHARS"' |
    Set-Content $configTemplate

# ── 6. Create start script ────────────────────────────────────────────────────
@"
@echo off
echo Starting Chronicle...
Chronicle.API.exe
pause
"@ | Set-Content "$OutputDir\Start Chronicle.bat"

# ── 7. Create README ──────────────────────────────────────────────────────────
@"
Chronicle v$Version
==================

SETUP
-----
1. Open appsettings.json and set a strong JwtSecret (32+ random characters)
2. Run "Start Chronicle.bat" (or Chronicle.API.exe directly)
3. Open http://localhost:8080 in your browser
4. Register your account (first account is automatically admin)

CONFIGURATION
-------------
All settings are in appsettings.json:
  - ConnectionStrings.DefaultConnection : SQLite database file path
  - Security.JwtSecret                  : MUST be changed before use
  - Urls                                : Change port here (default :8080)

API
---
Swagger UI: http://localhost:8080/swagger
Scrobble endpoint: POST http://localhost:8080/api/v1/scrobble

DATA
----
The SQLite database (chronicle.db) is created in the same folder as the exe.
Back it up regularly.
"@ | Set-Content "$OutputDir\README.txt"

# ── 8. Build external plugins ─────────────────────────────────────────────────
$PluginsOutDir = Join-Path $OutputDir "plugins"
New-Item -ItemType Directory -Force -Path $PluginsOutDir | Out-Null

foreach ($pluginDir in @(
    "W:\Scripts\Chronicle.Plugin.TMDB",
    "W:\Scripts\Chronicle.Plugin.MusicBrainz"
)) {
    if (Test-Path $pluginDir) {
        Write-Host "Building plugin: $pluginDir" -ForegroundColor Cyan
        $pluginPublish = Join-Path $pluginDir "publish"
        & dotnet publish $pluginDir -c Release -o $pluginPublish --no-self-contained
        if ($LASTEXITCODE -ne 0) { Write-Error "Plugin build failed: $pluginDir"; exit 1 }
        $manifestPath = Join-Path $pluginDir "manifest.json"
        $pluginId = (Get-Content $manifestPath | ConvertFrom-Json).plugin_id
        $dest = Join-Path $PluginsOutDir $pluginId
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        Copy-Item -Path (Join-Path $pluginPublish "*") -Destination $dest -Recurse -Force
        Write-Host "  Deployed $pluginId to $dest" -ForegroundColor Green
    } else {
        Write-Host "  Plugin directory not found, skipping: $pluginDir" -ForegroundColor Yellow
    }
}

# ── 9. Summary ────────────────────────────────────────────────────────────────
$size = (Get-ChildItem $OutputDir -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host ""
Write-Host "Publish complete!" -ForegroundColor Green
Write-Host "  Location : $OutputDir" -ForegroundColor Cyan
Write-Host "  Size     : $([math]::Round($size, 1)) MB" -ForegroundColor Cyan
Write-Host ""
Write-Host "IMPORTANT: Edit appsettings.json and set a strong JwtSecret before distributing." -ForegroundColor Yellow
