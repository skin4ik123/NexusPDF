# Downloads Tesseract language models (rus+eng, tessdata_fast) pinned by
# tools/tessdata.lock.json. Files are verified by SHA-256 and are not stored
# in git (tools/tessdata/ is in .gitignore). build.ps1 calls this automatically.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$lock = Get-Content (Join-Path $root "tools\tessdata.lock.json") -Raw | ConvertFrom-Json
$targetDir = Join-Path $root "tools\tessdata"
New-Item -ItemType Directory -Force $targetDir | Out-Null
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

foreach ($file in $lock.files) {
    $target = Join-Path $targetDir $file.name
    if (Test-Path $target) {
        $hash = (Get-FileHash $target -Algorithm SHA256).Hash
        if ($hash -eq $file.sha256) {
            Write-Host "tessdata: $($file.name) is up to date"
            continue
        }
        Remove-Item $target -Force
    }
    Write-Host "tessdata: downloading $($file.name)..."
    $ProgressPreference = "SilentlyContinue"
    Invoke-WebRequest -Uri $file.url -OutFile $target
    $hash = (Get-FileHash $target -Algorithm SHA256).Hash
    if ($hash -ne $file.sha256) {
        Remove-Item $target -Force
        throw "SHA-256 mismatch for $($file.name): expected $($file.sha256), got $hash"
    }
    Write-Host "tessdata: $($file.name) OK"
}
