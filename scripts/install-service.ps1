#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs Chronicle as a Windows service.

.DESCRIPTION
    Registers Chronicle.API.exe as a Windows service that starts automatically.
    Run this script once after publishing Chronicle. To update, stop the service,
    overwrite the files, and start it again — no need to re-run this script.

.PARAMETER InstallPath
    Directory where Chronicle.API.exe was published. Default: C:\Chronicle

.PARAMETER ServiceUser
    Windows account to run the service as. Use one of:
      LocalService     (default) — Limited network access. Recommended for most users.
      NetworkService             — Needed if Chronicle accesses network shares.
      LocalSystem                — Full local access. Use only if others fail.
      DOMAIN\username            — Custom domain/local account (requires -ServicePassword).

.PARAMETER ServicePassword
    Password for a custom service account. Leave blank for built-in accounts.

.EXAMPLE
    .\install-service.ps1
    .\install-service.ps1 -InstallPath "D:\Apps\Chronicle"
    .\install-service.ps1 -ServiceUser "NetworkService"
    .\install-service.ps1 -ServiceUser "MYPC\chronicleuser" -ServicePassword "P@ssword1"
#>
param(
    [string]$InstallPath    = "C:\Chronicle",
    [string]$ServiceUser    = "LocalService",
    [string]$ServicePassword = ""
)

$ServiceName = "Chronicle"
$DisplayName = "Chronicle Media Tracker"
$Description = "Self-hosted universal media tracking platform."
$ExePath     = Join-Path $InstallPath "Chronicle.API.exe"

# ── Validate ──────────────────────────────────────────────────────────────────
if (-not (Test-Path $ExePath)) {
    Write-Error "Chronicle.API.exe not found at '$ExePath'. Run the publish script first."
    exit 1
}

# ── Remove existing service if present ────────────────────────────────────────
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping existing Chronicle service..."
    if ($existing.Status -eq "Running") {
        Stop-Service -Name $ServiceName -Force
        Start-Sleep -Seconds 2
    }
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
    Write-Host "Existing service removed."
}

# ── Create service ────────────────────────────────────────────────────────────
$builtInAccounts = @("LocalService", "NetworkService", "LocalSystem")

if ($ServiceUser -in $builtInAccounts) {
    # Built-in accounts use sc.exe because New-Service doesn't support them cleanly.
    $scUser = switch ($ServiceUser) {
        "LocalService"   { "NT AUTHORITY\LocalService" }
        "NetworkService" { "NT AUTHORITY\NetworkService" }
        "LocalSystem"    { "LocalSystem" }
    }
    $result = sc.exe create $ServiceName `
        binPath= "`"$ExePath`"" `
        start= auto `
        obj= $scUser `
        DisplayName= $DisplayName 2>&1
    sc.exe description $ServiceName $Description | Out-Null
} else {
    # Custom domain/local account
    if ([string]::IsNullOrWhiteSpace($ServicePassword)) {
        Write-Error "A -ServicePassword is required for custom account '$ServiceUser'."
        exit 1
    }
    $credential = New-Object System.Management.Automation.PSCredential(
        $ServiceUser,
        (ConvertTo-SecureString $ServicePassword -AsPlainText -Force)
    )
    New-Service `
        -Name $ServiceName `
        -DisplayName $DisplayName `
        -Description $Description `
        -BinaryPathName "`"$ExePath`"" `
        -StartupType Automatic `
        -Credential $credential | Out-Null
}

# ── Verify & report ───────────────────────────────────────────────────────────
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-Error "Service installation failed. Check the output above for errors."
    exit 1
}

Write-Host ""
Write-Host "Chronicle service installed successfully." -ForegroundColor Green
Write-Host "  Install path : $InstallPath"
Write-Host "  Service user : $ServiceUser"
Write-Host "  Start type   : Automatic"
Write-Host ""
Write-Host "Next steps:"
Write-Host "  Start the service : Start-Service $ServiceName"
Write-Host "  Open Chronicle    : http://localhost:8080"
Write-Host "  View logs         : $InstallPath\logs\"
Write-Host ""
Write-Host "To change the service account later, use Settings > Service in the Chronicle UI."
