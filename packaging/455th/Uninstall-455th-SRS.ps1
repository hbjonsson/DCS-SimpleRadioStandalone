param(
    [string]$InstallPath = (Join-Path $env:LOCALAPPDATA "Programs\455th-SRS"),
    [switch]$RemoveSettings
)

$ErrorActionPreference = "Stop"
$desktopShortcut = Join-Path ([Environment]::GetFolderPath("Desktop")) "455th SRS.lnk"
$startMenuDirectory = Join-Path ([Environment]::GetFolderPath("Programs")) "455th SRS"

Remove-Item $desktopShortcut -Force -ErrorAction SilentlyContinue
Remove-Item $startMenuDirectory -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $InstallPath -Recurse -Force -ErrorAction SilentlyContinue

if ($RemoveSettings) {
    Remove-Item (Join-Path $env:LOCALAPPDATA "455th-SRS") -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "455th SRS uninstalled." -ForegroundColor Green
if (-not $RemoveSettings) {
    Write-Host "Settings were retained. Run with -RemoveSettings to delete them."
}
