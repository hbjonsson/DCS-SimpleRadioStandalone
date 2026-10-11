param(
    [string]$InstallPath = (Join-Path $env:LOCALAPPDATA "Programs\455th-SRS")
)

$ErrorActionPreference = "Stop"
$packageRoot = $PSScriptRoot
$source = Join-Path $packageRoot "Client"
$exeName = "455th-SR-ClientRadio.exe"
$clientInstallPath = Join-Path $InstallPath "Client"

if (-not (Test-Path (Join-Path $source $exeName))) {
    throw "The Client payload is missing $exeName. Keep this installer beside the Client folder."
}

New-Item -ItemType Directory -Force -Path $clientInstallPath | Out-Null
Copy-Item (Join-Path $source "*") $clientInstallPath -Recurse -Force

$shell = New-Object -ComObject WScript.Shell
$desktopShortcut = Join-Path ([Environment]::GetFolderPath("Desktop")) "455th SRS.lnk"
$startMenuDirectory = Join-Path ([Environment]::GetFolderPath("Programs")) "455th SRS"
New-Item -ItemType Directory -Force -Path $startMenuDirectory | Out-Null

$exePath = Join-Path $clientInstallPath $exeName
foreach ($shortcutPath in @($desktopShortcut, (Join-Path $startMenuDirectory "455th SRS.lnk"))) {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $exePath
    $shortcut.WorkingDirectory = $clientInstallPath
    $shortcut.Description = "455th SRS Client with OpenFreq receiver DSP"
    $shortcut.IconLocation = "$exePath,0"
    $shortcut.Save()
}

Write-Host "455th SRS installed to $clientInstallPath" -ForegroundColor Green
Write-Host "It uses separate settings in $env:LOCALAPPDATA\455th-SRS."
Write-Host "The official SRS installation and DCS scripts were not modified."
Write-Host "Re-running this installer overwrites the existing Client folder in place."
