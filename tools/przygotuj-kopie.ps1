# Przygotowuje gotowy folder (i zip) z programem dla jednej osoby: PoGGaduchy.exe + jej kod-instalacji.txt.
#   .\tools\przygotuj-kopie.ps1 -Id mama                       # program z najnowszego wydania na GitHubie (wymaga gh)
#   .\tools\przygotuj-kopie.ps1 -Id mama -Exe .\PoGGaduchy.exe  # własny plik programu
# Wynik: dist\kopie\<id>\ oraz dist\kopie\PoGGaduchy-<id>.zip. Zip zawiera klucz tej osoby – przekaż go tylko jej.
param(
    [Parameter(Mandatory)][string]$Id,
    [string]$Exe
)
. (Join-Path $PSScriptRoot 'lib.ps1')
$code = Join-Path $InstallDir "$Id\kod-instalacji.txt"
if (-not (Test-Path $code)) { throw "Brak $code – najpierw: .\tools\dodaj-osobe.ps1 -Id $Id -Nazwa ..." }
$out = Join-Path $Root "dist\kopie\$Id"
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $out | Out-Null
if ($Exe) { Copy-Item $Exe (Join-Path $out 'PoGGaduchy.exe') }
else {
    $tmp = Join-Path $Root 'dist\pobrane'
    New-Item -ItemType Directory -Force $tmp | Out-Null
    Remove-Item "$tmp\*.zip" -ErrorAction SilentlyContinue
    gh release download --repo NepciorLab/PoGGaduchy --pattern 'PoGGaduchy-*-win-x64.zip' --dir $tmp
    if ($LASTEXITCODE -ne 0) { throw 'Nie udało się pobrać wydania (czy wydanie istnieje?).' }
    Expand-Archive (Get-ChildItem "$tmp\*.zip" | Select-Object -First 1).FullName $out -Force
}
Copy-Item $code (Join-Path $out 'kod-instalacji.txt')
$zip = Join-Path $Root "dist\kopie\PoGGaduchy-$Id.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "Gotowe: $out`nZip do przekazania: $zip"
Write-Host 'Na komputerze osoby: rozpakuj w dowolnym folderze i uruchom PoGGaduchy.exe (zgoda na kamerę/mikrofon nie jest potrzebna – program ma ją wbudowaną).'
