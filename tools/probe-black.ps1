# Sonda: kiedy okno rozmowy odbierającego jest puste na ekranie i co je "budzi". Uruchamia dwie kopie, łączy rozmowę,
# a potem po kolei: zrzut świeżo po połączeniu, po ruchu myszą, po wysunięciu na wierzch, po minimalizacji i przywróceniu.
param([string]$Exe = (Join-Path $PSScriptRoot '..\Source\bin\Debug\net8.0-windows10.0.19041.0\PoGGaduchy.exe'))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'uia.ps1')
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$shots = Join-Path $root 'shots'
function Start-Instance([string]$id) {
    $dir = Join-Path $root "_probe\$id"
    foreach ($f in 'settings.json', 'log.txt') { Remove-Item (Join-Path $dir $f) -ErrorAction SilentlyContinue }
    $env:PGG_DATA = $dir; $env:PGG_FAKE_MEDIA = '1'; $env:PGG_NO_UPDATE = '1'; $env:PGG_NO_CAMERA = ''; $env:PGG_FORCE_RELAY = ''
    [pscustomobject]@{ Id = $id; Proc = (Start-Process $Exe -PassThru) }
}
function Show-Colors([string]$label) {
    $p = Join-Path $shots 'probe.png'; Save-Screen $p
    Write-Host ("{0,-45} różnych kolorów: {1}" -f $label, (Get-DistinctColors $p))
}
$a = Start-Instance 'ala'; $b = Start-Instance 'bartek'
try {
    Find-InProcess $a.Proc.Id 'Bartek' 25 | Out-Null; Find-InProcess $b.Proc.Id 'Ala' 25 | Out-Null; Start-Sleep 2
    [void](Invoke-Element (Find-InProcess $a.Proc.Id 'Bartek' 5)); Start-Sleep -Milliseconds 700
    [void](Invoke-Element (Find-InProcess $a.Proc.Id 'Zadzwoń wideo' 5))
    $answer = Find-InProcess $b.Proc.Id 'Odbierz' 20
    [void](Invoke-Element $answer)
    Start-Sleep -Seconds 8
    Show-Colors 'świeżo po połączeniu (bartek, odbierający, na wierzchu)'
    Set-WindowState $b.Proc.Id Minimized; Start-Sleep 1.5
    Show-Colors 'dzwoniący (ala) nietknięty, bartek zminimalizowany'
    Set-WindowState $b.Proc.Id Normal; Start-Sleep 1.5
    Show-Colors 'bartek po przywróceniu z minimalizacji'
    Start-Sleep -Seconds 3; Measure 'po kolejnych 3 s'
    [void][PggNative]::SetCursorPos(1700, 700); Start-Sleep -Milliseconds 300; [void][PggNative]::SetCursorPos(1720, 720); Start-Sleep 1
    Show-Colors 'po ruchu myszą nad oknem'
    Bring-ToFront $b.Proc.Id; Start-Sleep 1
    Show-Colors 'po wysunięciu na wierzch (Bring-ToFront)'
    Set-WindowState $b.Proc.Id Minimized; Start-Sleep 1; Set-WindowState $b.Proc.Id Normal; Start-Sleep 1.5
    Show-Colors 'po minimalizacji i przywróceniu bartka'
}
finally { foreach ($i in $a, $b) { try { Stop-Process -Id $i.Proc.Id -Force } catch { } } }
