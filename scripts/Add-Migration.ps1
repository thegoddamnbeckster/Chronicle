<#
.SYNOPSIS
    Adds an EF migration to BOTH database providers: SQLite (Chronicle.Data) and PostgreSQL (Chronicle.Data.Postgres).

.DESCRIPTION
    EF migrations are provider-specific, so every model change needs one in each place or PostgreSQL installs fall behind.
    Builds in Release so a running dev API (which locks the Debug output) does not get in the way.

    After generating, look at the PostgreSQL migration: if the change touches media_item_known_file_names.FileName, EF will
    write the SQLite-only NOCASE collation; PostgreSQL needs type "citext" there (see the edit at the top of
    Chronicle.Data.Postgres/Migrations/*_Initial.cs).

.EXAMPLE
    .\scripts\Add-Migration.ps1 AddSomething
#>
param([Parameter(Mandatory = $true)][string]$Name)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location (Join-Path $root 'src')
try {
    Write-Host "SQLite migration $Name..." -ForegroundColor Cyan
    dotnet build Chronicle.API -c Release | Out-Null
    dotnet ef migrations add $Name -p Chronicle.Data -s Chronicle.API -c ChronicleDbContext --configuration Release --no-build
    if ($LASTEXITCODE -ne 0) { throw "SQLite migration failed" }

    Write-Host "PostgreSQL migration $Name..." -ForegroundColor Cyan
    dotnet ef migrations add $Name -p Chronicle.Data.Postgres -s Chronicle.Data.Postgres -c ChronicleDbContext --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "PostgreSQL migration failed" }

    Write-Host "Done. Review both migrations before committing." -ForegroundColor Green
} finally { Pop-Location }
