# Wstawia kod-instalacji.txt osoby do jej katalogu (domyślnie TYLKO ten plik – bez kopii programu i bez zipa).
#   .\tools\przygotuj-kopie.ps1 -Id mama                       # dist\kopie\mama\kod-instalacji.txt
#   .\tools\przygotuj-kopie.ps1 -Id mama -Katalog D:\Mama      # kod trafia do wskazanego katalogu osoby
#   .\tools\przygotuj-kopie.ps1 -Id mama -Exe .\PoGGaduchy.exe  # dodatkowo kopiuje program do tego katalogu
param(
    [Parameter(Mandatory)][string]$Id,
    [string]$Katalog,
    [string]$Exe
)
. (Join-Path $PSScriptRoot 'lib.ps1')
$code = Join-Path $InstallDir "$Id\kod-instalacji.txt"
if (-not (Test-Path $code)) { throw "Brak $code – najpierw: .\tools\dodaj-osobe.ps1 -Id $Id -Nazwa ..." }
$out = if ($Katalog) { $Katalog } else { Join-Path $Root "dist\kopie\$Id" }
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item $code (Join-Path $out 'kod-instalacji.txt') -Force
if ($Exe) { Copy-Item $Exe (Join-Path $out 'PoGGaduchy.exe') -Force }
Write-Host "Gotowe: $out\kod-instalacji.txt (plik zawiera tajny klucz – przekaż go tylko tej osobie)"
