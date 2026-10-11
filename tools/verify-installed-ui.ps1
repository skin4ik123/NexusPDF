param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Output,
    [string]$Document = '',
    [switch]$Setup
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Runtime.InteropServices;
public static class NexusPdfFixtureScreen {
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    public static void Click(int x, int y) {
        if (!SetCursorPos(x, y)) throw new InvalidOperationException("Cannot position the desktop pointer.");
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }
    public static int CountBlueSamples(int left, int top, int width, int height) {
        using (var bitmap = new Bitmap(width, height)) {
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(left, top, 0, 0, bitmap.Size);
            int count = 0;
            for (int y = 0; y < height; y += 4)
                for (int x = 0; x < width; x += 4) {
                    var color = bitmap.GetPixel(x, y);
                    if (Math.Abs(color.R - 55) <= 2 && Math.Abs(color.G - 185) <= 2 && Math.Abs(color.B - 215) <= 2)
                        count++;
                }
            return count;
        }
    }
}
'@
New-Item -ItemType Directory -Force $Output | Out-Null
$renderChecks = New-Object System.Collections.Generic.List[object]

function Find-Button([string]$Pattern) {
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        $matches = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
        $button = @($matches | Where-Object {
            $_.Current.IsEnabled -and -not $_.Current.IsOffscreen -and $_.Current.Name -match $Pattern
        }) | Select-Object -First 1
        if ($button) { return $button }
        Start-Sleep -Milliseconds 100
    }
    throw "Installed action missing or unavailable: $Pattern"
}

function Invoke-Button([string]$Pattern) {
    $button = Find-Button $Pattern
    $action = $null
    if ($button.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$action)) {
        ([System.Windows.Automation.InvokePattern]$action).Invoke()
    } elseif ($button.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$action)) {
        # WPF TogglePattern changes IsChecked but does not execute the button's
        # command. These panel buttons use a one-way binding plus a command.
        $point = $button.GetClickablePoint()
        [NexusPdfFixtureScreen]::Click([int][Math]::Round($point.X), [int][Math]::Round($point.Y))
        Start-Sleep -Milliseconds 200
    } else {
        throw "Installed button has no invocation or toggle pattern: $Pattern"
    }
}

function Wait-RenderedFixture([string]$Stage) {
    $idCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'PagesList')
    $itemCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $samples = 0
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        $reader = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCondition)
        if ($reader) {
            $items = $reader.FindAll([System.Windows.Automation.TreeScope]::Descendants, $itemCondition)
            $item = @($items | Where-Object { -not $_.Current.IsOffscreen }) | Select-Object -First 1
            if ($item) {
                $area = $item.Current.BoundingRectangle
                $viewport = $reader.Current.BoundingRectangle
                $left = [int][Math]::Ceiling([Math]::Max($screen.Left, [Math]::Max($area.Left, $viewport.Left)))
                $top = [int][Math]::Ceiling([Math]::Max($screen.Top, [Math]::Max($area.Top, $viewport.Top)))
                $right = [int][Math]::Floor([Math]::Min($screen.Right, [Math]::Min($area.Right, $viewport.Right)))
                $bottom = [int][Math]::Floor([Math]::Min($screen.Bottom, [Math]::Min($area.Bottom, $viewport.Bottom)))
                if ($right -gt $left -and $bottom -gt $top) {
                    $samples = [NexusPdfFixtureScreen]::CountBlueSamples($left, $top, $right - $left, $bottom - $top)
                    if ($samples -ge 50) { break }
                }
            }
        }
        Start-Sleep -Milliseconds 100
    }
    $renderChecks.Add(@{ Stage = $Stage; BlueSamples = $samples; Passed = ($samples -ge 50) })
    $renderChecks | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $Output 'installed-render-checks.json')
    if ($samples -lt 50) { throw "Installed page did not render: $Stage (blue samples: $samples)" }
    Write-Host "PASS Installed PDF paints its actual contents: $Stage ($samples samples)"
}

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
    $processCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $windowCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
    $condition = New-Object System.Windows.Automation.AndCondition($processCondition, $windowCondition)
    for ($attempt = 0; $attempt -lt 200; $attempt++) {
        $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants, $condition)
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
    $names | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $Output $(if ($Setup) { 'setup-actions.json' } else { 'installed-actions.json' }))
    Capture-Desktop $(if ($Setup) { 'setup-window' } else { 'installed-reader' })
    if (-not $Setup) {
        if ($names.Count -lt 8 -or $process.MainWindowTitle -notmatch 'fixture.*NexusPDF') {
            throw "Installed reader did not load its PDF: $($process.MainWindowTitle)"
        }
        Wait-RenderedFixture 'initial reader'
        Invoke-Button '^Свойства объекта$'
        Invoke-Button '^Инструменты$'
        $searchCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ToolsSearch')
        $search = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $searchCondition)
        if (-not $search) { throw 'Installed tool search did not appear' }
        ([System.Windows.Automation.ValuePattern]$search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue('Повернуть вправо')
        for ($round = 1; $round -le 2; $round++) {
            Invoke-Button '^Повернуть вправо$'
            Start-Sleep -Milliseconds 300
            Wait-RenderedFixture "rotation $round without reopening the reader"
            Capture-Desktop "installed-after-rotation-$round"
            Invoke-Button '^Отменить$'
            Start-Sleep -Milliseconds 300
            Wait-RenderedFixture "undo rotation $round without reopening the reader"
        }
        Invoke-Button '^Инструменты$'
        Invoke-Button '^Свойства объекта$'
        Start-Sleep -Milliseconds 300
        Wait-RenderedFixture 'reader after repeated changes'
        Capture-Desktop 'installed-after-render-regressions'
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
} catch {
    Capture-Desktop 'installed-ui-failure'
    $windowCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
    [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, $windowCondition) |
        ForEach-Object { @{ Name = $_.Current.Name; ProcessId = $_.Current.ProcessId; Offscreen = $_.Current.IsOffscreen } } |
        ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $Output 'failure-windows.json')
    throw
} finally {
    if (-not $Setup) {
        $logFolder = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'NexusPDF/Logs'
        if (Test-Path $logFolder) { Copy-Item $logFolder (Join-Path $Output 'installed-logs') -Recurse -Force -ErrorAction SilentlyContinue }
    }
    $process.Refresh()
    if (-not $process.HasExited) { $process.Kill() }
}
