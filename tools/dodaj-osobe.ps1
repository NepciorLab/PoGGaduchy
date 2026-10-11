# Dodaje osobę (rodzinę) do serwera: generuje jej tajny klucz, zapisuje SKRÓT klucza na liście serwera
# i tworzy plik kod-instalacji.txt dla jej kopii programu (folder instalacje\<id>\).
#
#   .\tools\dodaj-osobe.ps1 -Id mama -Nazwa "Mama"            # nowa osoba (lista lokalna + kod instalacji)
#   .\tools\dodaj-osobe.ps1 -Id mama -NowyKlucz               # nowy klucz dla istniejącej osoby (stary przestaje działać)
#   .\tools\dodaj-osobe.ps1 -Id mama -Usun                    # usunięcie osoby z listy
#   .\tools\dodaj-osobe.ps1 -Id mama -Nazwa "Mama" -Wyslij    # j.w., a potem wysyła listę na serwer Cloudflare (sekret MEMBERS)
#
# Lista osób (server\members.json) zawiera tylko skróty SHA-256 kluczy i nie trafia do repozytorium.
# Bez przełącznika -Wyslij nic nie jest wysyłane do Cloudflare; wtedy wyślij ją później: .\tools\wyslij-osoby.ps1
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9_-]{1,32}$')][string]$Id,
    [string]$Nazwa,
    [switch]$NowyKlucz,
    [switch]$Usun,
    [string]$Serwer,
    [switch]$Wyslij
)
. (Join-Path $PSScriptRoot 'lib.ps1')
New-Item -ItemType Directory -Force $InstallDir | Out-Null

$members = @(Read-Members)
$existing = $members | Where-Object { $_.id -eq $Id }

if ($Usun) {
    if (-not $existing) { throw "Nie ma osoby '$Id' na liście." }
    Save-Members @($members | Where-Object { $_.id -ne $Id })
    Write-Host "Usunięto '$Id' z listy. Wyślij listę na serwer: .\tools\wyslij-osoby.ps1"
}
else {
    if ($existing -and -not $NowyKlucz -and -not $Nazwa) { throw "Osoba '$Id' już jest na liście. Podaj -NowyKlucz, aby wydać nowy klucz, albo -Nazwa, aby zmienić nazwę." }
    if (-not $existing -and -not $Nazwa) { throw 'Podaj -Nazwa (nazwa wyświetlana osoby, np. "Mama").' }

    if (-not $Serwer -and (Test-Path $ServerUrlFile)) { $Serwer = (Get-Content $ServerUrlFile -Raw).Trim() }
    if (-not $Serwer) { throw 'Podaj -Serwer (adres wdrożonego serwera, np. https://poggaduchy.twoje-konto.workers.dev) – zapamiętam go w instalacje\serwer.txt.' }
    if ($Serwer -notmatch '^https://' -and $Serwer -notmatch '^http://(127\.0\.0\.1|localhost)') { throw 'Adres serwera musi zaczynać się od https://.' }
    $Serwer = $Serwer.TrimEnd('/')
    Set-Content $ServerUrlFile $Serwer -Encoding ASCII

    if ($existing -and -not $NowyKlucz) {
        $existing.name = $Nazwa
        Save-Members $members
        Write-Host "Zmieniono nazwę osoby '$Id' na '$Nazwa'. (Klucz bez zmian – kod instalacji z poprzedniego razu nadal działa.)"
    }
    else {
        $key = New-Key
        $entry = [pscustomobject]@{ id = $Id; name = $(if ($Nazwa) { $Nazwa } else { $existing.name }); hash = (Get-Sha256Hex $key) }
        Save-Members (@($members | Where-Object { $_.id -ne $Id }) + $entry)
        $dir = Join-Path $InstallDir $Id
        New-Item -ItemType Directory -Force $dir | Out-Null
        [IO.File]::WriteAllText((Join-Path $dir 'kod-instalacji.txt'), (New-InstallCode $Serwer $key $Id), (New-Object Text.UTF8Encoding($false)))
        Write-Host "Osoba '$Id' ($($entry.name)): kod instalacji zapisany w $dir\kod-instalacji.txt"
        Write-Host "Ten plik trzeba położyć obok PoGGaduchy.exe na komputerze tej osoby (patrz: .\tools\przygotuj-kopie.ps1)."
    }
}

if ($Wyslij) { & (Join-Path $PSScriptRoot 'wyslij-osoby.ps1') }
else { Write-Host 'Lista osób NIE została jeszcze wysłana na serwer. Zrób to poleceniem: .\tools\wyslij-osoby.ps1' }
