# Zrzuty ekranu okien programu (tryb --demo) dla różnych rozmiarów ekranu – do sprawdzania układu.
# Rozmiary podajemy w jednostkach niezależnych od urządzenia (piksele ekranu / skalowanie Windows), np.:
#   1920x1080 przy 100% = 1920x1080, przy 125% = 1536x864, przy 150% = 1280x720.
# Uruchomienie:  powershell -ExecutionPolicy Bypass -File tools\shots.ps1 [-Sizes 1920x1080,1280x720] [-Scenes main,settings,nocamera,incoming,banner]
param(
    [string[]]$Sizes = @('1280x720', '1536x864', '1920x1080', '2293x960', '3440x1440'),
    [string[]]$Scenes = @('main', 'nocamera', 'incoming', 'settings', 'banner'),
    [string]$Exe = (Join-Path $PSScriptRoot '..\Source\bin\Debug\net8.0-windows10.0.19041.0\PoGGaduchy.exe')
)
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$demo = Join-Path $root '_probe\demo'
New-Item -ItemType Directory -Force (Join-Path $demo 'images'), (Join-Path $root 'shots') | Out-Null
$photo = Get-ChildItem 'G:\Claude\SeniorWhatsApp\images\*.jpg' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($photo -and -not (Test-Path (Join-Path $demo 'images\demo.png'))) { Copy-Item $photo.FullName (Join-Path $demo 'images\demo.png') }
$env:PGG_DATA = $demo
foreach ($size in $Sizes) {
    foreach ($scene in $Scenes) {
        $out = Join-Path $root "shots\$scene-$size.png"
        $a = @('--demo', '--size', $size, '--screenshot', $out)
        switch ($scene) {
            'main' { $a += '--select' }
            'nocamera' { $a += '--nocamera', '--select' }
            'incoming' { $a += '--incoming' }
            'settings' { $a += '--settings', '--select' }
            'banner' { $a += '--select', '--banner', 'Nieodebrane połączenie od: Córka Kasia (14:20).' }
        }
        Start-Process $Exe -ArgumentList $a -Wait
        Write-Host "$scene $size -> $out"
    }
}
