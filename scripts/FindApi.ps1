Get-CimInstance Win32_Process | Where-Object { $_.Name -like '*Chronicle*' -or $_.CommandLine -like '*Chronicle.API*' } | Select-Object ProcessId, Name, CommandLine | Format-List
Write-Host "---"
netstat -ano | Select-String ":8080"
