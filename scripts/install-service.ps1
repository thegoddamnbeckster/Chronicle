#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs Chronicle as a Windows service.

.DESCRIPTION
    Registers Chronicle.API.exe as a Windows service that starts automatically on boot and
    restarts itself automatically if it ever crashes or is killed -- root-caused live
    (2026-09-29): Chronicle running as a plain background process had no supervisor at all, so
    when the host environment restarted, the process silently vanished until someone happened
    to notice. A Windows Service with recovery actions configured (this script's whole point)
    doesn't have that gap: SCM brings it back on its own, and it restarts on the next boot too.

    Run this script once after publish-windows.ps1. To update Chronicle afterward, re-run
    publish-windows.ps1 (or copy new files over $InstallPath yourself), then either restart the
    service (Restart-Service Chronicle) or re-run this script -- it's idempotent and detects an
    existing install/port/account and reconfigures in place rather than erroring.

.PARAMETER InstallPath
    Directory where Chronicle.API.exe was published. Default: C:\Chronicle

.PARAMETER Port
    Port Chronicle listens on. Default: 7979 (Chronicle's own documented default -- see
    CLAUDE.md and PortManager.cs). Written to a ports.json in $InstallPath so it's visible and
    editable after install too, not just a one-time install-time choice.

.PARAMETER ServiceUser
    Windows account to run the service as. Use one of:
      LocalService     (default) — Limited network access. Recommended for most users.
      NetworkService             — Needed if Chronicle accesses network shares.
      LocalSystem                — Full local access. Use only if others fail.
      DOMAIN\username            — Custom domain/local account (requires -ServicePassword).

.PARAMETER ServicePassword
    Password for a custom service account. Leave blank for built-in accounts.

.PARAMETER SkipFirewallRule
    Don't create a Windows Firewall inbound rule for -Port. Set this if you manage firewall
    rules yourself, or Chronicle should stay unreachable from other machines on the network.

.EXAMPLE
    .\install-service.ps1
    .\install-service.ps1 -InstallPath "D:\Apps\Chronicle" -Port 9000
    .\install-service.ps1 -ServiceUser "NetworkService"
    .\install-service.ps1 -ServiceUser "MYPC\chronicleuser" -ServicePassword "P@ssword1"
#>
param(
    [string]$InstallPath     = "C:\Chronicle",
    [int]$Port               = 7979,
    [string]$ServiceUser     = "LocalService",
    [string]$ServicePassword = "",
    [switch]$SkipFirewallRule
)

$ErrorActionPreference = "Stop"
$ServiceName = "Chronicle"
$DisplayName = "Chronicle Media Tracker"
$Description = "Self-hosted universal media tracking platform."
$ExePath     = Join-Path $InstallPath "Chronicle.API.exe"
$FirewallRuleName = "Chronicle"

# ── Validate ──────────────────────────────────────────────────────────────────
if (-not (Test-Path $ExePath)) {
    Write-Error "Chronicle.API.exe not found at '$ExePath'. Run publish-windows.ps1 first (e.g. .\publish-windows.ps1 -OutputDir `"$InstallPath`")."
    exit 1
}
if ($Port -lt 1 -or $Port -gt 65535) {
    Write-Error "Port must be between 1 and 65535 (got $Port)."
    exit 1
}

# ── Write ports.json ──────────────────────────────────────────────────────────
# PortManager.LoadConfig reads this from the app's own directory (see its own doc for the
# full precedence order) -- this is the persistent, visible-after-install way to change the
# port later without re-running this script, just by editing one file next to the exe.
$portsJsonPath = Join-Path $InstallPath "ports.json"
@{ api = $Port } | ConvertTo-Json | Set-Content $portsJsonPath
Write-Host "Wrote $portsJsonPath (api: $Port)"

# ── Remove existing service if present (idempotent re-install/reconfigure) ────────────────
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
    sc.exe create $ServiceName `
        binPath= "`"$ExePath`"" `
        start= auto `
        obj= $scUser `
        DisplayName= $DisplayName | Out-Null
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

# ── Recovery actions ───────────────────────────────────────────────────────────
# The actual fix for the incident that prompted this script: restart automatically if
# Chronicle crashes or is killed, instead of staying down until a human notices. No native
# PowerShell cmdlet configures this (New-Service/Set-Service don't expose it) -- sc.exe failure
# is the only way. reset=86400 means the failure count (which action/actions/... indexes into)
# clears after a full day with no further failures, so one bad day doesn't permanently exhaust
# the "restart" actions and silently fall through to "take no action" on some later, unrelated
# crash weeks from now.
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null
sc.exe failureflag $ServiceName 1 | Out-Null

# ── Firewall rule ──────────────────────────────────────────────────────────────
if (-not $SkipFirewallRule) {
    Remove-NetFirewallRule -DisplayName $FirewallRuleName -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName $FirewallRuleName -Direction Inbound -Protocol TCP `
        -LocalPort $Port -Action Allow | Out-Null
    Write-Host "Firewall rule '$FirewallRuleName' allows inbound TCP $Port."
}

# ── Start & verify ────────────────────────────────────────────────────────────
Start-Service -Name $ServiceName
Start-Sleep -Seconds 2
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-Error "Service installation failed. Check the output above for errors."
    exit 1
}
if ($svc.Status -ne "Running") {
    Write-Warning "Service was created but is not running (status: $($svc.Status)). Check $InstallPath\logs\ and Windows Event Viewer for why."
}

Write-Host ""
Write-Host "Chronicle service installed successfully." -ForegroundColor Green
Write-Host "  Install path      : $InstallPath"
Write-Host "  Service user      : $ServiceUser"
Write-Host "  Start type        : Automatic (survives a machine/VM restart)"
Write-Host "  Recovery          : restarts itself automatically on crash/hang"
Write-Host "  Port              : $Port"
Write-Host "  Status            : $($svc.Status)"
Write-Host ""
Write-Host "Open Chronicle : http://localhost:$Port"
Write-Host "View logs      : $InstallPath\logs\"
Write-Host ""
Write-Host "To change the port later: edit $portsJsonPath and run 'Restart-Service Chronicle', or re-run this script with -Port."
Write-Host "To change the service account later, use Settings > Service in the Chronicle UI, or re-run this script with -ServiceUser."
