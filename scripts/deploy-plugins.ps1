# deploy-plugins.ps1
# Copies freshly-built plugin DLLs into the Chronicle API plugins directory.
# Run this AFTER stopping the Chronicle API (so file locks are released).
#
# Usage:
#   .\scripts\deploy-plugins.ps1

$ErrorActionPreference = "Stop"

$repo = Split-Path $PSScriptRoot -Parent
$pluginsDir = Join-Path $repo "src\Chronicle.API\plugins"

$plugins = @(
    @{ Name = "chronicle.plugin.tmdb";        Src = "W:\Scripts\Chronicle.Plugin.TMDB\bin\Release\net9.0\Chronicle.Plugin.TMDB.dll" }
    @{ Name = "chronicle.plugin.musicbrainz"; Src = "W:\Scripts\Chronicle.Plugin.MusicBrainz\bin\Release\net9.0\Chronicle.Plugin.MusicBrainz.dll" }
    @{ Name = "chronicle.plugin.fanedit";     Src = "W:\Scripts\Chronicle.Plugin.FanEdit\bin\Release\net9.0\Chronicle.Plugin.FanEdit.dll" }
    @{ Name = "chronicle.plugin.filescanner"; Src = "W:\Scripts\Chronicle.Plugin.FileScanner\bin\Release\net9.0\Chronicle.Plugin.FileScanner.dll" }
)

foreach ($p in $plugins) {
    $dest = Join-Path $pluginsDir "$($p.Name)\$(Split-Path $p.Src -Leaf)"
    if (-not (Test-Path $p.Src)) {
        Write-Warning "DLL not found: $($p.Src) — build the plugin first"
        continue
    }
    Copy-Item -Path $p.Src -Destination $dest -Force
    Write-Host "Deployed $($p.Name)" -ForegroundColor Green
}

Write-Host "Done. Restart the Chronicle API to load the updated plugins." -ForegroundColor Cyan
