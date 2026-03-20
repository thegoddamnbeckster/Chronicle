[System.IO.DriveInfo]::GetDrives() | ForEach-Object {
    Write-Host ("{0,-6} Type={1,-10} IsReady={2}" -f $_.Name, $_.DriveType, $_.IsReady)
}
