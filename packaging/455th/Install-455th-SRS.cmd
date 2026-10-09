@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-455th-SRS.ps1"
if errorlevel 1 (
  echo.
  echo Installation failed.
  pause
)
