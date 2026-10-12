# Wysyła listę osób (skróty kluczy z server\members.json) na serwer Cloudflare jako sekret MEMBERS.
# Wymaga zalogowanego wranglera (wrangler login) i wdrożonego serwera. Nic innego nie zmienia.
. (Join-Path $PSScriptRoot 'lib.ps1')
if (-not (Test-Path $MembersFile)) { throw 'Brak server\members.json – najpierw dodaj osoby: .\tools\dodaj-osobe.ps1' }
$json = (Get-Content $MembersFile -Raw -Encoding UTF8).Trim()
$count = ([regex]::Matches($json, '"id"')).Count
Write-Host "Wysyłam listę $count osób do Cloudflare (sekret MEMBERS)…"
Send-Secrets @{ MEMBERS = $json }
Write-Host 'Gotowe. Nowy sekret rozchodzi się po sieci Cloudflare przez ok. minutę (do tego czasu nowe klucze mogą dawać błąd 401).'
