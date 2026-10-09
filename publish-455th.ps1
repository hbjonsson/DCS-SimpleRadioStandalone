param(
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot
$productVersion = "2.4.1.0"
$buildOutput = Join-Path $repoRoot "install-build"
$packageRoot = Join-Path $repoRoot "install-build\455th-SRS-Package"
$clientOutput = Join-Path $packageRoot "Client"
$zipPath = Join-Path $buildOutput "455th-SRS-$productVersion-win-x64.zip"
$msiPath = Join-Path $buildOutput "455th-SRS-$productVersion-win-x64.msi"
$localizedMsiPath = Join-Path $buildOutput "en-US\455th-SRS-$productVersion-win-x64.msi"
$emblemPath = Join-Path $repoRoot "packaging\455th-msi\455th-vaew-emblem.png"
$dialogBmpPath = Join-Path $buildOutput "455th-SRS-dialog.bmp"
$bannerBmpPath = Join-Path $buildOutput "455th-SRS-banner.bmp"

Remove-Item $packageRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
Remove-Item $msiPath -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $clientOutput | Out-Null

$publishArgs = @(
    "publish",
    (Join-Path $repoRoot "DCS-SR-Client\DCS-SR-Client.csproj"),
    "--configuration", "Release",
    "--runtime", "win-x64",
    "--output", $clientOutput,
    "/p:DebugType=None",
    "/p:DebugSymbols=false",
    "/p:IncludeSourceRevisionInInformationalVersion=false"
)

if ($FrameworkDependent) {
    $publishArgs += "--no-self-contained"
} else {
    $publishArgs += "--self-contained"
}

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Copy-Item (Join-Path $repoRoot "LICENSE") (Join-Path $clientOutput "LICENSE.txt")
Copy-Item `
    (Join-Path $repoRoot "libs\OpenFreqAudio\LICENSE.md") `
    (Join-Path $clientOutput "OPENFREQAUDIO-LICENSE.txt")
Copy-Item `
    (Join-Path $repoRoot "packaging\455th\SOURCE.txt") `
    (Join-Path $clientOutput "SOURCE.txt")
Copy-Item `
    (Join-Path $repoRoot "packaging\455th\NWAVES-LICENSE.txt") `
    (Join-Path $clientOutput "NWAVES-LICENSE.txt")

# WiX uses BMP artwork. Generate correctly sized dialog/banner assets from the
# supplied 455th emblem while leaving the text side of each dialog uncluttered.
Add-Type -AssemblyName System.Drawing
function New-InstallerBitmap(
    [string]$OutputPath,
    [int]$Width,
    [int]$Height,
    [int]$EmblemSize,
    [int]$EmblemX,
    [int]$EmblemY
) {
    $bitmap = [System.Drawing.Bitmap]::new($Width, $Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $emblem = [System.Drawing.Image]::FromFile($emblemPath)
    try {
        $graphics.Clear([System.Drawing.Color]::White)
        $graphics.InterpolationMode =
            [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.DrawImage($emblem, $EmblemX, $EmblemY, $EmblemSize, $EmblemSize)
        $bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Bmp)
    }
    finally {
        $emblem.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

New-InstallerBitmap $dialogBmpPath 493 312 150 8 72
New-InstallerBitmap $bannerBmpPath 493 58 52 435 3

Copy-Item (Join-Path $repoRoot "packaging\455th\*") $packageRoot -Recurse -Force

$zipCreated = $false
for ($attempt = 1; $attempt -le 3 -and -not $zipCreated; $attempt++) {
    Remove-Item $zipPath -Force -ErrorAction SilentlyContinue

    try {
        Compress-Archive `
            -Path (Join-Path $packageRoot "*") `
            -DestinationPath $zipPath `
            -CompressionLevel Optimal `
            -ErrorAction Stop
        $zipCreated = Test-Path $zipPath
    }
    catch {
        if ($attempt -eq 3) {
            throw
        }

        Write-Warning "ZIP creation attempt $attempt failed; retrying after transient file locks clear."
        Start-Sleep -Seconds 2
    }
}

if (-not $zipCreated) {
    throw "ZIP creation did not produce $zipPath"
}

$wixProject = Join-Path $repoRoot "packaging\455th-msi\455th-SRS.wixproj"
$wixArgs = @(
    "build",
    $wixProject,
    "--configuration", "Release",
    "/p:PayloadDir=$clientOutput",
    "/p:ProductVersion=$productVersion",
    "/p:DialogBmp=$dialogBmpPath",
    "/p:BannerBmp=$bannerBmpPath",
    "/p:OutputPath=$buildOutput\"
)

& dotnet @wixArgs
if ($LASTEXITCODE -ne 0) {
    throw "MSI build failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path $msiPath) -and (Test-Path $localizedMsiPath)) {
    Copy-Item $localizedMsiPath $msiPath -Force
}

if (-not (Test-Path $msiPath)) {
    throw "MSI build did not produce $msiPath"
}

Write-Host "455th SRS packages created:" -ForegroundColor Green
Write-Host $msiPath
Write-Host $zipPath
