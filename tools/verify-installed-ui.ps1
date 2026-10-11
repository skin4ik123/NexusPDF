param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Output,
    [string]$Document = '',
    [switch]$Setup
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
New-Item -ItemType Directory -Force $Output | Out-Null

function Capture-Desktop([string]$Name) {
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bitmap = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
        $bitmap.Save((Join-Path $Output "$Name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}

function Find-Dialog([int]$ProcessId, [string]$Pattern) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children, $condition)
        foreach ($window in $windows) {
            if ($window.Current.Name -match $Pattern) { return $window }
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Installed dialog did not open: $Pattern"
}

$process = if ($Document) {
    Start-Process $Exe -ArgumentList ('"' + (Resolve-Path $Document).Path + '"') -PassThru
} else { Start-Process $Exe -PassThru }
try {
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
        if ($process.HasExited) { throw "Installed UI exited: $($process.ExitCode)" }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) { break }
    }
    if ($process.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Installed window did not appear' }
    Start-Sleep -Milliseconds 1800
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $buttonCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    $buttons = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
    $names = @($buttons | ForEach-Object { $_.Current.Name })
    $names | ConvertTo-Json | Set-Content (Join-Path $Output $(if ($Setup) { 'setup-actions.json' } else { 'installed-actions.json' }))
    Capture-Desktop $(if ($Setup) { 'setup-window' } else { 'installed-reader' })
    if (-not $Setup) {
        if ($names.Count -lt 8 -or $process.MainWindowTitle -notmatch 'fixture.*NexusPDF') {
            throw "Installed reader did not load its PDF: $($process.MainWindowTitle)"
        }
        foreach ($target in @(
            @{ Button = '^Режим систематизации страниц$'; Screenshot = 'installed-organizer'; Dialog = '' },
            @{ Button = 'Оптимизац'; Screenshot = 'installed-optimization'; Dialog = 'Оптимизац' },
            @{ Button = '^Печать$'; Screenshot = 'installed-print-center'; Dialog = '^Центр печати$' }
        )) {
            $button = @($buttons | Where-Object { $_.Current.IsEnabled -and $_.Current.Name -match $target.Button }) | Select-Object -First 1
            if (-not $button) { throw "Installed action missing: $($target.Button)" }
            $invoke = [System.Windows.Automation.InvokePattern]$button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $invoke.Invoke()
            if ($target.Dialog) {
                $dialog = Find-Dialog $process.Id $target.Dialog
                Start-Sleep -Milliseconds 1800
                Capture-Desktop $target.Screenshot
                $windowPattern = [System.Windows.Automation.WindowPattern]$dialog.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
                $windowPattern.Close()
            } else {
                Start-Sleep -Milliseconds 500
                Capture-Desktop $target.Screenshot
                $invoke.Invoke()
            }
            Start-Sleep -Milliseconds 300
        }
    }
    if (-not $process.CloseMainWindow()) { throw 'Installed window did not accept closing' }
    if (-not $process.WaitForExit(20000)) { throw 'Installed window did not close normally' }
    if ($process.ExitCode -ne 0) { throw "Installed UI returned $($process.ExitCode)" }
    Write-Host 'Actual installed windows opened and closed normally.'
} finally {
    $process.Refresh()
    if (-not $process.HasExited) { $process.Kill() }
}
