# daily-restart.ps1
# Restarts the bridge once a day so the Telegram update subscription cannot
# go stale over repeated reconnects. Run by "TG-WA Bridge Daily Restart".
$ErrorActionPreference = "SilentlyContinue"

$Project = Split-Path -Parent $MyInvocation.MyCommand.Path
$ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
Add-Content (Join-Path $Project "watchdog.log") -Value "$ts  DAILY RESTART - scheduled"

Get-CimInstance Win32_Process -Filter "name='node.exe'" |
    Where-Object { $_.CommandLine -like "*wa-service.js*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Get-CimInstance Win32_Process -Filter "name='python.exe'" |
    Where-Object { $_.CommandLine -like "*tg-listener.py*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }

Start-Sleep -Seconds 5
Start-ScheduledTask -TaskName "TG-WA Bridge"
