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
    Directory where Chronicle.API.exe was published. Default: Program Files\Chronicle (this
    script grants the chosen -ServiceUser explicit write access here, below -- Chronicle
    writes its own database/logs/keys into this same folder, which Program Files does not
    allow by default for anything other than an administrator).

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
    [string]$InstallPath     = (Join-Path ${env:ProgramFiles} "Chronicle"),
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
    # ".\name", not a bare "name" -- New-Service's underlying Win32 API needs an explicit
    # domain/machine qualifier to resolve a LOCAL account correctly instead of misinterpreting
    # it (e.g. trying to resolve it against a domain that doesn't exist here). A name that
    # already has one (a real "DOMAIN\user" or ".\user" the caller passed in) is left alone.
    $qualifiedServiceUser = if ($ServiceUser -match '\\') { $ServiceUser } else { ".\$ServiceUser" }
    $credential = New-Object System.Management.Automation.PSCredential(
        $qualifiedServiceUser,
        (ConvertTo-SecureString $ServicePassword -AsPlainText -Force)
    )
    New-Service `
        -Name $ServiceName `
        -DisplayName $DisplayName `
        -Description $Description `
        -BinaryPathName "`"$ExePath`"" `
        -StartupType Automatic `
        -Credential $credential | Out-Null

    # Root-caused (2026-09-29, while adding custom-account support for SMB share access):
    # New-Service/sc.exe do NOT grant "Log on as a service" (SeServiceLogonRight) to a custom
    # account -- only the Services MMC snap-in does that automatically, as a side effect of its
    # own UI flow. Without it, the service is created successfully but fails to START with a
    # logon failure (Win32 error 1069), silently, the first time SCM tries to start it. No
    # built-in PowerShell cmdlet grants this right; LsaAddAccountRights via P/Invoke is the
    # standard, well-established way to do it from a script. Skipped for the built-in accounts
    # above (LocalService/NetworkService/LocalSystem already have it by design).
    $bareUser = $ServiceUser.Split('\')[-1]
    Add-Type -Namespace ChronicleInstall -Name LsaRights -MemberDefinition @'
using System;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
public struct LSA_UNICODE_STRING {
    public ushort Length;
    public ushort MaximumLength;
    public IntPtr Buffer;
}

[StructLayout(LayoutKind.Sequential)]
public struct LSA_OBJECT_ATTRIBUTES {
    public int Length;
    public IntPtr RootDirectory;
    public IntPtr ObjectName;
    public int Attributes;
    public IntPtr SecurityDescriptor;
    public IntPtr SecurityQualityOfService;
}

public static class Native {
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint LsaOpenPolicy(
        LSA_UNICODE_STRING[] SystemName, ref LSA_OBJECT_ATTRIBUTES ObjectAttributes,
        int DesiredAccess, out IntPtr PolicyHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint LsaAddAccountRights(
        IntPtr PolicyHandle, byte[] AccountSid,
        LSA_UNICODE_STRING[] UserRights, int CountOfRights);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern uint LsaClose(IntPtr ObjectHandle);
}

public static class LsaRightsGranter {
    public static void GrantServiceLogonRight(string accountName) {
        var sid = new System.Security.Principal.NTAccount(accountName)
            .Translate(typeof(System.Security.Principal.SecurityIdentifier))
            as System.Security.Principal.SecurityIdentifier;
        var sidBytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(sidBytes, 0);

        var objectAttributes = new LSA_OBJECT_ATTRIBUTES();
        IntPtr policyHandle;
        uint status = Native.LsaOpenPolicy(null, ref objectAttributes, 0x00000800 /* POLICY_CREATE_ACCOUNT + lookup */, out policyHandle);
        if (status != 0)
            throw new InvalidOperationException("LsaOpenPolicy failed: " + status);

        try {
            var right = "SeServiceLogonRight";
            var rightStr = new LSA_UNICODE_STRING {
                Buffer = Marshal.StringToHGlobalUni(right),
                Length = (ushort)(right.Length * 2),
                MaximumLength = (ushort)((right.Length + 1) * 2)
            };
            var rights = new[] { rightStr };
            status = Native.LsaAddAccountRights(policyHandle, sidBytes, rights, 1);
            Marshal.FreeHGlobal(rightStr.Buffer);
            if (status != 0)
                throw new InvalidOperationException("LsaAddAccountRights failed: " + status);
        } finally {
            Native.LsaClose(policyHandle);
        }
    }
}
'@
    [ChronicleInstall.LsaRights.LsaRightsGranter]::GrantServiceLogonRight("$env:COMPUTERNAME\$bareUser")
    Write-Host "Granted '$bareUser' the right to log on as a service."
}

# ── Grant the service account write access to $InstallPath ────────────────────────────────
# Root-caused live (2026-09-29): Chronicle writes its own database, logs, and encryption keys
# directly into $InstallPath (see Program.cs -- everything is anchored to the app's own
# directory, deliberately, so it works the same way regardless of install location). That's a
# non-issue under C:\Chronicle (a plain folder, writable by anyone by default), but installing
# under Program Files -- which LocalService/NetworkService/a custom account do NOT have write
# access to by default -- would leave the service unable to create chronicle.db on first run.
# Granting Modify (not Full Control) explicitly to just this folder, for just the account this
# service actually runs as, fixes that everywhere without broadening what that account can do
# anywhere else on the machine. Skipped for LocalSystem, which already has full access
# everywhere by design -- an explicit grant would be a no-op.
if ($ServiceUser -ne "LocalSystem") {
    $aclAccount = switch ($ServiceUser) {
        "LocalService"   { "NT AUTHORITY\LocalService" }
        "NetworkService" { "NT AUTHORITY\NetworkService" }
        default          { $ServiceUser }   # custom domain/local account, as given
    }
    icacls $InstallPath /grant "${aclAccount}:(OI)(CI)M" /T /Q | Out-Null
    Write-Host "Granted $aclAccount write access to $InstallPath"
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
