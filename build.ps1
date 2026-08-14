# Build + tests + publish. Usage: ./build.ps1 [-Configuration Release] [-SkipTests]
param(
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$Msi
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

# Fall back to per-user SDK install if no system-wide SDK is present
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -or -not (dotnet --list-sdks 2>$null)) {
    $userDotnet = Join-Path $env:USERPROFILE ".dotnet"
    if (Test-Path (Join-Path $userDotnet "dotnet.exe")) {
        $env:DOTNET_ROOT = $userDotnet
        $env:PATH = "$userDotnet;$env:PATH"
    }
}

Write-Host "== Restore + Build ($Configuration) =="
dotnet build NexusPdf.slnx -c $Configuration
if ($LASTEXITCODE -ne 0) { exit 1 }

if (-not $SkipTests) {
    Write-Host "== Tests =="
    dotnet test tests/NexusPdf.UnitTests -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { exit 1 }
    dotnet test tests/NexusPdf.PdfEngineTests -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { exit 1 }
}

Write-Host "== Publish win-x64 =="
$publishDir = Join-Path $root "artifacts/publish/win-x64"
dotnet publish src/NexusPdf.App.Desktop -c $Configuration -r win-x64 --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "== SHA-256 =="
$exe = Join-Path $publishDir "NexusPdf.exe"
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
"NexusPdf.exe SHA-256: $hash"
$hash | Out-File (Join-Path $root "artifacts/NexusPdf.exe.sha256.txt") -Encoding ascii

if ($Msi) {
    Write-Host "== MSI (WiX 5) =="
    $env:PATH = "$env:USERPROFILE\.dotnet\tools;$env:PATH"
    if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
        dotnet tool install --global wix --version 5.0.2
    }
    wix build installer/Msi/NexusPdf.wxs -bindpath "publish=$publishDir" -o artifacts/NexusPdf.msi
    if ($LASTEXITCODE -ne 0) { exit 1 }
    "MSI: " + (Get-Item artifacts/NexusPdf.msi).Length + " bytes"
}

Write-Host "Done: $publishDir"
