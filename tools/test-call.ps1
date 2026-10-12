# Test przez prawdziwy interfejs: dwie kopie programu na jednym komputerze (osobne foldery danych) łączą się przez serwer
# (lokalny: wrangler dev na http://127.0.0.1:8787 – albo wdrożony, gdy kody instalacji wskazują na niego),
# jedna dzwoni do drugiej, druga odbiera, potem rozmowa jest kończona czerwoną słuchawką.
#   powershell -ExecutionPolicy Bypass -Command "& .\tools\test-call.ps1 [-Scenario answer|reject|nocamera|cancel|autoanswer] [-ForceRelay]"
param(
    [ValidateSet('answer', 'reject', 'nocamera', 'cancel', 'autoanswer', 'settings')][string]$Scenario = 'answer',
    [switch]$ForceRelay,
    [switch]$CalleeNoCamera,
    [switch]$RealMedia,
    [string]$Exe = (Join-Path $PSScriptRoot '..\Source\bin\Debug\net8.0-windows10.0.19041.0\PoGGaduchy.exe'),
    [string]$Caller = 'ala', [string]$Callee = 'bartek',
    [string]$CallerName = 'Ala', [string]$CalleeName = 'Bartek'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'uia.ps1')
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$shots = Join-Path $root 'shots'; New-Item -ItemType Directory -Force $shots | Out-Null
$results = [System.Collections.ArrayList]@()
function Check([bool]$ok, [string]$what) { [void]$results.Add([pscustomobject]@{ Ok = $ok; What = $what }); Write-Host (($(if ($ok) { 'ok:   ' } else { 'FAIL: ' })) + $what) }

function Start-Instance([string]$id, [bool]$noCamera, [string]$settingsJson = $null) {
    $dir = Join-Path $root "_probe\$id"
    foreach ($f in 'settings.json', 'log.txt', 'log.txt.old') { Remove-Item (Join-Path $dir $f) -ErrorAction SilentlyContinue }
    if ($settingsJson) { [IO.File]::WriteAllText((Join-Path $dir 'settings.json'), $settingsJson, (New-Object Text.UTF8Encoding($false))) }
    $env:PGG_DATA = $dir
    $env:PGG_FAKE_MEDIA = $(if ($RealMedia) { '' } else { '1' })
    $env:PGG_NO_CAMERA = $(if ($noCamera) { '1' } else { '' })
    $env:PGG_FORCE_RELAY = $(if ($ForceRelay) { '1' } else { '' })
    $env:PGG_DEVTOOLS = ''
    $env:PGG_NO_UPDATE = '1'
    $env:PGG_SHOT_DIR = $shots
    $p = Start-Process $Exe -PassThru
    [pscustomobject]@{ Id = $id; Proc = $p; Log = (Join-Path $dir 'log.txt'); Dir = $dir }
}

$a = Start-Instance $Caller ($Scenario -eq 'nocamera')
$trusted = if ($Scenario -eq 'autoanswer') { '{"Contacts":[{"Id":"' + $Caller + '","ServerName":"' + $CallerName + '","AutoAnswer":true}]}' } else { $null }
$b = Start-Instance $Callee ([bool]$CalleeNoCamera) $trusted
try {
    # --- obie kopie łączą się z serwerem i widzą się nawzajem
    $okA = Find-InProcess $a.Proc.Id $CalleeName 25
    $okB = Find-InProcess $b.Proc.Id $CallerName 25
    Check ($null -ne $okA -and $null -ne $okB) 'obie kopie pokazują kafelek drugiej osoby (katalog z serwera)'
    Start-Sleep -Seconds 2
    Save-Screen (Join-Path $shots "e2e-$Scenario-1-list.png")

    # --- dzwonienie
    $lenA = Get-LogLength $a.Log; $lenB = Get-LogLength $b.Log
    $tile = Find-InProcess $a.Proc.Id $CalleeName 5
    [void](Invoke-Element $tile)
    Start-Sleep -Milliseconds 700
    $call = Find-InProcess $a.Proc.Id 'Zadzwoń wideo' 5
    Check ($null -ne $call) 'po wybraniu osoby jest zielony przycisk rozmowy'
    [void](Invoke-Element $call)
    Start-Sleep -Milliseconds 800
    if ($Scenario -eq 'nocamera') {
        Save-Screen (Join-Path $shots "e2e-$Scenario-2-nocamera.png")
        $voice = Find-InProcess $a.Proc.Id 'Zadzwoń bez kamery' 5
        Check ($null -ne $voice) 'brak kamery: jest przycisk „Zadzwoń bez kamery”'
        [void](Invoke-Element $voice)
    }

    $answer = $null
    if ($Scenario -eq 'autoanswer') {
        # osoba zaufana (AutoAnswer w ustawieniach odbierającego): okna „Odbierz” nie ma, rozmowa łączy się sama
        Start-Sleep -Seconds 2
        Check ($null -eq (Find-InProcess $b.Proc.Id 'Odbierz' 1)) 'osoba zaufana: okno „Odbierz” się nie pokazuje'
    }
    else {
        $answer = Find-InProcess $b.Proc.Id 'Odbierz' 20
        Check ($null -ne $answer) 'druga kopia pokazuje pełnoekranowe „Odbierz”'
        Start-Sleep -Milliseconds 800
        Save-Screen (Join-Path $shots "e2e-$Scenario-3-incoming.png")
    }

    if ($Scenario -eq 'reject') {
        $rej = Find-InProcess $b.Proc.Id 'Odrzuć' 5
        [void](Invoke-Element $rej)
        $m = Wait-LogLine $a.Log 'Rozmowa zakończona: .*nie może teraz rozmawiać' 15 $lenA
        Check ($null -ne $m) 'dzwoniący dowiaduje się, że rozmowa została odrzucona'
        return
    }
    if ($Scenario -eq 'cancel') {
        # dzwoniący rezygnuje, zanim druga strona odbierze
        $call2 = Find-InProcess $a.Proc.Id 'Rozłącz' 10
        Check ($null -ne $call2) 'u dzwoniącego jest przycisk rozłączenia (okno rozmowy)'
        [void](Invoke-Element $call2)
        $m = Wait-LogLine $b.Log 'Rozmowa zakończona: Nieodebrane połączenie' 15 $lenB
        Check ($null -ne $m) 'odbierający widzi nieodebrane połączenie'
        return
    }
    if ($answer) { [void](Invoke-Element $answer) }

    # --- połączenie
    $conA = Wait-LogLine $a.Log 'połączenie connected' 40 $lenA
    $conB = Wait-LogLine $b.Log 'połączenie connected' 40 $lenB
    Check (($null -ne $conA) -and ($null -ne $conB)) 'oba końce raportują połączenie WebRTC (connected)'
    $pathA = Wait-LogLine $a.Log 'wybrana ścieżka połączenia' 10 $lenA
    Write-Host ('      ' + $pathA)
    if ($ForceRelay) { Check ($pathA -match 'lokalna: relay') 'wymuszony przekaźnik: wybrano ścieżkę przez TURN (relay)' }
    Start-Sleep -Seconds 5
    Save-Screen (Join-Path $shots "e2e-$Scenario-4-call.png")

    # co naprawdę widać na ekranie (a nie tylko w stronie): każde okno rozmowy po kolei na wierzchu, drugie zminimalizowane
    foreach ($pair in @(@($a, $b), @($b, $a))) {
        $front = $pair[0]; $other = $pair[1]
        Set-WindowState $other.Proc.Id Minimized; Start-Sleep -Milliseconds 700; Bring-ToFront $front.Proc.Id; Start-Sleep -Milliseconds 1200
        $p = Join-Path $shots ("e2e-$Scenario-screen-" + $front.Id + ".png")
        Save-Screen $p
        $n = Get-DistinctColors $p
        Check ($n -gt 3) ("na ekranie widać okno rozmowy " + $front.Id + " (różnych kolorów: $n; jednolite tło = pusty ekran)")
    }
    Set-WindowState $b.Proc.Id Normal; Set-WindowState $a.Proc.Id Normal; Start-Sleep -Milliseconds 800

    if ($Scenario -eq 'settings') {
        # ustawienia dostępne w trakcie rozmowy: przytrzymanie przycisku na stronie rozmowy otwiera okno ustawień na wierzchu
        Set-WindowState $b.Proc.Id Minimized; Start-Sleep -Milliseconds 600; Bring-ToFront $a.Proc.Id
        $callWin = Get-TopWindows $a.Proc.Id | Where-Object { $_.Current.Name -like '*rozmowa*' } | Select-Object -First 1
        $gear = Find-ByName $callWin 'Ustawienia (przytrzymaj)' 10      # w oknie rozmowy (okno główne ma przycisk o tej samej nazwie)
        Check ($null -ne $gear) 'w oknie rozmowy jest przycisk „Ustawienia (przytrzymaj)”'
        if ($gear) {
            $gr = $gear.Current.BoundingRectangle; $gx = [int]($gr.X + $gr.Width / 2); $gy = [int]($gr.Y + $gr.Height / 2)
            $cr = $callWin.Current.BoundingRectangle
            Write-Host ("      przycisk ustawień: x={0} y={1} w={2} h={3}; okno rozmowy: x={4} y={5} w={6} h={7}" -f [int]$gr.X, [int]$gr.Y, [int]$gr.Width, [int]$gr.Height, [int]$cr.X, [int]$cr.Y, [int]$cr.Width, [int]$cr.Height)
            for ($k = 0; $k -lt 6; $k++) {
                $root = Get-RootAt $gx $gy
                if ($callWin -and $root.Handle -eq [IntPtr]$callWin.Current.NativeWindowHandle) { break }
                Write-Host ("      pod przyciskiem jest okno [{0}] zamiast okna rozmowy – wysuwam okno rozmowy ({1})" -f $root.Title, ($k + 1))
                Bring-ToFront $a.Proc.Id; Start-Sleep -Milliseconds 700
            }
            [void][PggNative]::SetCursorPos($gx, $gy); Start-Sleep -Milliseconds 200       # krótkie kliknięcie: tylko podpowiedź
            [PggNative]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 150; [PggNative]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 500
            Check ($null -eq (Find-InProcess $a.Proc.Id 'Gotowe' 1)) 'krótkie kliknięcie w rozmowie nie otwiera ustawień'
            $done = $null
            for ($try = 1; $try -le 3 -and -not $done; $try++) {
                [PggNative]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 1500; [PggNative]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
                $done = Find-InProcess $a.Proc.Id 'Gotowe' 5
                if (-not $done) { Write-Host "      (przytrzymanie, próba $try – ustawienia się nie otworzyły)" }
            }
            Check ($null -ne $done) 'przytrzymanie 1 s w rozmowie otwiera ustawienia (okno na wierzchu)'
            Start-Sleep -Seconds 1
            [void](Invoke-Element (Find-InProcess $a.Proc.Id 'Audio i wideo' 5)); Start-Sleep -Seconds 3
            Save-Screen (Join-Path $shots "e2e-$Scenario-5-settings-in-call.png")
            Check ($null -ne (Find-InProcess $a.Proc.Id 'Trwa rozmowa: podgląd jest wyłączony, ale zmiany działają od razu.' 5)) 'w rozmowie podgląd kamery jest wyłączony (kamera jest zajęta przez rozmowę), zmiany urządzeń działają od razu'
            if ($done) { [void](Invoke-Element $done) }
            Start-Sleep -Seconds 1
            Check ($null -ne (Find-InProcess $a.Proc.Id 'Rozłącz' 3)) 'po zamknięciu ustawień rozmowa trwa dalej'
        }
        Set-WindowState $b.Proc.Id Normal; Start-Sleep -Seconds 1
    }
    $nFresh = Get-DistinctColors (Join-Path $shots "e2e-$Scenario-4-call.png")
    Check ($nFresh -gt 3) "zaraz po połączeniu, bez ruszania oknami, na ekranie widać rozmowę (różnych kolorów: $nFresh)"
    $scr = [Windows.Forms.Screen]::PrimaryScreen.Bounds
    foreach ($i in $a, $b) {
        $cw = Get-TopWindows $i.Proc.Id | Where-Object { $_.Current.Name -like '*rozmowa*' } | Select-Object -First 1
        if ($cw) {
            $r = $cw.Current.BoundingRectangle
            Write-Host ("      okno rozmowy {0}: {1}x{2} na ekranie {3}x{4}" -f $i.Id, [int]$r.Width, [int]$r.Height, $scr.Width, $scr.Height)
            Check ([int]$r.Width -ge $scr.Width -and [int]$r.Height -ge $scr.Height) "okno rozmowy ($($i.Id)) zajmuje cały ekran"
        } else { Check $false "znaleziono okno rozmowy ($($i.Id))" }
    }

    # --- rozłączenie czerwoną słuchawką po stronie dzwoniącego
    $hang = Find-InProcess $a.Proc.Id 'Rozłącz' 10
    Check ($null -ne $hang) 'w oknie rozmowy jest czerwona słuchawka „Rozłącz”'
    if ($hang) {
        $r = $hang.Current.BoundingRectangle
        Write-Host ("      słuchawka: x={0} y={1} w={2} h={3}" -f [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)
        [void](Invoke-Element $hang)
    }
    $endB = Wait-LogLine $b.Log 'Rozmowa zakończona: Rozmowa z: .* zakończona' 15 $lenB
    Check ($null -ne $endB) 'druga strona dowiaduje się o rozłączeniu i zamyka okno rozmowy'
    Start-Sleep -Seconds 1
    Check ($null -eq (Find-InProcess $b.Proc.Id 'Rozłącz' 2)) 'okno rozmowy zniknęło po drugiej stronie'
    Check ($null -eq (Find-InProcess $a.Proc.Id 'Rozłącz' 2)) 'okno rozmowy zniknęło po stronie dzwoniącego'
}
finally {
    Save-Screen (Join-Path $shots "e2e-$Scenario-9-end.png")
    foreach ($i in $a, $b) { try { Stop-Process -Id $i.Proc.Id -Force } catch { } }
}
$failed = @($results | Where-Object { -not $_.Ok }).Count
Write-Host ($(if ($failed) { "NIEPOWODZENIA: $failed" } else { 'Wszystko w porządku.' }))
