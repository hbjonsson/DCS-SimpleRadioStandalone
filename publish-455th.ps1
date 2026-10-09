param(
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot
$packageRoot = Join-Path $repoRoot "install-build\455th-SRS-Package"
$clientOutput = Join-Path $packageRoot "Client"
$zipPath = Join-Path $repoRoot "install-build\455th-SRS-2.4.1.0-win-x64.zip"

Remove-Item $packageRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
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

Write-Host "455th SRS package created:" -ForegroundColor Green
Write-Host $zipPath
