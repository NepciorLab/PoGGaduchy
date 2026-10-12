# Pełna regresja na lokalnym serwerze testowym: testy serwera (protokół, API, sprzątanie), scenariusze rozmów przez prawdziwy
# interfejs, główne okno i ustawienia. Uruchamia też (i zatrzymuje) lokalny serwer z czystym stanem.
#   powershell -ExecutionPolicy Bypass -File tools\test-all.ps1 [-SkipUi]
param([switch]$SkipUi)
. (Join-Path $PSScriptRoot 'lib.ps1')
Use-Node
$env:WRANGLER_SEND_METRICS = 'false'
$summary = [System.Collections.ArrayList]@()
function Stop-Dev { Get-CimInstance Win32_Process -Filter "Name='node.exe' or Name='workerd.exe'" | Where-Object { $_.CommandLine -match 'wrangler|workerd' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue } }
function Run([string]$name, [scriptblock]$block) {
    Write-Host "=== $name"
    $out = & $block 2>&1 | Out-String
    $fails = ([regex]::Matches($out, '(?m)^FAIL:')).Count
    $oks = ([regex]::Matches($out, '(?m)^ok:')).Count
    if ($fails -gt 0 -or $oks -eq 0) { Write-Host $out }
    Write-Host ("    ok: $oks, błędy: $fails")
    [void]$summary.Add([pscustomobject]@{ Test = $name; Ok = $oks; Błędy = $fails })
}

Stop-Dev; Start-Sleep 2
Push-Location $ServerDir
Remove-Item .wrangler\state -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item test\keys.json, .dev.vars -ErrorAction SilentlyContinue
node test/make-dev-vars.mjs | Out-Null; node test/make-install-codes.mjs | Out-Null
$dev = Start-Process -FilePath .\node_modules\.bin\wrangler.cmd -ArgumentList 'dev', '--port', '8787', '--ip', '127.0.0.1', '--show-interactive-dev-session=false' -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $Root '_probe\dev-server.log')
Start-Sleep 14
try {
    Run 'serwer: protokół' { node test/protocol.mjs }
    Run 'serwer: API (obrazy, administrator)' { node test/api.mjs }
    Pop-Location
    if (-not $SkipUi) {
        foreach ($s in 'answer', 'reject', 'cancel', 'nocamera', 'autoanswer', 'settings') {
            Run "rozmowa: $s" { powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'test-call.ps1') -Scenario $s }
        }
        Run 'rozmowa: odbierający bez kamery' { powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'test-call.ps1') -Scenario answer -CalleeNoCamera }
        Run 'główne okno (ustawienia 1 s, X)' { powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'test-ui.ps1') }
        Run 'ustawienia: grupy, obrazy, administrator' { powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'test-settings.ps1') }
    }
}
finally { Stop-Dev; Get-Process PoGGaduchy -ErrorAction SilentlyContinue | Stop-Process -Force }
Write-Host "`n=== PODSUMOWANIE"
$summary | Format-Table -AutoSize | Out-String | Write-Host
$bad = ($summary | Measure-Object -Property Błędy -Sum).Sum
Write-Host ($(if ($bad) { "NIEPOWODZENIA: $bad" } else { 'Wszystko w porządku.' }))
