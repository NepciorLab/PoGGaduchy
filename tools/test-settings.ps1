# Test ustawień przez prawdziwy interfejs (dwie kopie: "ala" = administrator z plikiem Admin.PMa, "bartek" = zwykła osoba):
# grupy ustawień, własny obraz widoczny u drugiej osoby, obraz innej osoby ustawiany przez administratora, dodanie i usunięcie osoby.
# Wymaga lokalnego serwera (server: npm run dev) z .dev.vars z make-dev-vars.mjs i kodów z make-install-codes.mjs.
param([string]$Exe = (Join-Path $PSScriptRoot '..\Source\bin\Debug\net8.0-windows10.0.19041.0\PoGGaduchy.exe'))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'uia.ps1')
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$shots = Join-Path $root 'shots'; New-Item -ItemType Directory -Force $shots | Out-Null
$results = [System.Collections.ArrayList]@()
function Check([bool]$ok, [string]$what) { [void]$results.Add($ok); Write-Host (($(if ($ok) { 'ok:   ' } else { 'FAIL: ' })) + $what) }
function Mouse-Hold([int]$x, [int]$y, [int]$ms) {
    [void][PggNative]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 150
    [PggNative]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds $ms
    [PggNative]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
}
function Find-ById($Root, [string]$Id, [int]$TimeoutSec = 8) {
    $cond = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::AutomationIdProperty), $Id
    $end = (Get-Date).AddSeconds($TimeoutSec)
    do { $el = $Root.FindFirst([Windows.Automation.TreeScope]::Descendants, $cond); if ($el) { return $el }; Start-Sleep -Milliseconds 250 } while ((Get-Date) -lt $end)
    return $null
}
function Text-Contains([int]$ProcessId, [string]$Part, [int]$TimeoutSec = 10) {
    $end = (Get-Date).AddSeconds($TimeoutSec)
    do {
        foreach ($w in Get-TopWindows $ProcessId) {
            $all = $w.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
            foreach ($e in $all) { if ($e.Current.Name -like "*$Part*") { return $e.Current.Name } }
        }
        Start-Sleep -Milliseconds 400
    } while ((Get-Date) -lt $end)
    return $null
}
function Find-InSettings([int]$ProcessId, [string]$Name, [int]$TimeoutSec = 6) {
    $end = (Get-Date).AddSeconds($TimeoutSec)
    do {
        # okno ustawień jest "należące" do głównego (modalne): UIA pokazuje je jako potomka okna głównego albo jako osobne okno
        $w = $null
        foreach ($t in Get-TopWindows $ProcessId) {
            if ($t.Current.Name -eq 'Ustawienia') { $w = $t; break }
            $c = New-Object Windows.Automation.AndCondition (
                (New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::NameProperty), 'Ustawienia'),
                (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Window)))
            $w = $t.FindFirst([Windows.Automation.TreeScope]::Descendants, $c)
            if ($w) { break }
        }
        if ($w) { $el = Find-ByName $w $Name 1; if ($el) { return $el } }
    } while ((Get-Date) -lt $end)
    $names = Get-TopWindows $ProcessId | ForEach-Object { '[' + $_.Current.Name + ']' }
    Write-Host "      (nie znaleziono „$Name”; okna procesu: $($names -join ' '))"
    return $null
}
# Otwiera ustawienia przytrzymaniem przycisku. Przed kliknięciem wysuwa okno TEJ kopii i sprawdza, czy pod kursorem jest właśnie ona.
function Open-Settings([int]$ProcessId) {
    for ($try = 0; $try -lt 4; $try++) {
        Get-Process PoGGaduchy -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $ProcessId } | ForEach-Object { Set-WindowState $_.Id Minimized }
        Bring-ToFront $ProcessId
        $btn = Find-InProcess $ProcessId 'Ustawienia (przytrzymaj)' 20
        $r = $btn.Current.BoundingRectangle
        $cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
        $at = [Windows.Automation.AutomationElement]::FromPoint((New-Object Windows.Point $cx, $cy))
        if ($at -and $at.Current.ProcessId -eq $ProcessId) {
            Mouse-Hold $cx $cy 1400
            $done = Find-InProcess $ProcessId 'Gotowe' 6
            if ($done) { return $done }
        }
        else { Write-Host "      (próba $($try + 1): pod kursorem jest inne okno, ponawiam)" }
    }
    return $null
}
function Start-Instance([string]$id) {
    $dir = Join-Path $root "_probe\$id"
    foreach ($f in 'settings.json', 'log.txt', 'log.txt.old') { Remove-Item (Join-Path $dir $f) -ErrorAction SilentlyContinue }
    Remove-Item (Join-Path $dir 'images'), (Join-Path $dir 'Nowe osoby') -Recurse -Force -ErrorAction SilentlyContinue
    $env:PGG_DATA = $dir; $env:PGG_FAKE_MEDIA = '1'; $env:PGG_NO_UPDATE = '1'; $env:PGG_NO_CAMERA = ''; $env:PGG_FORCE_RELAY = ''
    $env:PGG_TEST_PHOTO = Join-Path $root '_probe\test-photo.png'
    [pscustomobject]@{ Id = $id; Dir = $dir; Proc = (Start-Process $Exe -PassThru) }
}

# zdjęcie testowe: kolorowy gradient z napisem
Add-Type -AssemblyName System.Drawing
$bmp = New-Object Drawing.Bitmap 800, 600
$g = [Drawing.Graphics]::FromImage($bmp)
$g.FillRectangle((New-Object Drawing.Drawing2D.LinearGradientBrush((New-Object Drawing.Point 0, 0), (New-Object Drawing.Point 800, 600), [Drawing.Color]::OrangeRed, [Drawing.Color]::Gold)), 0, 0, 800, 600)
$g.DrawString('TEST', (New-Object Drawing.Font('Segoe UI', [single]140, [Drawing.FontStyle]::Bold)), [Drawing.Brushes]::White, 120, 170)
$g.Dispose(); $bmp.Save((Join-Path $root '_probe\test-photo.png'), [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()

$a = Start-Instance 'ala'; $b = Start-Instance 'bartek'
try {
    # ---- zwykła osoba: bez grupy administratora
    Set-WindowState $a.Proc.Id Minimized; Start-Sleep -Milliseconds 800
    $done = Open-Settings $b.Proc.Id
    Check ($null -ne $done) 'bartek: przytrzymanie otwiera ustawienia'
    foreach ($n in 'Mój obraz', 'Audio i wideo', 'Połączenia', 'Wygląd', 'Program') { Check ($null -ne (Find-InProcess $b.Proc.Id $n 3)) "bartek widzi grupę [$n]" }
    Start-Sleep -Seconds 3
    Check ($null -eq (Find-InProcess $b.Proc.Id 'Osoby (administrator)' 2)) 'bartek (zwykła osoba) NIE widzi grupy „Osoby (administrator)”'
    Save-Screen (Join-Path $shots 'settings-bartek-image.png')
    [void](Invoke-Element (Find-InProcess $b.Proc.Id 'Połączenia' 3)); Start-Sleep -Milliseconds 600
    Check ($null -ne (Find-InProcess $b.Proc.Id 'Ala' 3)) 'bartek: w „Połączenia” jest lista osób do automatycznego odbierania (Ala)'
    [void](Invoke-Element (Find-InProcess $b.Proc.Id 'Gotowe' 3))

    # ---- administrator
    Set-WindowState $b.Proc.Id Minimized; Set-WindowState $a.Proc.Id Normal; Start-Sleep -Seconds 1
    $done = Open-Settings $a.Proc.Id
    Check ($null -ne $done) 'ala: ustawienia otwarte'
    $people = Find-InProcess $a.Proc.Id 'Osoby (administrator)' 15
    Check ($null -ne $people) 'ala (administrator z plikiem Admin.PMa) widzi grupę „Osoby (administrator)”'

    # własny obraz
    [void](Invoke-Element (Find-InSettings $a.Proc.Id 'Mój obraz' 3)); Start-Sleep -Milliseconds 500
    [void](Invoke-Element (Find-InSettings $a.Proc.Id 'Wybierz zdjęcie…' 3)); Start-Sleep -Milliseconds 800
    [void](Invoke-Element (Find-InSettings $a.Proc.Id 'Zapisz i pokaż wszystkim' 3))
    Check ($null -ne (Text-Contains $a.Proc.Id 'Zapisane. Twój obraz zobaczą' 10)) 'ala zapisuje swój obraz (komunikat o zapisie)'
    Save-Screen (Join-Path $shots 'settings-ala-image.png')
    $f = Wait-LogLine (Join-Path $b.Dir 'log.txt') 'Obraz osoby ala pobrany' 15
    Check ($null -ne $f -and (Test-Path (Join-Path $b.Dir 'images\avatar-ala.jpg'))) 'bartek automatycznie pobiera obraz ali (plik avatar-ala.jpg)'

    # administrator ustawia obraz innej osoby
    [void](Invoke-Element $people); Start-Sleep -Milliseconds 700
    [void](Invoke-Element (Find-InSettings $a.Proc.Id 'Bartek' 5)); Start-Sleep -Milliseconds 600
    [void](Invoke-Element (Find-InSettings $a.Proc.Id 'Wybierz zdjęcie…' 3)); Start-Sleep -Milliseconds 800
    [void](Invoke-Element (Find-InSettings $a.Proc.Id 'Zapisz obraz tej osoby' 3))
    Check ($null -ne (Text-Contains $a.Proc.Id 'zapisany i widoczny u wszystkich' 10)) 'administrator zapisuje obraz osoby „Bartek”'
    $f = Wait-LogLine (Join-Path $b.Dir 'log.txt') 'Własny obraz|Obraz osoby' 3
    Start-Sleep -Seconds 3
    Check (Test-Path (Join-Path $b.Dir 'images\avatar-bartek.jpg')) 'bartek dostaje własny obraz ustawiony przez administratora'
    Save-Screen (Join-Path $shots 'settings-ala-people.png')

    # dodanie osoby
    [void](Invoke-Element (Find-InSettings $a.Proc.Id '+ Dodaj osobę…' 3)); Start-Sleep -Milliseconds 700
    $dlg = Get-TopWindows $a.Proc.Id | Where-Object { $_.Current.Name -eq 'PoGGaduchy' } | Select-Object -First 1
    Check ($null -ne $dlg) 'okno „Dodaj osobę” się pokazuje'
    if ($dlg) {
        $box = Find-ById $dlg 'Box' 3
        $vp = $box.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern); $vp.SetValue('Ciocia Łucja')
        [void](Invoke-Element (Find-ByName $dlg 'Dodaj' 3))
    }
    $ok = Find-InProcess $a.Proc.Id 'OK' 10
    Check ($null -ne $ok) 'po dodaniu osoby pojawia się komunikat z informacją o pliku do przekazania'
    if ($ok) { [void](Invoke-Element $ok) }
    Start-Sleep -Milliseconds 800
    Check ((Test-Path (Join-Path $a.Dir 'Nowe osoby\ciocia-lucja\kod-instalacji.txt')) -and (Test-Path (Join-Path $a.Dir 'Nowe osoby\ciocia-lucja.zip'))) 'powstał folder i zip z kodem instalacji nowej osoby'
    Check ($null -ne (Find-InProcess $b.Proc.Id 'Ciocia Łucja' 10)) 'bartek od razu widzi nową osobę na liście kafelków'

    # usunięcie osoby
    [void](Invoke-Element (Find-InSettings $a.Proc.Id 'Ciocia Łucja' 5)); Start-Sleep -Milliseconds 500
    [void](Invoke-Element (Find-InSettings $a.Proc.Id 'Usuń osobę' 3))
    $yes = Find-InProcess $a.Proc.Id 'Tak' 6
    Check ($null -ne $yes) 'usuwanie osoby pyta o potwierdzenie'
    if ($yes) { [void](Invoke-Element $yes) }
    Start-Sleep -Seconds 3
    Check ($null -eq (Find-InProcess $b.Proc.Id 'Ciocia Łucja' 3)) 'po usunięciu osoba znika z kafelków u bartka'

    # audio i wideo
    [void](Invoke-Element (Find-InSettings $a.Proc.Id 'Audio i wideo' 3)); Start-Sleep -Seconds 6
    Save-Screen (Join-Path $shots 'settings-ala-av.png')
    Check ($null -ne (Find-InSettings $a.Proc.Id 'Mikrofon' 3)) 'grupa „Audio i wideo” pokazuje wybór mikrofonu'
}
finally {
    foreach ($i in $a, $b) { try { Stop-Process -Id $i.Proc.Id -Force } catch { } }
}
$bad = @($results | Where-Object { -not $_ }).Count
Write-Host ($(if ($bad) { "NIEPOWODZENIA: $bad" } else { 'Wszystko w porządku.' }))
