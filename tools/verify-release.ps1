param([string]$Version = '0.27.0')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$evidence = Join-Path $root 'artifacts/release-verification'
New-Item -ItemType Directory -Force $evidence | Out-Null
$setup = Join-Path $root 'artifacts/NexusPdfSetup.exe'
$installed = Join-Path $env:RUNNER_TEMP 'nexuspdf-installed-audit'
$payloadBefore = @(Get-ChildItem $env:TEMP -Directory -Filter 'NexusPdfSetup-*' | ForEach-Object FullName)

function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    Write-Host "PASS $Message"
}

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-installed-ui.ps1 -Exe $setup -Output $evidence -Setup
Assert ($LASTEXITCODE -eq 0) 'The delivered EXE opens its real setup window'
for ($attempt = 0; $attempt -lt 2; $attempt++) {
    $process = Start-Process $setup -ArgumentList @('/S', '/nodesktop', "/dir=$installed") -PassThru -Wait
    Assert ($process.ExitCode -in 0, 3010) "Installation/reinstallation $attempt succeeds"
}
$exe = Join-Path $installed 'NexusPdf.exe'
$cli = Join-Path $installed 'NexusPdfCli.exe'
Assert ((Get-Item $exe).VersionInfo.FileVersion -eq "$Version.0") 'Installed executable has the release version'
Assert (Test-Path (Join-Path $installed 'NexusPdfThumbnail.dll')) 'Installed Explorer thumbnail extension is present'
Assert (@(Get-ChildItem $env:TEMP -Directory -Filter 'NexusPdfSetup-*' | Where-Object FullName -notin $payloadBefore).Count -eq 0) 'The EXE leaves no extracted MSI payload'

$profileRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'NexusPDF'
New-Item -ItemType Directory -Force $profileRoot | Out-Null
@{ Language = 'ru'; Theme = 'light'; SingleInstance = $true; UiDensity = 'compact' } | ConvertTo-Json | Set-Content (Join-Path $profileRoot 'settings.json')
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-installed-ui.ps1 -Exe $exe -Output $evidence -Document artifacts/ui-audit/fixture.pdf
Assert ($LASTEXITCODE -eq 0) 'Installed reader, organizer, optimization and print center work'
Assert (-not (Test-Path (Join-Path $profileRoot 'session.lock'))) 'Normal closing clears the crash sentinel'
$logText = (Get-ChildItem (Join-Path $profileRoot 'Logs') -File | Get-Content) -join "`n"
Assert ($logText -notmatch '\[ERR\]|\[FTL\]|Binding error|Ошибка привязки|необработан') 'Installed UI produces no error or binding-error log'

$fixtures = Join-Path $root 'artifacts/ui-audit'
$output = Join-Path $evidence 'cli'
New-Item -ItemType Directory -Force $output | Out-Null
function Invoke-Cli([string[]]$Arguments, [int[]]$AllowedExit = @(0)) {
    & $cli @Arguments
    Assert ($LASTEXITCODE -in $AllowedExit) ('Installed CLI: ' + $Arguments[0])
}
Invoke-Cli @('--help')
Invoke-Cli @('from-images', "$output/imported.pdf", "$fixtures/compressed-photo.jpg")
$jpegBytes = (Get-Item "$fixtures/compressed-photo.jpg").Length
$pdfBytes = (Get-Item "$output/imported.pdf").Length
Assert ($pdfBytes -le $jpegBytes + 4096) "CLI JPEG import preserves compression: $jpegBytes -> $pdfBytes bytes"
Invoke-Cli @('export-images', "$fixtures/fixture.pdf", "$output/images", '--pages', '3', '--format', 'jpeg', '--quality', '80')
Assert (@(Get-ChildItem "$output/images" -File).Count -eq 1 -and (Test-Path "$output/images/fixture-003.jpg")) 'Image export saves only page 3'
$thirdPageHash = (Get-FileHash "$output/images/fixture-003.jpg").Hash
Invoke-Cli @('export-images', "$fixtures/fixture.pdf", "$output/images", '--pages', '1', '--format', 'jpeg', '--quality', '80')
Invoke-Cli @('export-images', "$fixtures/fixture.pdf", "$output/images", '--pages', '1', '--format', 'jpeg', '--quality', '90', '--force')
Assert ((Get-FileHash "$output/images/fixture-003.jpg").Hash -eq $thirdPageHash) 'A selected-page export preserves other exported pages'
Invoke-Cli @('export-images', "$fixtures/fixture.pdf", "$output/rejected", '--dpi', 'NaN') @(1)
Invoke-Cli @('export-images', "$fixtures/fixture.pdf", "$output/rejected", '--format', 'jpeg', '--quality', 'Infinity') @(1)
Invoke-Cli @('extract-text', "$fixtures/fixture.pdf", "$output/text.txt")
Assert ((Get-Content "$output/text.txt" -Raw) -match 'NexusPDF') 'Installed text extraction retains readable text'
Invoke-Cli @('word', "$fixtures/fixture.pdf", "$output/selected.docx", '--pages', '2')
Invoke-Cli @('excel', "$fixtures/fixture.pdf", "$output/tables.xlsx")
Invoke-Cli @('merge', "$output/merged.pdf", "$fixtures/fixture.pdf", "$output/imported.pdf")
Invoke-Cli @('optimize', "$fixtures/fixture.pdf", "$output/optimized.pdf")
Invoke-Cli @('protect', "$fixtures/fixture.pdf", "$output/protected.pdf", '--password', 'audit-fixture')
Invoke-Cli @('extract-text', "$output/protected.pdf", "$output/protected.txt", '--password', 'audit-fixture')
Invoke-Cli @('ocr', "$fixtures/ocr-scan.pdf", "$output/recognized.pdf", '--engine', 'tesseract')
Invoke-Cli @('extract-text', "$output/recognized.pdf", "$output/recognized.txt")
Assert ((Get-Content "$output/recognized.txt" -Raw) -match 'NEXUS') 'Installed OCR adds a searchable text layer'
Invoke-Cli @('compress', "$fixtures/fixture.pdf", "$output/compressed.pdf", '--preset', 'balanced')
Invoke-Cli @('compress', "$output/compressed.pdf", "$output/compressed-twice.pdf", '--preset', 'balanced')
Invoke-Cli @('extract-text', "$output/compressed-twice.pdf", "$output/compressed-twice.txt")
Assert (([regex]::Matches((Get-Content "$output/compressed-twice.txt" -Raw), 'NexusPDF')).Count -eq 3) 'Repeated installed compression keeps all three text pages'
Invoke-Cli @('compare', "$fixtures/fixture.pdf", "$fixtures/fixture.pdf")
Invoke-Cli @('compare', "$fixtures/fixture.pdf", "$output/imported.pdf") @(3)
foreach ($pdf in Get-ChildItem $output -File -Filter '*.pdf') {
    $arguments = @('--check', $pdf.FullName)
    if ($pdf.Name -eq 'protected.pdf') { $arguments = @('--password=audit-fixture') + $arguments }
    & "$installed/tools/qpdf/qpdf.exe" @arguments
    Assert ($LASTEXITCODE -eq 0) ("qpdf validates delivered output: " + $pdf.Name)
}
Assert (@(Get-ChildItem $output -Recurse -File | Where-Object Name -like '*.nexustmp-*').Count -eq 0) 'Installed commands leave no incomplete output files'

$products = @(Get-ChildItem HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall | Get-ItemProperty | Where-Object DisplayName -eq 'NexusPDF')
Assert ($products.Count -eq 1 -and $products[0].DisplayVersion -eq $Version) 'Reinstallation leaves one correctly versioned application entry'
$remove = Start-Process msiexec.exe -ArgumentList @('/x', $products[0].PSChildName, '/qn', '/norestart', '/l*v', "$evidence/uninstall.log") -PassThru -Wait
Assert ($remove.ExitCode -in 0, 3010) 'The delivered application uninstalls normally'
Assert (-not (Test-Path $exe)) 'Uninstall removes the application executable'
Assert (Test-Path "$output/compressed-twice.pdf") 'Uninstall preserves user export files'
Write-Host 'Release installer, real installed WPF UI and every CLI command verified.'
