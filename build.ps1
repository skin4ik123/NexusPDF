# Build + tests + full artifact pipeline.
# Usage: ./build.ps1 [-Configuration Release] [-SkipTests] [-Msi] [-All]
#   -Msi : also build the MSI package
#   -All : MSI + branded Setup.exe + portable ZIP + checksums
param(
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$Msi,
    [switch]$All
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root
# Single source of truth for the version is Directory.Build.props
$propsXml = [xml](Get-Content (Join-Path $root "Directory.Build.props"))
$version = ($propsXml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw "Version not found in Directory.Build.props" }

# The app manifest carries its own copy of the version and is edited by hand.
# Nothing used to compare the two, so a version bump could ship a manifest still
# claiming the previous release. Checked here rather than commented about.
$manifestPath = Join-Path $root "src/NexusPdf.App.Desktop/app.manifest"
$manifestVersion = ([xml](Get-Content $manifestPath)).assembly.assemblyIdentity.version
if ($manifestVersion -ne "$version.0") {
    throw "app.manifest says $manifestVersion but Directory.Build.props says $version (expected $version.0). Update $manifestPath."
}

# Fall back to per-user SDK install if no system-wide SDK is present
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -or -not (dotnet --list-sdks 2>$null)) {
    $userDotnet = Join-Path $env:USERPROFILE ".dotnet"
    if (Test-Path (Join-Path $userDotnet "dotnet.exe")) {
        $env:DOTNET_ROOT = $userDotnet
        $env:PATH = "$userDotnet;$env:PATH"
    }
}
$env:PATH = "$env:USERPROFILE\.dotnet\tools;$env:PATH"

# qpdf: restore pinned binaries if missing (tools/qpdf.lock.json)
if (-not (Test-Path "$root\tools\qpdf\qpdf.exe")) {
    $lock = Get-Content "$root\tools\qpdf.lock.json" | ConvertFrom-Json
    Write-Host "== Fetching qpdf $($lock.version) =="
    & "$root\tools\fetch-qpdf.ps1" -Version $lock.version -ExpectedSha256 $lock.sha256
}

# PaddleOCR models: restore ALL pinned language packs if missing (tools/ocrmodels.lock.json).
# Проверяется каждый файл каталога, а не один «маркерный»: частично
# загруженный набор молча оставил бы половину языков нерабочей.
$ocrLock = Get-Content "$root\tools\ocrmodels.lock.json" -Raw | ConvertFrom-Json
$ocrNeeded = @($ocrLock.shared | ForEach-Object { $_.name })
foreach ($p in $ocrLock.packs) { $ocrNeeded += $p.model.name; $ocrNeeded += $p.dict.name }
$ocrMissing = @($ocrNeeded | Where-Object { -not (Test-Path (Join-Path "$root\tools\ocrmodels" $_)) })
if ($ocrMissing.Count -gt 0) {
    Write-Host "== Fetching OCR models ($($ocrMissing.Count) files missing) =="
    & "$root\tools\fetch-ocrmodels.ps1" -All
    if ($LASTEXITCODE -ne 0) { exit 1 }
}

# Tesseract language models: restore pinned files if missing (tools/tessdata.lock.json)
if (-not (Test-Path "$root\tools\tessdata\rus.traineddata") -or
    -not (Test-Path "$root\tools\tessdata\eng.traineddata")) {
    Write-Host "== Fetching tessdata (rus+eng) =="
    & "$root\tools\fetch-tessdata.ps1"
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
    # Печать проверялась только вручную: проект существовал и был в решении,
    # но ни сборка, ни CI его не запускали, то есть целый набор тестов не
    # относился к выпуску вообще.
    dotnet test tests/NexusPdf.PrintingTests -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { exit 1 }
}

# Предкомпиляция в машинный код (ReadyToRun). Без неё первый запуск после
# установки или перезагрузки уходит на JIT: замерено 4,8 с против 2,6 с на том
# же файле. Плата — около 34 МБ к размеру, и она того стоит: холодный запуск
# пользователь видит каждый рабочий день, а лишние мегабайты — один раз.
Write-Host "== Publish app win-x64 =="
$publishDir = Join-Path $root "artifacts/publish/win-x64"
dotnet publish src/NexusPdf.App.Desktop -c $Configuration -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -o $publishDir
if ($LASTEXITCODE -ne 0) { exit 1 }

# CLI publishes into the SAME folder: shares the self-contained runtime files
Write-Host "== Publish CLI win-x64 =="
dotnet publish src/NexusPdf.Cli -c $Configuration -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -o $publishDir
if ($LASTEXITCODE -ne 0) { exit 1 }

# Свои символы встроены в сборки (DebugType=embedded в Directory.Build.props),
# но сторонние пакеты кладут рядом собственные .pdb. Самый крупный — 80 МБ
# символов SkiaSharp: пятая часть всей загрузки ради отладки чужой библиотеки,
# исходников которой здесь всё равно нет. Папка уезжает в MSI и в архив целиком,
# поэтому чистить надо тут, до упаковки.
$strayPdb = Get-ChildItem $publishDir -Recurse -Filter *.pdb
if ($strayPdb) {
    $mb = ($strayPdb | Measure-Object Length -Sum).Sum / 1MB
    Write-Host ("== Strip {0} third-party .pdb ({1:N1} MB) ==" -f $strayPdb.Count, $mb)
    $strayPdb | Remove-Item -Force
}

# Bundle qpdf + tessdata + notices + license with the app
New-Item -ItemType Directory -Force (Join-Path $publishDir "tools/qpdf") | Out-Null
Copy-Item "$root\tools\qpdf\*" (Join-Path $publishDir "tools/qpdf") -Force
New-Item -ItemType Directory -Force (Join-Path $publishDir "tools/tessdata") | Out-Null
Copy-Item "$root\tools\tessdata\*.traineddata" (Join-Path $publishDir "tools/tessdata") -Force
# Модели PaddleOCR и каталог языковых пакетов: движок ищет их как tools\ocrmodels
# рядом с приложением, каталог — как tools\ocrmodels.lock.json.
New-Item -ItemType Directory -Force (Join-Path $publishDir "tools/ocrmodels") | Out-Null
Copy-Item "$root\tools\ocrmodels\*" (Join-Path $publishDir "tools/ocrmodels") -Force
Copy-Item "$root\tools\ocrmodels.lock.json" (Join-Path $publishDir "tools") -Force
Copy-Item "$root\docs\THIRD_PARTY_NOTICES.md" $publishDir -Force
Copy-Item "$root\installer\Assets\license.en.txt" (Join-Path $publishDir "LICENSE.txt") -Force
Copy-Item "$root\installer\Assets\license.ru.txt" (Join-Path $publishDir "LICENSE.ru.txt") -Force

$hashTargets = @()

if ($Msi -or $All) {
    Write-Host "== MSI (WiX 5, x64, en-US UI, version $version) =="
    if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
        dotnet tool install --global wix --version 5.0.2
    }
    wix extension add --global WixToolset.UI.wixext/5.0.2 2>$null
    $msiPath = Join-Path $root "artifacts/NexusPdf.msi"
    # -d Version: the MSI product version comes from Directory.Build.props, never
    # from the .wxs. A hard-coded number there once went four releases stale, and
    # because MajorUpgrade compares product versions, every install stayed a
    # separate product and piled up its own entry in "Installed apps".
    wix build installer/Msi/NexusPdf.wxs -arch x64 -culture en-US `
        -ext WixToolset.UI.wixext `
        -d "Version=$version" `
        -bindpath "publish=$publishDir" `
        -bindpath "assets=$root\installer\Assets" `
        -o $msiPath
    if ($LASTEXITCODE -ne 0) { exit 1 }
    "MSI: " + [math]::Round((Get-Item $msiPath).Length / 1MB, 1) + " MB"
    $hashTargets += $msiPath
}

if ($All) {
    Write-Host "== Branded Setup.exe =="
    $setupTmp = Join-Path $root "artifacts/setup-tmp"
    dotnet publish src/NexusPdf.Setup -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:MsiPath="$root\artifacts\NexusPdf.msi" -o $setupTmp
    if ($LASTEXITCODE -ne 0) { exit 1 }
    $setupExe = Join-Path $root "artifacts/NexusPdfSetup.exe"
    Copy-Item (Join-Path $setupTmp "NexusPdfSetup.exe") $setupExe -Force
    "Setup: " + [math]::Round((Get-Item $setupExe).Length / 1MB, 1) + " MB"
    $hashTargets += $setupExe

    Write-Host "== Portable ZIP =="
    $zipPath = Join-Path $root "artifacts/NexusPdf-$version-portable-win-x64.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath
    "ZIP: " + [math]::Round((Get-Item $zipPath).Length / 1MB, 1) + " MB"
    $hashTargets += $zipPath
}

if ($All) {
    # Релизная сборка - терминальная: гасим персистентные серверы сборки
    # .NET SDK (MSBuild/Roslyn), чтобы в системе не висели ".NET Host".
    dotnet build-server shutdown | Out-Null
}

Write-Host "== SHA-256 =="
$hashTargets += (Join-Path $publishDir "NexusPdf.exe")
$lines = foreach ($f in $hashTargets) {
    (Get-FileHash $f -Algorithm SHA256).Hash + "  " + (Split-Path $f -Leaf)
}
$lines | Out-File (Join-Path $root "artifacts/checksums.sha256.txt") -Encoding ascii
$lines

Write-Host "Done."
