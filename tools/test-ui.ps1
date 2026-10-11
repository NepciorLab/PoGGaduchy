# Test interfejsu głównego okna przez prawdziwą mysz i UI Automation:
# krótkie kliknięcie „Ustawienia” pokazuje podpowiedź (i znika po ok. 3 s), przytrzymanie 1 s otwiera ustawienia,
# „Gotowe” je zamyka, czerwony X wyłącza program.
param([string]$Exe = (Join-Path $PSScriptRoot '..\Source\bin\Debug\net8.0-windows10.0.19041.0\PoGGaduchy.exe'), [string]$Id = 'ala')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'uia.ps1')
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$shots = Join-Path $root 'shots'
$results = [System.Collections.ArrayList]@()
function Check([bool]$ok, [string]$what) { [void]$results.Add($ok); Write-Host (($(if ($ok) { 'ok:   ' } else { 'FAIL: ' })) + $what) }
function Mouse-Hold([int]$x, [int]$y, [int]$ms) {
    [void][PggNative]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 150
    [PggNative]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds $ms
    [PggNative]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
}
$env:PGG_DATA = Join-Path $root "_probe\$Id"; $env:PGG_FAKE_MEDIA = '1'; $env:PGG_NO_UPDATE = '1'
$p = Start-Process $Exe -PassThru
try {
    $btn = Find-InProcess $p.Id 'Ustawienia (przytrzymaj)' 20
    Check ($null -ne $btn) 'jest przycisk „Ustawienia (przytrzymaj)”'
    $r = $btn.Current.BoundingRectangle; $cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
    Write-Host ("      przycisk ustawień: {0}x{1} px" -f [int]$r.Width, [int]$r.Height)

    Mouse-Hold $cx $cy 150
    Start-Sleep -Milliseconds 400
    $hint = Find-InProcess $p.Id 'Aby otworzyć ustawienia, przytrzymaj ten przycisk przez 1 sekundę.' 2
    Check ($null -ne $hint -and -not $hint.Current.IsOffscreen) 'krótkie kliknięcie pokazuje podpowiedź'
    Save-Screen (Join-Path $shots 'ui-hint.png')
    Check ($null -eq (Find-InProcess $p.Id 'Gotowe' 1)) 'krótkie kliknięcie nie otwiera ustawień'
    Start-Sleep -Seconds 3
    $hint = Find-InProcess $p.Id 'Aby otworzyć ustawienia, przytrzymaj ten przycisk przez 1 sekundę.' 1
    Check ($null -eq $hint -or $hint.Current.IsOffscreen) 'podpowiedź znika po ok. 3 s'

    Mouse-Hold $cx $cy 1400
    $done = Find-InProcess $p.Id 'Gotowe' 5
    Check ($null -ne $done) 'przytrzymanie 1 s otwiera ustawienia'
    Start-Sleep -Seconds 1
    Save-Screen (Join-Path $shots 'ui-settings.png')
    if ($done) { [void](Invoke-Element $done) }
    Start-Sleep -Milliseconds 700
    Check ($null -eq (Find-InProcess $p.Id 'Gotowe' 1)) '„Gotowe” zamyka ustawienia'

    $x = Find-InProcess $p.Id 'Wyłącz program' 5
    $xr = $x.Current.BoundingRectangle
    Write-Host ("      przycisk X: {0}x{1} px" -f [int]$xr.Width, [int]$xr.Height)
    Check ([int]$xr.Width -eq 128 -and [int]$xr.Height -eq 128) 'czerwony X ma 128 x 128'
    [void](Invoke-Element $x)
    Start-Sleep -Seconds 2
    Check $p.HasExited 'czerwony X wyłącza program'
}
finally { if (-not $p.HasExited) { Stop-Process $p -Force } }
$bad = @($results | Where-Object { -not $_ }).Count
Write-Host ($(if ($bad) { "NIEPOWODZENIA: $bad" } else { 'Wszystko w porządku.' }))
