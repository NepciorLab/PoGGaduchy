# Pomocnik testów interfejsu: UI Automation okien własnego programu + zrzuty ekranu + kliknięcia myszą.
# Użycie:  . .\tools\uia.ps1   (dołączenie funkcji do bieżącej sesji)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class PggNative {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
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

# Zmienia stan okien procesu: Minimized / Normal / Maximized (przydaje się, gdy dwie kopie programu zasłaniają się nawzajem).
function Set-WindowState([int]$ProcessId, [string]$State) {
    # przez user32 (UIA WindowPattern nie minimalizuje okien bez paska tytułu, np. okna rozmowy)
    $cmd = switch ($State) { 'Minimized' { 6 } 'Maximized' { 3 } default { 9 } }
    foreach ($w in Get-TopWindows $ProcessId) {
        $h = [IntPtr]$w.Current.NativeWindowHandle
        if ($State -ne 'Normal' -or [PggNative]::IsIconic($h)) { [void][PggNative]::ShowWindow($h, $cmd) }
    }
}

# Wysuwa główne okno procesu na wierzch (obchodzi blokadę przejmowania fokusu: krótkie wciśnięcie Alt + dołączenie do wątku okna z fokusem).
function Bring-ToFront([int]$ProcessId) {
    $all = @(Get-TopWindows $ProcessId)
    $w = ($all | Where-Object { $_.Current.Name -like '*rozmowa*' -or $_.Current.Name -like '*dzwoni*' } | Select-Object -First 1)
    if (-not $w) { $w = $all | Select-Object -First 1 }
    if (-not $w) { return }
    $h = [IntPtr]$w.Current.NativeWindowHandle
    if ([PggNative]::IsIconic($h)) { [void][PggNative]::ShowWindow($h, 9) }   # przywraca tylko zminimalizowane (zmaksymalizowane zostaje)
    $fg = [PggNative]::GetForegroundWindow(); $fgPid = 0
    $fgThread = [PggNative]::GetWindowThreadProcessId($fg, [ref]$fgPid)
    $me = [PggNative]::GetCurrentThreadId()
    [void][PggNative]::AttachThreadInput($me, $fgThread, $true)
    [void][PggNative]::SetForegroundWindow($h)
    # kolejność okien (z-order) niezależnie od fokusu: na chwilę "zawsze na wierzchu", potem normalnie
    [void][PggNative]::SetWindowPos($h, [IntPtr](-1), 0, 0, 0, 0, 0x0003 -bor 0x0040)
    [void][PggNative]::SetWindowPos($h, [IntPtr](-2), 0, 0, 0, 0, 0x0003 -bor 0x0040)
    [void][PggNative]::AttachThreadInput($me, $fgThread, $false)
    Start-Sleep -Milliseconds 400
}

# Okno najwyższego poziomu (uchwyt i tytuł) leżące w danym punkcie ekranu.
function Get-RootAt([int]$X, [int]$Y) {
    $p = New-Object PggNative+POINT; $p.X = $X; $p.Y = $Y
    $h = [PggNative]::GetAncestor([PggNative]::WindowFromPoint($p), 2)
    $sb = New-Object Text.StringBuilder 256; [void][PggNative]::GetWindowText($h, $sb, 256)
    [pscustomobject]@{ Handle = $h; Title = $sb.ToString() }
}
