# Wysyła listę osób (skróty kluczy z server\members.json) na serwer Cloudflare jako sekret MEMBERS.
# Wymaga zalogowanego wranglera (wrangler login) i wdrożonego serwera. Nic innego nie zmienia.
. (Join-Path $PSScriptRoot 'lib.ps1')
if (-not (Test-Path $MembersFile)) { throw 'Brak server\members.json – najpierw dodaj osoby: .\tools\dodaj-osobe.ps1' }
$json = (Get-Content $MembersFile -Raw -Encoding UTF8).Trim()
$count = @($json | ConvertFrom-Json).Count
Write-Host "Wysyłam listę $count osób do Cloudflare (sekret MEMBERS)…"
$json | Invoke-Wrangler secret put MEMBERS
if ($LASTEXITCODE -ne 0) { throw 'Nie udało się zapisać sekretu MEMBERS.' }
Write-Host 'Gotowe. Serwer korzysta z nowej listy od razu (połączone programy mogą wymagać ponownego połączenia).'
