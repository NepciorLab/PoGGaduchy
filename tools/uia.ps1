# Pomocnik testów interfejsu: UI Automation okien własnego programu + zrzuty ekranu + kliknięcia myszą.
# Użycie:  . .\tools\uia.ps1   (dołączenie funkcji do bieżącej sesji)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class PggNative {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
}
"@
[void][PggNative]::SetProcessDPIAware()

function Get-TopWindows([int]$ProcessId) {
    $cond = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ProcessIdProperty), $ProcessId
    $col = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $cond)
    foreach ($w in $col) { $w }
}

function Find-ByName($Root, [string]$Name, [int]$TimeoutSec = 10) {
    $cond = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::NameProperty), $Name
    $end = (Get-Date).AddSeconds($TimeoutSec)
    do {
        $el = $Root.FindFirst([Windows.Automation.TreeScope]::Descendants, $cond)
        if ($el) { return $el }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $end)
    return $null
}

# Szuka w oknach procesu (okna mogą się pojawiać i znikać).
function Find-InProcess([int]$ProcessId, [string]$Name, [int]$TimeoutSec = 10) {
    $end = (Get-Date).AddSeconds($TimeoutSec)
    do {
        foreach ($w in Get-TopWindows $ProcessId) {
            $cond = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::NameProperty), $Name
            $el = $w.FindFirst([Windows.Automation.TreeScope]::Descendants, $cond)
            if ($el) { return $el }
        }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $end)
    return $null
}

function Invoke-Element($El) {
    $p = $null
    if ($El.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke(); return "invoke" }
    if ($El.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { $p.Select(); return "select" }
    $r = $El.Current.BoundingRectangle
    Click-At ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2))
    return "mouse"
}

function Click-At([int]$X, [int]$Y) {
    [void][PggNative]::SetCursorPos($X, $Y)
    Start-Sleep -Milliseconds 80
    [PggNative]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [PggNative]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
}

function Save-Screen([string]$Path) {
    $b = [Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object Drawing.Bitmap $b.Width, $b.Height
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
    $bmp.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

function Wait-LogLine([string]$LogPath, [string]$Pattern, [int]$TimeoutSec = 30, [int]$FromLine = 0) {
    $end = (Get-Date).AddSeconds($TimeoutSec)
    do {
        if (Test-Path $LogPath) {
            $lines = @(Get-Content $LogPath -Encoding UTF8 | Select-Object -Skip $FromLine)
            $m = $lines | Where-Object { $_ -match $Pattern } | Select-Object -First 1
            if ($m) { return $m }
        }
        Start-Sleep -Milliseconds 400
    } while ((Get-Date) -lt $end)
    return $null
}

function Get-LogLength([string]$LogPath) { if (Test-Path $LogPath) { @(Get-Content $LogPath -Encoding UTF8).Count } else { 0 } }
