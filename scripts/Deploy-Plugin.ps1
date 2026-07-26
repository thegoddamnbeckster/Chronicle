<#
.SYNOPSIS
    Hot-deploys an updated plugin DLL to a running Chronicle API without restarting it.

.DESCRIPTION
    Builds the plugin project, calls POST /api/v1/plugins/{id}/unload to release the
    file lock, copies the new DLL, then calls POST /api/v1/plugins/{id}/reload.
    The API stays up throughout. Requires a valid JWT (pass via -Token or set
    $env:CHRONICLE_TOKEN).

.PARAMETER PluginId
    The plugin manifest ID, e.g. "chronicle.plugin.fanedit"

.PARAMETER ApiBase
    API base URL. Defaults to http://localhost:7979

.PARAMETER Token
    JWT bearer token. Falls back to $env:CHRONICLE_TOKEN.

.PARAMETER Release
    Build in Release configuration (default: Debug).

.EXAMPLE
    .\Deploy-Plugin.ps1 chronicle.plugin.fanedit
    .\Deploy-Plugin.ps1 chronicle.plugin.trakt -Release
#>
param(
    [Parameter(Mandatory)]
    [string]$PluginId,

    [string]$ApiBase = "http://localhost:7979",

    [string]$Token = $env:CHRONICLE_TOKEN,

    [switch]$Release
)

$ErrorActionPreference = "Stop"

$RepoRoot   = Split-Path $PSScriptRoot -Parent
$PluginsDir = Join-Path $RepoRoot "src\Chronicle.API\plugins"
$Config     = if ($Release) { "Release" } else { "Debug" }

# ── Map plugin ID to project ──────────────────────────────────────────────────
$PluginMap = @{
    "chronicle.plugin.fanedit"     = @{
        Project = Join-Path (Split-Path $RepoRoot -Parent) "Chronicle.Plugin.FanEdit\Chronicle.Plugin.FanEdit.csproj"
        DllName = "Chronicle.Plugin.FanEdit.dll"
        OutDir  = Join-Path $PluginsDir "chronicle.plugin.fanedit"
    }
    "chronicle.plugin.moviesremastered" = @{
        Project = Join-Path (Split-Path $RepoRoot -Parent) "Chronicle.Plugin.MoviesRemastered\Chronicle.Plugin.MoviesRemastered.csproj"
        DllName = "Chronicle.Plugin.MoviesRemastered.dll"
        OutDir  = Join-Path $PluginsDir "chronicle.plugin.moviesremastered"
    }
    "chronicle.plugin.trakt"       = @{
        Project = Join-Path (Split-Path $RepoRoot -Parent) "Chronicle.Plugin.Trakt\Chronicle.Plugin.Trakt.csproj"
        DllName = "Chronicle.Plugin.Trakt.dll"
        OutDir  = Join-Path $PluginsDir "chronicle.plugin.trakt"
    }
    "chronicle.plugin.simkl"       = @{
        Project = Join-Path (Split-Path $RepoRoot -Parent) "Chronicle.Plugin.Simkl\Chronicle.Plugin.Simkl.csproj"
        DllName = "Chronicle.Plugin.Simkl.dll"
        OutDir  = Join-Path $PluginsDir "chronicle.plugin.simkl"
    }
    "chronicle.plugin.musicbrainz" = @{
        Project = Join-Path (Split-Path $RepoRoot -Parent) "Chronicle.Plugin.MusicBrainz\Chronicle.Plugin.MusicBrainz.csproj"
        DllName = "Chronicle.Plugin.MusicBrainz.dll"
        OutDir  = Join-Path $PluginsDir "chronicle.plugin.musicbrainz"
    }
    "chronicle.plugin.tmdb"        = @{
        Project = Join-Path (Split-Path $RepoRoot -Parent) "Chronicle.Plugin.TMDB\Chronicle.Plugin.TMDB.csproj"
        DllName = "Chronicle.Plugin.TMDB.dll"
        OutDir  = Join-Path $PluginsDir "chronicle.plugin.tmdb"
    }
    "chronicle.plugin.hardcover"   = @{
        Project = Join-Path (Split-Path $RepoRoot -Parent) "Chronicle.Plugin.Hardcover\Chronicle.Plugin.Hardcover.csproj"
        DllName = "Chronicle.Plugin.Hardcover.dll"
        OutDir  = Join-Path $PluginsDir "hardcover"
    }
    "chronicle.plugin.fanarttv"    = @{
        Project = Join-Path (Split-Path $RepoRoot -Parent) "Chronicle.Plugin.FanartTV\Chronicle.Plugin.FanartTV.csproj"
        DllName = "Chronicle.Plugin.FanartTV.dll"
        OutDir  = Join-Path $PluginsDir "chronicle.plugin.fanarttv"
    }
}

if (-not $PluginMap.ContainsKey($PluginId)) {
    Write-Error "Unknown plugin ID '$PluginId'. Known IDs: $($PluginMap.Keys -join ', ')"
    exit 1
}

$p = $PluginMap[$PluginId]

if (-not (Test-Path $p.Project)) {
    Write-Error "Project not found: $($p.Project)"
    exit 1
}

# ── Step 1: Build ─────────────────────────────────────────────────────────────
Write-Host "Building $PluginId ($Config)..." -ForegroundColor Cyan
dotnet build $p.Project -c $Config --no-restore -v quiet
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed."; exit 1 }

$srcDir = Join-Path (Split-Path $p.Project -Parent) "bin\$Config\net9.0"
$srcDll = Join-Path $srcDir $p.DllName
if (-not (Test-Path $srcDll)) { Write-Error "DLL not found at $srcDll"; exit 1 }

# ── Step 2: Unload (release file lock) ────────────────────────────────────────
if ($Token) {
    Write-Host "Unloading $PluginId from API..." -ForegroundColor Yellow
    try {
        $headers = @{ Authorization = "Bearer $Token"; "Content-Length" = "0" }
        $r = Invoke-RestMethod "$ApiBase/api/v1/plugins/$PluginId/unload" -Method Post -Headers $headers
        Write-Host "  Unloaded: $($r.data.status)" -ForegroundColor DarkYellow
        # Give the GC time to release the PluginLoadContext file lock
        Write-Host "  Waiting for GC to release file lock..." -ForegroundColor DarkGray
        Start-Sleep -Milliseconds 2000
    } catch {
        Write-Warning "Unload call failed ($($_.Exception.Message)) — will copy anyway (API may not be running)"
    }
} else {
    Write-Warning "No token supplied (-Token or `$env:CHRONICLE_TOKEN). Skipping unload — API must not be running."
}

# ── Step 3: Copy DLL + manifest + extra deps ──────────────────────────────────
Write-Host "Copying files to $($p.OutDir)..." -ForegroundColor Cyan
New-Item -ItemType Directory -Path $p.OutDir -Force | Out-Null

# Extra dependency DLLs (e.g. HtmlAgilityPack)
Get-ChildItem $srcDir -Filter "*.dll" | Where-Object {
    $_.Name -ne $p.DllName -and
    $_.Name -notlike "Chronicle.*" -and
    $_.Name -notlike "Microsoft.*" -and
    $_.Name -notlike "System.*"
} | ForEach-Object {
    Copy-Item -Path $_.FullName -Destination (Join-Path $p.OutDir $_.Name) -Force
    Write-Host "  + $($_.Name)" -ForegroundColor DarkGray
}

$destDll = Join-Path $p.OutDir $p.DllName
$copied = $false
for ($attempt = 1; $attempt -le 15; $attempt++) {
    try {
        Copy-Item -Path $srcDll -Destination $destDll -Force -ErrorAction Stop
        Write-Host "  + $($p.DllName)" -ForegroundColor Green
        $copied = $true
        break
    } catch {
        if ($attempt -lt 15) {
            Write-Host "  Waiting for file lock to release (attempt $attempt/15)..." -ForegroundColor DarkGray
            Start-Sleep -Milliseconds 1000
        } else {
            throw
        }
    }
}
if (-not $copied) { exit 1 }

$srcManifest = Join-Path $srcDir "manifest.json"
if (Test-Path $srcManifest) {
    Copy-Item -Path $srcManifest -Destination (Join-Path $p.OutDir "manifest.json") -Force
    Write-Host "  + manifest.json" -ForegroundColor Green
}

# ── Step 4: Reload ────────────────────────────────────────────────────────────
if ($Token) {
    Write-Host "Reloading $PluginId in API..." -ForegroundColor Yellow
    try {
        $headers = @{ Authorization = "Bearer $Token"; "Content-Length" = "0" }
        $r = Invoke-RestMethod "$ApiBase/api/v1/plugins/$PluginId/reload" -Method Post -Headers $headers
        Write-Host "  Reloaded: $($r.data.status)" -ForegroundColor Green
    } catch {
        Write-Warning "Reload call failed: $($_.Exception.Message)"
        Write-Host "  Restart the API to pick up the new DLL." -ForegroundColor Yellow
    }
} else {
    Write-Host "Restart the API to pick up the new DLL." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
