@echo off
REM ============================================================================
REM  Postarr - install as a Windows service (run this as Administrator)
REM  The service runs Postarr.exe and serves the web UI on port 5286.
REM ============================================================================
setlocal
net session >nul 2>&1
if %errorlevel% neq 0 (
  echo This script must be run as Administrator.
  echo Right-click it and choose "Run as administrator".
  pause
  exit /b 1
)

set "EXE=%~dp0Postarr.exe"

sc.exe stop Postarr >nul 2>&1
sc.exe delete Postarr >nul 2>&1

sc.exe create Postarr binPath= "\"%EXE%\" --urls http://0.0.0.0:5286" start= auto DisplayName= "Postarr"
sc.exe description Postarr "Postarr - Plex poster and artwork manager"
REM Restart the service automatically if it ever crashes
sc.exe failure Postarr reset= 86400 actions= restart/5000/restart/5000/restart/5000
sc.exe start Postarr

echo.
echo Postarr service installed and started.
echo Open the web UI at:  http://localhost:5286
echo.
pause
