#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Removes the Chronicle Windows service.

.DESCRIPTION
    Stops and deletes the Chronicle service registration. Your data and config files
    in the install folder are NOT removed.
#>

$ServiceName = "Chronicle"

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-Host "Chronicle service is not installed. Nothing to remove."
    exit 0
}

Write-Host "Stopping Chronicle service..."
if ($svc.Status -eq "Running") {
    Stop-Service -Name $ServiceName -Force
    Start-Sleep -Seconds 2
}

sc.exe delete $ServiceName | Out-Null
Start-Sleep -Seconds 1

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    Write-Error "Service removal failed. You may need to restart Windows to complete removal."
    exit 1
}

Write-Host ""
Write-Host "Chronicle service removed." -ForegroundColor Green
Write-Host "Your data and configuration files have been left in place."
