# TG-WA Bridge

Forwards messages from read-only Telegram alert groups into WhatsApp groups,
routed by source. Runs unattended on a Windows host, starts itself after a
reboot, and recovers from failures on its own.

```
Telegram alert groups ──Telethon──▶ tg-listener.py ──HTTP──▶ wa-service.js ──▶ WhatsApp groups
```

Built for ATFM operations: infrastructure monitoring alerts land in Telegram
groups the team can't post to, while the team coordinates in WhatsApp.

---

## How it works

Two programs run side by side on one Windows host:

| Program | Runtime | Role |
|---------|---------|------|
| `wa-service.js` | Node | Logs into WhatsApp via whatsapp-web.js (headless Chromium). Exposes `/health`, `/groups`, `/send` on `127.0.0.1:3000`, token-authenticated, loopback only. |
| `tg-listener.py` | Python | Logs into Telegram via Telethon, watches the configured groups, POSTs each message to the sender. |

They're decoupled: either can restart without disturbing the other's session.

### Routing

`ROUTES` in `tg-listener.py` maps each Telegram source group id to a WhatsApp
destination group id. Several sources may share one destination. Any Telegram
chat not listed is ignored entirely.

```python
ROUTES = {
    -1001921125702: {"id": "120363033966812513@g.us"},  # AFM-WIN1 -> NCS JTC Server Team
    -1001938490464: {"id": "120363033966812513@g.us"},  # AFM-UNX1 -> NCS JTC Server Team
    -1001924561050: {"id": "120363046067303105@g.us"},  # AFM-DBA1 -> NCS Team at JTC
}
```

---

## Requirements

- Windows 10/11 x64, ~2 GB free disk (Chromium is the bulk)
- **Node.js** LTS x64 — <https://nodejs.org>
- **Python** 3.11+ x64 — <https://python.org> (tick *Add python.exe to PATH*)
- A **Telegram account** that is a member of the source groups (read-only is fine)
- A **WhatsApp account** on a phone that can post to the destination groups
- Local Administrator rights (only to register the scheduled tasks)

> Avoid the Microsoft Store build of Python — its `WindowsApps` alias can fail
> when launched from a background scheduled task. `setup.ps1` warns if it finds one.

---

## Install

```powershell
git clone <this-repo> C:\tg-wa-bridge
cd C:\tg-wa-bridge
powershell -ExecutionPolicy Bypass -File .\setup.ps1
```

`setup.ps1` verifies the runtimes, installs Node and Python dependencies, and
writes a `.env` from your answers.

### Telegram credentials

Get `api_id` / `api_hash` from <https://my.telegram.org> → *API development
tools*. The login code arrives **inside the Telegram app**, not by SMS.

### One-time logins

Background tasks can't scan a QR code or type a phone code, so do these once
interactively:

```powershell
node wa-service.js          # scan QR via WhatsApp > Linked Devices, wait for
                            # "[wa] client ready", then Ctrl+C
python list-groups.py       # Telegram phone login; prints group chat_ids
```

With the sender running, get the WhatsApp group ids:

```powershell
curl.exe -H "X-Bridge-Token: YOUR_TOKEN" http://127.0.0.1:3000/groups
```

> `curl.exe` with the extension — bare `curl` in PowerShell is an alias for
> `Invoke-WebRequest` and takes different arguments.

Put the ids into `ROUTES` in `tg-listener.py`, then validate:

```powershell
python -c "import ast; ast.parse(open('tg-listener.py').read()); print('ok')"
```

### Test before automating

```powershell
.\start-bridge.bat
Start-Sleep 45
Get-Content .\tg-listener.log -Tail 15
```

Expect `Bridge running.` with the routes listed. Then have a real alert posted
and confirm a `forwarded -> ...@g.us` line appears. **Processes being alive is
not proof that messages flow — only that line is.**

### Automate

```powershell
# as Administrator
powershell -ExecutionPolicy Bypass -File .\register-tasks.ps1
```

| Task | Trigger | Purpose |
|------|---------|---------|
| `TG-WA Bridge` | At startup (+2 min) | Launches both programs |
| `TG-WA Bridge Watchdog` | Every 5 min, and at startup | Restarts a dead or wedged bridge |
| `TG-WA Bridge Daily Restart` | Daily 04:00 | Refreshes the Telegram subscription |

---

## Operations

```powershell
# health check: processes + last lines of all three logs
powershell -ExecutionPolicy Bypass -File .\status.ps1

# start / restart
Start-ScheduledTask -TaskName "TG-WA Bridge"

# stop (the task does not own the processes - kill them directly)
Get-CimInstance Win32_Process -Filter "name='node.exe'"   | Where-Object { $_.CommandLine -like "*wa-service.js*" }  | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Get-CimInstance Win32_Process -Filter "name='python.exe'" | Where-Object { $_.CommandLine -like "*tg-listener.py*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
```

Healthy looks like: two processes, `[wa] client ready`, `Bridge running.`

> Disable the watchdog before any maintenance, or it will restart the bridge
> within five minutes.

### Logs

| File | Contents | Lifecycle |
|------|----------|-----------|
| `wa-service.log` | WhatsApp sender | cleared each start |
| `tg-listener.log` | Telegram listener and deliveries | cleared each start |
| `bridge-start.log` | Startup history | accumulates |
| `watchdog.log` | Recoveries and hourly heartbeat | accumulates |

---

## Resilience design

Hard-won from production; each addresses a failure that actually occurred.

- **Telethon retries forever** (`connection_retries=None`) plus an outer loop in
  `main()`. Previously a transient drop exhausted 5 retries and killed the
  process.
- **Chromium launch flags** (`--no-sandbox`, `--disable-gpu`, `--no-zygote`, …)
  — without them Chromium can't start in Task Scheduler's non-interactive
  Session 0.
- **Chromium auto-discovery** — searches the puppeteer cache rather than
  hardcoding a version folder, so an update doesn't break it.
- **2-minute startup delay** — the listener used to start before networking was
  up and fail with `WinError 5 Access is denied`.
- **Watchdog grace period** (3 min) — the launcher staggers node and python by
  20s; without the grace period a check landing in that gap triggered a
  cascade of restarts.
- **Logs cleared each start** — stale tracebacks were repeatedly misread as
  current failures.

---

## Troubleshooting

| Symptom | Cause / fix |
|---------|-------------|
| `SyntaxError: invalid character '»' (U+00BB)` | BOM in a `.py` or `.env`. Rewrite it as UTF-8 without BOM. |
| `ModuleNotFoundError` for telethon/httpx/dotenv | Installed into a different Python. Use `python -m pip install -r requirements.txt`. |
| `{"error":"unauthorized"}` from `/groups` | Wrong token, or a BOM on `.env` line 1. |
| `TimeoutError ... WS endpoint URL` | Chromium can't launch in the background session — confirm the launch flags are present. |
| `detached Frame` / repeated 500s on send | WhatsApp browser context went stale. Restart; the watchdog also catches this. |
| Only reconnects, no `msg from ... -> routing` | Listener subscription went stale. Restart; the daily 04:00 task prevents recurrence. |
| Everything stops after a password change | All three tasks store the old password. Re-run `register-tasks.ps1`. |

> When editing any file in this project, prefer **full-file rewrites** over
> find-and-replace. Partial patching has corrupted scripts here more than once
> (duplicated blocks that still parse). A quick duplication check:
> `(Select-String -Path <file> -Pattern 'ErrorActionPreference').Count` should be 1.

---

## Security

- `.env`, `wa-auth/` and `*.session` are **gitignored** — they hold the API
  hash, the WhatsApp linked-device session and the Telegram session. Never
  commit them.
- The HTTP API binds to `127.0.0.1` only and requires a shared token.
- Scheduled tasks run as the installing user and store that account's password
  in Windows Credential Manager.

---

## Limitations

- **Unofficial WhatsApp path.** There is no official API for posting to normal
  WhatsApp groups; this automates a linked device, which carries a genuine risk
  of the number being banned. Use a dedicated number and keep to operational
  alert volumes.
- **The host must stay powered on.** Auto-start and the watchdog cover crashes
  and reboots; nothing forwards while the machine is off or asleep.
- **The linked phone must stay online** — the WhatsApp session depends on it.
- **Text only** — media-only messages with no caption are skipped.

---

## Repository layout

```
.
├── wa-service.js          WhatsApp sender service (Node)
├── tg-listener.py         Telegram listener and router (Python)
├── list-groups.py         Helper: list Telegram groups with ids
├── start-bridge.bat       Launcher used by the startup task
├── bridge-watchdog.ps1    Auto-recovery (every 5 min)
├── daily-restart.ps1      Daily refresh (04:00)
├── status.ps1             One-command health check
├── setup.ps1              Installer: prerequisites, deps, .env
├── register-tasks.ps1     Installer: scheduled tasks (admin)
├── package.json           Node dependencies
├── requirements.txt       Python dependencies
├── .env.example           Settings template
├── .gitignore             Excludes secrets, sessions, logs, node_modules
└── .gitattributes         CRLF for Windows scripts, LF for the rest
```
