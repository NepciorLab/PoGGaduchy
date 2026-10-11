# Wdraża serwer (Worker + Durable Object) na darmowe konto Cloudflare (adres *.workers.dev).
# Wymaga zalogowanego wranglera: .\server\node_modules\.bin\wrangler login   (otwiera przeglądarkę, loguje się właściciel konta)
# Po wdrożeniu zapisuje adres serwera w instalacje\serwer.txt (używa go dodaj-osobe.ps1).
. (Join-Path $PSScriptRoot 'lib.ps1')
$out = Invoke-Wrangler deploy 2>&1 | Tee-Object -Variable log | Out-String
Write-Host $out
$m = [regex]::Match($out, 'https://[a-z0-9-]+\.[a-z0-9-]+\.workers\.dev')
if ($m.Success) {
    New-Item -ItemType Directory -Force $InstallDir | Out-Null
    Set-Content $ServerUrlFile $m.Value -Encoding ASCII
    Write-Host "Adres serwera: $($m.Value) (zapisany w instalacje\serwer.txt)"
}
else { Write-Host 'Nie znalazłem adresu workers.dev w wyniku – wpisz go ręcznie do instalacje\serwer.txt.' }
