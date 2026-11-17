# bridge-watchdog.ps1
# Restarts the TG-WA bridge if it is dead or wedged.
# Run every 5 minutes by the "TG-WA Bridge Watchdog" scheduled task.
$ErrorActionPreference = "SilentlyContinue"

$Project   = Split-Path -Parent $MyInvocation.MyCommand.Path
$TaskName  = "TG-WA Bridge"
$TgLog     = Join-Path $Project "tg-listener.log"
$WatchLog  = Join-Path $Project "watchdog.log"
$TailLines = 40

function Write-WatchLog($msg) {
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    Add-Content -Path $WatchLog -Value "$ts  $msg"
}

# --- grace period ---
# The launcher starts node, waits 20s, then python. Without this, a check
# landing in that gap sees python missing and fires a second restart.
if (Test-Path $WatchLog) {
    $lastRestart = Get-Content $WatchLog -Tail 30 |
        Where-Object { $_ -match "restart issued" } | Select-Object -Last 1
    if ($lastRestart -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})') {
        $when = [datetime]::ParseExact($matches[1], "yyyy-MM-dd HH:mm:ss", $null)
        if (((Get-Date) - $when) -lt (New-TimeSpan -Minutes 3)) { exit 0 }
    }
}

# --- are both processes alive? ---
$nodeProc = Get-CimInstance Win32_Process -Filter "name='node.exe'" |
    Where-Object { $_.CommandLine -like "*wa-service.js*" }
$pyProc = Get-CimInstance Win32_Process -Filter "name='python.exe'" |
    Where-Object { $_.CommandLine -like "*tg-listener.py*" }

$reason = $null
if (-not $nodeProc)   { $reason = "WhatsApp process (node wa-service.js) not running" }
elseif (-not $pyProc) { $reason = "Telegram process (python tg-listener.py) not running" }

# --- both alive: look for wedged sends ---
if (-not $reason -and (Test-Path $TgLog)) {
    $tail = Get-Content $TgLog -Tail $TailLines
    $lastFailIdx = -1; $lastOkIdx = -1
    for ($i = 0; $i -lt $tail.Count; $i++) {
        if ($tail[$i] -match "detached Frame" -or $tail[$i] -match "giving up on message") { $lastFailIdx = $i }
        if ($tail[$i] -match "forwarded ->") { $lastOkIdx = $i }
    }
    if ($lastFailIdx -ge 0 -and $lastFailIdx -gt $lastOkIdx) {
        $reason = "send failures in recent log with no successful forward after them"
    }
}

if (-not $reason) {
    if ((Get-Date).Minute -lt 5) { Write-WatchLog "OK - bridge healthy" }
    exit 0
}

Write-WatchLog "RESTARTING - reason: $reason"
Get-CimInstance Win32_Process -Filter "name='node.exe'" |
    Where-Object { $_.CommandLine -like "*wa-service.js*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Get-CimInstance Win32_Process -Filter "name='python.exe'" |
    Where-Object { $_.CommandLine -like "*tg-listener.py*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Start-Sleep -Seconds 3
Start-ScheduledTask -TaskName $TaskName
Write-WatchLog "restart issued via scheduled task '$TaskName'"
exit 0
