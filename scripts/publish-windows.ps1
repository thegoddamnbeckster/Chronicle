# Chronicle — Windows Publish Script
# Builds a self-contained Windows x64 executable (no .NET runtime needed on the target
# machine) with the React frontend baked into wwwroot, ready for install-service.ps1.
# Usage: .\scripts\publish-windows.ps1 [-Version "0.1.0"] [-OutputDir ".\publish"] [-SkipTests]

param(
    [string]$Version   = "0.1.0",
    [string]$OutputDir = ".\publish",
    [switch]$SkipTests
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
if (-not $SkipTests) {
    Write-Host "Running tests..." -ForegroundColor Yellow
    & dotnet test "$Root\src\Chronicle.sln" --verbosity quiet
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Tests failed. Aborting publish. Pass -SkipTests to publish anyway (not recommended)."
        exit 1
    }
    Write-Host "Tests passed." -ForegroundColor Green
} else {
    Write-Host "Skipping tests (-SkipTests)." -ForegroundColor Yellow
}

# ── 3. Publish backend ────────────────────────────────────────────────────────
# Publishes directly INTO $OutputDir (not a subfolder) -- install-service.ps1 expects
# Chronicle.API.exe at the root of whatever -InstallPath it's given, so this and that script
# agree on layout without either needing to know about the other's internals.
Write-Host "Publishing Chronicle.API..." -ForegroundColor Yellow
& dotnet publish "$Root\src\Chronicle.API\Chronicle.API.csproj" `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output "$OutputDir" `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:Version=$Version
if ($LASTEXITCODE -ne 0) { Write-Error "Backend publish failed."; exit 1 }

Write-Host "Backend published." -ForegroundColor Green

# ── 4. Build frontend ──────────────────────────────────────────────────────────
# Not optional (unlike the old version of this script) -- Program.cs now serves the web UI
# directly from wwwroot (see its "Serve the built React frontend" comment), so a publish
# without it is a Chronicle with no web UI at all, not a smaller/lesser install.
$nodeAvailable = Get-Command node -ErrorAction SilentlyContinue
if (-not $nodeAvailable) {
    Write-Error "Node.js is required to build the web frontend (Program.cs serves it directly -- there is no API-only publish option). Install Node.js and re-run."
    exit 1
}
Write-Host "Building React frontend..." -ForegroundColor Yellow
Push-Location "$Root\src\Chronicle.Web"
try {
    & npm ci --silent
    if ($LASTEXITCODE -ne 0) { Write-Error "npm ci failed."; exit 1 }
    & npm run build
    if ($LASTEXITCODE -ne 0) { Write-Error "Frontend build failed."; exit 1 }
} finally {
    Pop-Location
}
$wwwroot = Join-Path $OutputDir "wwwroot"
New-Item -ItemType Directory -Path $wwwroot -Force | Out-Null
Copy-Item -Path "$Root\src\Chronicle.Web\dist\*" -Destination $wwwroot -Recurse
Write-Host "Frontend built and copied." -ForegroundColor Green

# ── 5. Create start script (for running by hand, outside the Windows Service) ─────────────
@"
@echo off
echo Starting Chronicle...
Chronicle.API.exe
pause
"@ | Set-Content (Join-Path $OutputDir "Start Chronicle.bat")

# ── 6. Create README ──────────────────────────────────────────────────────────
@"
Chronicle v$Version
==================

SETUP
-----
1. Run "Start Chronicle.bat" (or Chronicle.API.exe directly), or install it as a Windows
   service with install-service.ps1 for it to run automatically and restart itself if it
   ever crashes.
2. Open http://localhost:7979 in your browser (see PORT below to use a different one).
3. Register your account (first account is automatically admin).
4. Settings -> Plugins -> Browse Catalog to add metadata providers, scrobblers, etc.
   Nothing is bundled -- Chronicle itself is the whole download; plugins are opt-in,
   installed and updated from inside the app.

PORT
----
Default is 7979. To use a different one, create a ports.json file next to
Chronicle.API.exe:
  { "api": 9000 }
or set the CHRONICLE_API_PORT environment variable before starting it -- an environment
variable always wins over ports.json if both are set.

DATA
----
Everything Chronicle writes -- the SQLite database, logs\, keys\ (encryption keys; do not
delete or your plugin credentials stop decrypting) -- lives next to Chronicle.API.exe.
Back up the whole folder, or at minimum chronicle.db and keys\, regularly. Nothing is
written anywhere else on the machine.

The JWT signing secret is generated automatically on first run and stored in keys\ --
there is nothing to configure for this.

API
---
Swagger UI: http://localhost:7979/swagger
Scrobble endpoint: POST http://localhost:7979/api/v1/scrobble
"@ | Set-Content (Join-Path $OutputDir "README.txt")

# ── 7. Summary ────────────────────────────────────────────────────────────────
$size = (Get-ChildItem $OutputDir -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host ""
Write-Host "Publish complete!" -ForegroundColor Green
Write-Host "  Location : $OutputDir" -ForegroundColor Cyan
Write-Host "  Size     : $([math]::Round($size, 1)) MB" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next: run Chronicle.API.exe / Start Chronicle.bat directly here, or install it as a" -ForegroundColor Cyan
Write-Host "service: .\scripts\install-service.ps1 -InstallPath `"$((Resolve-Path $OutputDir).Path)`"" -ForegroundColor Cyan
Write-Host "(publish straight to your real install location with -OutputDir if you'd rather skip a copy step, e.g. -OutputDir `"$env:ProgramFiles\Chronicle`")" -ForegroundColor DarkGray
