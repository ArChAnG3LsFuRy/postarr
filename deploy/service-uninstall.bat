@echo off
REM ============================================================================
REM  Postarr - remove the Windows service (run this as Administrator)
REM ============================================================================
net session >nul 2>&1
if %errorlevel% neq 0 (
  echo This script must be run as Administrator.
  pause
  exit /b 1
)

sc.exe stop Postarr
sc.exe delete Postarr
echo.
echo Postarr service removed.
pause
