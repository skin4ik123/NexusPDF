# Downloads the official qpdf build (Apache-2.0) with pinned version and SHA-256.
# Usage:
#   ./tools/fetch-qpdf.ps1 -Version 12.2.0 -ExpectedSha256 <sha256 of the zip from the release page>
# Take the SHA-256 from https://github.com/qpdf/qpdf/releases
# Without a matching hash the script refuses to install the binary on purpose.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$ExpectedSha256,
    [string]$TargetDir = (Join-Path $PSScriptRoot "qpdf")
)

$ErrorActionPreference = "Stop"

$zipName = "qpdf-$Version-msvc64.zip"
$url = "https://github.com/qpdf/qpdf/releases/download/v$Version/$zipName"
$tmp = Join-Path $env:TEMP $zipName

Write-Host "Downloading $url"
Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing

$actual = (Get-FileHash $tmp -Algorithm SHA256).Hash
if ($actual -ne $ExpectedSha256.ToUpperInvariant()) {
    Remove-Item $tmp -Force
    throw "SHA-256 mismatch: expected $ExpectedSha256, got $actual. File deleted."
}

$extract = Join-Path $env:TEMP "qpdf-extract-$Version"
if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
Expand-Archive $tmp -DestinationPath $extract

New-Item -ItemType Directory -Force $TargetDir | Out-Null
$bin = Get-ChildItem $extract -Recurse -Filter qpdf.exe | Select-Object -First 1
if (-not $bin) { throw "qpdf.exe not found in the archive." }
Copy-Item (Join-Path $bin.DirectoryName "*") $TargetDir -Force

Remove-Item $tmp -Force
Remove-Item $extract -Recurse -Force
Write-Host "qpdf $Version installed to $TargetDir"
