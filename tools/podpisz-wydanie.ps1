# Podpisuje wydanie już opublikowane na GitHubie (po tym, jak workflow zbuduje i wyda plik zip):
#   powershell -ExecutionPolicy Bypass -File tools\podpisz-wydanie.ps1 -Wersja 1.2.0
# Pobiera PoGGaduchy-<wersja>-win-x64.zip z wydania, podpisuje Twoim kluczem prywatnym i dodaje do wydania plik .zip.sig.
# Programy (od wersji 1.2.0) instalują aktualizację tylko z poprawnym podpisem. Wymaga zalogowanego GitHub CLI (gh).
param(
    [Parameter(Mandatory)][string]$Wersja,
    [string]$Klucz = '',
    [string]$Repo = 'NepciorLab/PoGGaduchy'
)
. "$PSScriptRoot\lib.ps1"

if ($Wersja -notmatch '^\d+\.\d+\.\d+$') { throw 'Wersja ma mieć postać X.Y.Z (np. 1.2.0).' }
if (-not $Klucz) { $Klucz = Join-Path $InstallDir 'klucz-podpisu.pem' }
if (-not (Test-Path $Klucz)) { throw "Nie ma klucza prywatnego: $Klucz. Wskaż go parametrem -Klucz (np. zapisany z menedżera haseł do pliku)." }
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'Brak GitHub CLI (gh).' }

$tag = "v$Wersja"
$zip = "PoGGaduchy-$Wersja-win-x64.zip"
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('pgg-sign-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    gh release download $tag --repo $Repo --pattern $zip --dir $tmp
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $tmp $zip))) { throw "Nie udało się pobrać $zip z wydania $tag." }

    $s = Read-Host 'Hasło do klucza podpisu' -AsSecureString
    $p = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)
    try { $env:PGG_KEY_PASS = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($p) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($p) }

    Use-Node
    $sig = Join-Path $tmp "$zip.sig"
    node (Join-Path $PSScriptRoot 'podpis.mjs') sign (Join-Path $tmp $zip) $Wersja $sig $Klucz
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $sig)) { throw 'Podpisywanie się nie udało (złe hasło?).' }

    gh release upload $tag $sig --repo $Repo --clobber
    if ($LASTEXITCODE -ne 0) { throw 'Nie udało się dodać podpisu do wydania.' }
    Write-Host "Wydanie $tag podpisane."
}
finally {
    Remove-Item Env:\PGG_KEY_PASS -ErrorAction SilentlyContinue
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
