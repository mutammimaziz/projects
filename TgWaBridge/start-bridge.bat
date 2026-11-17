@echo off
REM ---------------------------------------------------------------
REM  start-bridge.bat - launches both bridge programs with logging.
REM  Called by the "TG-WA Bridge" scheduled task at startup.
REM  PROJECT is derived from this file's own location.
REM  Program logs are cleared each run so stale errors don't mislead
REM  diagnosis; bridge-start.log and watchdog.log accumulate.
REM ---------------------------------------------------------------
setlocal

set "PROJECT=%~dp0"
if "%PROJECT:~-1%"=="\" set "PROJECT=%PROJECT:~0,-1%"

set "NODE="
for %%P in (node.exe) do if not defined NODE set "NODE=%%~$PATH:P"
if not defined NODE if exist "C:\Program Files\nodejs\node.exe" set "NODE=C:\Program Files\nodejs\node.exe"

set "PYTHON="
for %%P in (python.exe) do if not defined PYTHON set "PYTHON=%%~$PATH:P"
if not defined PYTHON if exist "C:\Python314\python.exe" set "PYTHON=C:\Python314\python.exe"

if not defined NODE   echo [ERROR] node.exe not found   >> "%PROJECT%\bridge-start.log" & exit /b 1
if not defined PYTHON echo [ERROR] python.exe not found >> "%PROJECT%\bridge-start.log" & exit /b 1

cd /d "%PROJECT%"

if exist "%PROJECT%\wa-service.log"  del /q "%PROJECT%\wa-service.log"
if exist "%PROJECT%\tg-listener.log" del /q "%PROJECT%\tg-listener.log"

echo ============================================= >> "%PROJECT%\bridge-start.log"
echo Startup at %DATE% %TIME% >> "%PROJECT%\bridge-start.log"
echo   node:   %NODE%   >> "%PROJECT%\bridge-start.log"
echo   python: %PYTHON% >> "%PROJECT%\bridge-start.log"

start "TG-WA WhatsApp" /min cmd /c ""%NODE%" wa-service.js >> "%PROJECT%\wa-service.log" 2>&1"
timeout /t 20 /nobreak > nul
start "TG-WA Telegram" /min cmd /c ""%PYTHON%" tg-listener.py >> "%PROJECT%\tg-listener.log" 2>&1"

echo Launched both programs. >> "%PROJECT%\bridge-start.log"
endlocal
