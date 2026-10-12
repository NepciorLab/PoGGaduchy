# Zapisuje na serwerze Cloudflare dane klucza TURN (Cloudflare Realtime): ID klucza i token API.
# Klucz tworzysz w panelu Cloudflare: Realtime → TURN Server → Create. Wartości wpisujesz tutaj sam – skrypt ich nie wyświetla ani nie zapisuje w plikach.
. (Join-Path $PSScriptRoot 'lib.ps1')
$id = Read-Host 'TURN Key ID'
$tokenSecure = Read-Host 'TURN Key API Token (wpisywany znak po znaku, niewidoczny)' -AsSecureString
$token = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($tokenSecure))
if (-not $id -or -not $token) { throw 'Podaj oba pola.' }
Send-Secrets @{ TURN_KEY_ID = $id.Trim(); TURN_KEY_API_TOKEN = $token.Trim() }
Write-Host 'Zapisano. Sprawdź: .\tools\serwer-status.ps1'
