@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-455th-SRS.ps1"
if errorlevel 1 (
  echo.
  echo Uninstall failed.
  pause
)
