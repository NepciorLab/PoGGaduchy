# Zapisuje na serwerze Cloudflare dane klucza TURN (Cloudflare Realtime): ID klucza i token API.
# Klucz tworzysz w panelu Cloudflare: Realtime → TURN Server → Create. Wartości wpisujesz tutaj sam – skrypt ich nie wyświetla ani nie zapisuje w plikach.
. (Join-Path $PSScriptRoot 'lib.ps1')
$id = Read-Host 'TURN Key ID'
$tokenSecure = Read-Host 'TURN Key API Token (wpisywany znak po znaku, niewidoczny)' -AsSecureString
$token = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($tokenSecure))
if (-not $id -or -not $token) { throw 'Podaj oba pola.' }
$id | Invoke-Wrangler secret put TURN_KEY_ID
if ($LASTEXITCODE -ne 0) { throw 'Nie udało się zapisać TURN_KEY_ID.' }
$token | Invoke-Wrangler secret put TURN_KEY_API_TOKEN
if ($LASTEXITCODE -ne 0) { throw 'Nie udało się zapisać TURN_KEY_API_TOKEN.' }
Write-Host 'Zapisano. Sprawdź: .\tools\serwer-status.ps1'
