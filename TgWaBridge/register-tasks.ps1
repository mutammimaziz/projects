# =====================================================================
#  register-tasks.ps1  -  TG-WA Bridge setup (Phase 3)
#  Registers the three scheduled tasks:
#     TG-WA Bridge                 - launches it at startup (2 min delay)
#     TG-WA Bridge Watchdog        - restarts it if dead/wedged (5 min)
#     TG-WA Bridge Daily Restart   - refreshes it once a day (04:00)
#
#  MUST be run as Administrator, and only AFTER the one-time WhatsApp QR
#  scan and Telegram login (a background task cannot do either).
# =====================================================================
$ErrorActionPreference = "Stop"
$Project = Split-Path -Parent $MyInvocation.MyCommand.Path

function Say($m, $c = "White") { Write-Host $m -ForegroundColor $c }

$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Say "This script must be run as Administrator." "Red"
    exit 1
}

Say "TG-WA Bridge - registering scheduled tasks" "Cyan"
Say "Project folder: $Project"

$waAuth = Join-Path $Project "wa-auth"
$tgSess = Join-Path $Project "tg-bridge-session.session"
if (-not (Test-Path $waAuth) -or -not (Test-Path $tgSess)) {
    Say ""
    Say "WARNING: the saved logins were not found." "Yellow"
    if (-not (Test-Path $waAuth)) { Say "  missing: wa-auth\  (WhatsApp QR scan not done)" "Yellow" }
    if (-not (Test-Path $tgSess)) { Say "  missing: tg-bridge-session.session  (Telegram login not done)" "Yellow" }
    Say "Background tasks cannot scan a QR code or type a phone code." "Yellow"
    $go = Read-Host "Continue anyway? (y/N)"
    if ($go -ne "y") { Say "Aborted."; exit 1 }
}

$User = "$env:COMPUTERNAME\$env:USERNAME"
Say ""
Say "Tasks will run as: $User" "Gray"
$sec = Read-Host "Windows password for $User" -AsSecureString
$pw  = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
       [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
if ([string]::IsNullOrWhiteSpace($pw)) { Say "No password entered - aborting." "Red"; exit 1 }

function Register-BridgeTask {
    param($Name, $Action, $Triggers, $Settings)
    if (Get-ScheduledTask -TaskName $Name -ErrorAction SilentlyContinue) {
        Say "  replacing existing task '$Name'" "Gray"
        Unregister-ScheduledTask -TaskName $Name -Confirm:$false
    }
    Register-ScheduledTask -TaskName $Name -Action $Action -Trigger $Triggers `
        -Settings $Settings -RunLevel Highest -User $User -Password $pw | Out-Null
    Say "  [OK] $Name" "Green"
}

$psExe = "powershell.exe"
Say ""
Say "Registering tasks..." "Cyan"

# 1. startup launcher - 2 minute delay so networking is up first
$a1 = New-ScheduledTaskAction -Execute (Join-Path $Project "start-bridge.bat") `
      -WorkingDirectory $Project
$t1 = New-ScheduledTaskTrigger -AtStartup
$t1.Delay = "PT2M"
$s1 = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
      -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit 0 `
      -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
Register-BridgeTask -Name "TG-WA Bridge" -Action $a1 -Triggers $t1 -Settings $s1

# 2. watchdog - every 5 minutes, plus at startup
$a2 = New-ScheduledTaskAction -Execute $psExe `
      -Argument ("-NoProfile -ExecutionPolicy Bypass -File `"" + (Join-Path $Project "bridge-watchdog.ps1") + "`"")
$t2a = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(2) `
       -RepetitionInterval (New-TimeSpan -Minutes 5) `
       -RepetitionDuration (New-TimeSpan -Days 3650)
$t2b = New-ScheduledTaskTrigger -AtStartup
$s2 = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
      -StartWhenAvailable -MultipleInstances IgnoreNew `
      -ExecutionTimeLimit (New-TimeSpan -Minutes 4) `
      -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
Register-BridgeTask -Name "TG-WA Bridge Watchdog" -Action $a2 -Triggers @($t2a,$t2b) -Settings $s2

# 3. daily restart
$a3 = New-ScheduledTaskAction -Execute $psExe `
      -Argument ("-NoProfile -ExecutionPolicy Bypass -File `"" + (Join-Path $Project "daily-restart.ps1") + "`"")
$t3 = New-ScheduledTaskTrigger -Daily -At 4:00AM
$s3 = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
      -StartWhenAvailable -MultipleInstances IgnoreNew `
      -ExecutionTimeLimit (New-TimeSpan -Minutes 5)
Register-BridgeTask -Name "TG-WA Bridge Daily Restart" -Action $a3 -Triggers $t3 -Settings $s3

Write-Host ""
Say "Registered tasks:" "Cyan"
Get-ScheduledTask -TaskName "TG-WA Bridge","TG-WA Bridge Watchdog","TG-WA Bridge Daily Restart" |
    Select-Object TaskName, State | Format-Table -AutoSize

Write-Host ""
Say "Start it:  Start-ScheduledTask -TaskName 'TG-WA Bridge'" "Cyan"
Say "Check it:  powershell -ExecutionPolicy Bypass -File '$Project\status.ps1'" "Cyan"
Write-Host ""
Say "NOTE: if this account's Windows password changes, all three tasks stop" "Yellow"
Say "      running until you re-run this script with the new password." "Yellow"
