# Sprawdza serwer: czy odpowiada, czy klucz danej osoby działa i czy działa TURN.
#   .\tools\serwer-status.ps1 -Id mama
param([string]$Id)
. (Join-Path $PSScriptRoot 'lib.ps1')
$server = (Get-Content $ServerUrlFile -Raw).Trim()
Write-Host "Serwer: $server"
Write-Host ('GET / : ' + (Invoke-WebRequest "$server/" -UseBasicParsing).Content.Trim())
if ($Id) {
    $code = (Get-Content (Join-Path $InstallDir "$Id\kod-instalacji.txt") -Raw).Trim().Substring(5).Replace('-', '+').Replace('_', '/')
    $code = $code.PadRight($code.Length + (4 - $code.Length % 4) % 4, '=')
    $key = ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($code)) | ConvertFrom-Json).k
    $r = Invoke-RestMethod "$server/api/status?turn=1" -Headers @{ Authorization = "Bearer $key" }
    $r | Format-List
}
