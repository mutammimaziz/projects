# =====================================================================
#  setup.ps1  -  TG-WA Bridge setup (Phase 1)
#  Checks prerequisites, installs dependencies, creates .env.
#  Run from the project folder (no Administrator needed):
#      powershell -ExecutionPolicy Bypass -File .\setup.ps1
# =====================================================================
$ErrorActionPreference = "Stop"
$Project = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $Project

function Say($m, $c = "White") { Write-Host $m -ForegroundColor $c }
function Head($m) { Write-Host ""; Write-Host "=== $m ===" -ForegroundColor Cyan }

Say "TG-WA Bridge setup" "Cyan"
Say "Project folder: $Project"

Head "1/5  Checking prerequisites"
$fail = $false

$node = Get-Command node -ErrorAction SilentlyContinue
if ($node) {
    Say ("  [OK]   Node.js  " + (& node --version) + "   $($node.Source)") "Green"
} else {
    Say "  [MISS] Node.js not found." "Red"
    Say "         Install the LTS x64 MSI from https://nodejs.org then reopen PowerShell." "Yellow"
    $fail = $true
}

$py = Get-Command python -ErrorAction SilentlyContinue
if ($py) {
    $pyReal = & python -c "import sys; print(sys.executable)"
    Say ("  [OK]   Python   " + (& python --version) + "   $pyReal") "Green"
    if ($pyReal -like "*WindowsApps*") {
        Say "  [WARN] This is the Microsoft Store Python alias. It can fail when run" "Yellow"
        Say "         from a background scheduled task. Installing python.org's build" "Yellow"
        Say "         to a plain path (e.g. C:\Python313) is strongly recommended." "Yellow"
    }
} else {
    Say "  [MISS] Python not found." "Red"
    Say "         Install Python 3.11+ x64 from https://python.org (tick 'Add to PATH')." "Yellow"
    $fail = $true
}

if ($fail) {
    Say ""
    Say "Install the missing runtime(s), open a NEW PowerShell window, re-run this script." "Red"
    exit 1
}

Head "2/5  Verifying project files"
$required = @("wa-service.js","tg-listener.py","list-groups.py","package.json",
              "requirements.txt","start-bridge.bat","bridge-watchdog.ps1",
              "daily-restart.ps1",".env.example")
$missing = $required | Where-Object { -not (Test-Path (Join-Path $Project $_)) }
if ($missing) {
    Say "  [MISS] Missing file(s): $($missing -join ', ')" "Red"
    exit 1
}
Say "  [OK]   All $($required.Count) project files present." "Green"

Head "3/5  Installing Node dependencies"
Say "  This downloads a bundled Chromium (~150 MB) - the slow step." "Gray"
& npm install
if ($LASTEXITCODE -ne 0) {
    Say "  npm install failed." "Red"
    Say "  Behind a proxy? Install Google Chrome and set CHROME_PATH in .env to skip" "Yellow"
    Say "  the Chromium download." "Yellow"
    exit 1
}
Say "  [OK]   Node packages installed." "Green"

Head "4/5  Installing Python dependencies"
# 'python -m pip' so packages land in the SAME interpreter that runs the script
& python -m pip install --disable-pip-version-check -r requirements.txt
if ($LASTEXITCODE -ne 0) { Say "  pip install failed." "Red"; exit 1 }
Say "  [OK]   Python packages installed." "Green"

Head "5/5  Creating .env"
$envPath = Join-Path $Project ".env"
if (Test-Path $envPath) {
    Say "  .env already exists - leaving it untouched." "Yellow"
} else {
    $tok = Read-Host "  BRIDGE_TOKEN (blank = auto-generate)"
    if ([string]::IsNullOrWhiteSpace($tok)) {
        $tok = "bridge-" + (-join ((48..57) + (97..122) | Get-Random -Count 12 | ForEach-Object { [char]$_ }))
        Say "  generated: $tok" "Gray"
    }
    $apiId   = Read-Host "  TG_API_ID   (number from my.telegram.org)"
    $apiHash = Read-Host "  TG_API_HASH (hash   from my.telegram.org)"
    if ([string]::IsNullOrWhiteSpace($apiId))   { $apiId   = "0000000" }
    if ([string]::IsNullOrWhiteSpace($apiHash)) { $apiHash = "replace_with_your_api_hash" }

    $body = @"
BRIDGE_TOKEN=$tok
TG_API_ID=$apiId
TG_API_HASH=$apiHash
WA_PORT=3000
WA_SERVICE_URL=http://127.0.0.1:3000
"@
    # UTF-8 WITHOUT BOM - PowerShell 5.1 would otherwise add one, which
    # breaks the first line for both programs.
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($envPath, $body, $utf8NoBom)
    Say "  [OK]   .env created." "Green"
}

Write-Host ""
Say "Phase 1 complete." "Green"
Write-Host ""
Say "Next (see README.md):" "Cyan"
Say "  1. node wa-service.js     -> scan WhatsApp QR, wait for '[wa] client ready', Ctrl+C"
Say "  2. python list-groups.py  -> Telegram login, note alert group chat_ids"
Say "  3. Start the sender again and call /groups for the @g.us ids"
Say "  4. Edit the ROUTES table in tg-listener.py"
Say "  5. Run register-tasks.ps1 as Administrator"
Write-Host ""
