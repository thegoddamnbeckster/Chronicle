$body = @{ username = "admin"; password = "admin" } | ConvertTo-Json
$resp = Invoke-RestMethod -Uri "http://localhost:7979/api/v1/auth/login" -Method POST -ContentType "application/json" -Body $body
Write-Host "Token: $($resp.data.token)"
Write-Host "---PLUGINS---"
$headers = @{ Authorization = "Bearer $($resp.data.token)" }
$plugins = Invoke-RestMethod -Uri "http://localhost:7979/api/v1/plugins" -Headers $headers
$plugins.data | ForEach-Object { Write-Host "$($_.id) | $($_.pluginId) | $($_.name) | $($_.version) | enabled=$($_.isEnabled) | dll=$($_.dllPath)" }
Write-Host "---DIAGNOSTICS---"
$diag = Invoke-RestMethod -Uri "http://localhost:7979/api/v1/diagnostics" -Headers $headers -ErrorAction SilentlyContinue
$diag | ConvertTo-Json -Depth 5
