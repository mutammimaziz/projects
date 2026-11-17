# status.ps1 - one-command health check for the bridge.
$Project = Split-Path -Parent $MyInvocation.MyCommand.Path

Get-CimInstance Win32_Process -Filter "name='node.exe' or name='python.exe'" |
  Where-Object { $_.CommandLine -like "*wa-service.js*" -or $_.CommandLine -like "*tg-listener.py*" } |
  Select-Object ProcessId, CreationDate, Name | Format-Table -AutoSize

Write-Host "--- WhatsApp ---" -ForegroundColor Cyan
Get-Content (Join-Path $Project "wa-service.log") -Tail 3
Write-Host "--- Telegram ---" -ForegroundColor Cyan
Get-Content (Join-Path $Project "tg-listener.log") -Tail 3
Write-Host "--- Watchdog ---" -ForegroundColor Cyan
Get-Content (Join-Path $Project "watchdog.log") -Tail 3
