# Tworzy parę kluczy do podpisywania wydań (raz na zawsze). Uruchom SAM, w zwykłym oknie PowerShell:
#   powershell -ExecutionPolicy Bypass -File tools\generuj-klucz-podpisu.ps1
# Klucz PRYWATNY (zaszyfrowany Twoim hasłem) trafia do instalacje\klucz-podpisu.pem - poza repozytorium.
# Zapisz w menedżerze haseł: CAŁĄ zawartość tego pliku oraz hasło. Bez nich nie wydasz aktualizacji, którą programy zaakceptują.
# Klucz PUBLICZNY trafia do Source\Assets\update-public-key.txt i jest wbudowany w program (nie jest tajny).
param([string]$Plik = '')
. "$PSScriptRoot\lib.ps1"

if (-not $Plik) { $Plik = Join-Path $InstallDir 'klucz-podpisu.pem' }
$pub = Join-Path $Root 'Source\Assets\update-public-key.txt'
if ((Test-Path $Plik) -or (Test-Path $pub)) { throw "Klucz już istnieje ($Plik lub $pub). Nie nadpisuję - usuń je ręcznie, jeśli naprawdę chcesz zacząć od nowa." }
if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir | Out-Null }

function ConvertTo-Plain([Security.SecureString]$s) {
    $p = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($p) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($p) }
}
$a = ConvertTo-Plain (Read-Host 'Hasło do klucza podpisu (min. 8 znaków)' -AsSecureString)
$b = ConvertTo-Plain (Read-Host 'Powtórz hasło' -AsSecureString)
if ($a -ne $b) { throw 'Hasła się różnią.' }
if ($a.Length -lt 8) { throw 'Hasło jest za krótkie.' }

Use-Node
$env:PGG_KEY_PASS = $a
try { node (Join-Path $PSScriptRoot 'podpis.mjs') gen $Plik $pub; if ($LASTEXITCODE -ne 0) { throw 'Nie udało się utworzyć klucza.' } }
finally { Remove-Item Env:\PGG_KEY_PASS -ErrorAction SilentlyContinue }

Write-Host ''
Write-Host "Klucz prywatny: $Plik"
Write-Host 'ZRÓB TERAZ: skopiuj całą zawartość tego pliku i hasło do menedżera haseł.'
Write-Host "Klucz publiczny (do wbudowania w program): $pub"
