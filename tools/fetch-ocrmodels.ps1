# Downloads PaddleOCR ONNX models for the RapidOcrNet engine.
# Files are pinned by SHA-256 in tools/ocrmodels.lock.json and are not stored
# in git (tools/ocrmodels/ is in .gitignore). build.ps1 calls this automatically.
#
# Usage: ./fetch-ocrmodels.ps1 [-Packs cyrillic,latin] [-All]
#   default: shared detector + every pack marked isDefault
param(
    [string[]]$Packs,
    [switch]$All
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$lockPath = Join-Path $root "tools\ocrmodels.lock.json"
$targetDir = Join-Path $root "tools\ocrmodels"

if (-not (Test-Path $lockPath)) { throw "Not found: $lockPath" }
$lock = Get-Content $lockPath -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not (Test-Path $targetDir)) { New-Item -ItemType Directory -Path $targetDir -Force | Out-Null }

# ModelScope answers 403 without a browser User-Agent — this is not optional.
# A fresh client per file as well: reusing one across several downloads made
# ModelScope answer 403 on the second file.
function New-Client {
    $c = New-Object System.Net.WebClient
    $c.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)")
    $c.Headers.Add("Accept", "*/*")
    return $c
}

function Get-Pinned {
    param([string]$Name, [string]$Url, [string]$Sha256)

    $path = Join-Path $targetDir $Name
    if (Test-Path $path) {
        if (-not $Sha256) {
            Write-Host "ocrmodels: $Name is present (no pin to verify)"
            return
        }
        $have = (Get-FileHash $path -Algorithm SHA256).Hash.ToLower()
        if ($have -eq $Sha256.ToLower()) {
            Write-Host "ocrmodels: $Name is up to date"
            return
        }
        Write-Host "ocrmodels: $Name has an unexpected hash, re-downloading"
        Remove-Item $path -Force
    }

    Write-Host "ocrmodels: downloading $Name"
    $temp = "$path.part"
    $lastError = $null
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            $client = New-Client
            try { $client.DownloadFile($Url, $temp) } finally { $client.Dispose() }
            $lastError = $null
            break
        } catch {
            $lastError = $_.Exception.Message
            if (Test-Path $temp) { Remove-Item $temp -Force }
            if ($attempt -lt 3) { Start-Sleep -Seconds (2 * $attempt) }
        }
    }
    if ($lastError) { throw "Failed to download $Name from $Url : $lastError" }

    if ($Sha256) {
        $have = (Get-FileHash $temp -Algorithm SHA256).Hash.ToLower()
        if ($have -ne $Sha256.ToLower()) {
            Remove-Item $temp -Force
            throw "SHA-256 mismatch for $Name. Expected $Sha256, got $have."
        }
    }
    Move-Item $temp $path -Force
    $mb = [math]::Round((Get-Item $path).Length / 1MB, 1)
    Write-Host "ocrmodels: $Name ready ($mb MB)"
}

# Shared models (detector) are needed by every pack.
foreach ($s in $lock.shared) { Get-Pinned -Name $s.name -Url $s.url -Sha256 $s.sha256 }

$wanted = if ($All) {
    $lock.packs
} elseif ($Packs) {
    $lock.packs | Where-Object { $Packs -contains $_.id }
} else {
    $lock.packs | Where-Object { $_.isDefault }
}

if (-not $wanted) { throw "No language packs matched. Available: $(($lock.packs | ForEach-Object { $_.id }) -join ', ')" }

foreach ($p in $wanted) {
    Write-Host "ocrmodels: pack '$($p.id)' - $($p.title)"
    Get-Pinned -Name $p.model.name -Url $p.model.url -Sha256 $p.model.sha256
    # The dictionary defines the recogniser's character set and MUST match the
    # model; upstream publishes no hash for dictionaries, so only presence is
    # checked. A mismatched dictionary produces garbled text, not an error.
    Get-Pinned -Name $p.dict.name -Url $p.dict.url -Sha256 ""
}

Write-Host "ocrmodels: done"
