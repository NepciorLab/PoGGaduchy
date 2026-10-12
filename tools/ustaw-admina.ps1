# Ustawia na serwerze Cloudflare skrót tekstu administratora (sekret ADMIN_HASH). Tekst to zawartość pliku Admin.PMa:
# kto ma taki plik w katalogu programu (i ważny kod instalacji), jest administratorem: dodaje i usuwa osoby, zmienia ich nazwy i obrazy.
# Serwer zna tylko skrót SHA-256 tego tekstu; sam tekst zostaje wyłącznie w pliku Admin.PMa.
#
#   .\tools\ustaw-admina.ps1 -Plik G:\_PoGGaduchy\Admin.PMa              # skrót z istniejącego pliku
#   .\tools\ustaw-admina.ps1 -Plik .\Admin.PMa -Generuj                 # tworzy NOWY, silny, losowy tekst w tym pliku i ustawia jego skrót
#
# Uwaga: krótki, łatwy do odgadnięcia tekst (np. samo imię) daje słabą ochronę. Zalecany jest długi, losowy (opcja -Generuj).
param(
    [Parameter(Mandatory)][string]$Plik,
    [switch]$Generuj
)
. (Join-Path $PSScriptRoot 'lib.ps1')
if ($Generuj) {
    $b = New-Object byte[] 24
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create(); $rng.GetBytes($b); $rng.Dispose()
    $text = 'adm-' + [Convert]::ToBase64String($b).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    [IO.File]::WriteAllText($Plik, $text, (New-Object Text.UTF8Encoding($false)))
    Write-Host "Zapisano nowy tekst administratora w pliku: $Plik"
}
if (-not (Test-Path $Plik)) { throw "Nie ma pliku $Plik." }
$text = ([IO.File]::ReadAllText($Plik, [Text.Encoding]::UTF8)).Trim()
if ($text.Length -lt 1 -or $text.Length -gt 256) { throw 'Zawartość pliku musi mieć od 1 do 256 znaków.' }
if ($text.Length -lt 12) { Write-Warning 'Ten tekst jest krótki, więc łatwy do odgadnięcia. Rozważ -Generuj.' }
Send-Secrets @{ ADMIN_HASH = (Get-Sha256Hex $text) }
Write-Host 'Zapisano ADMIN_HASH na serwerze. Zmiana zaczyna działać po chwili (do ok. minuty).'
