# Wspólne funkcje skryptów administracyjnych (dołączane przez ". .\lib.ps1").
$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ServerDir = Join-Path $Root 'server'
$MembersFile = Join-Path $ServerDir 'members.json'       # lista osób z SKRÓTAMI kluczy (poza repozytorium)
$InstallDir = Join-Path $Root 'instalacje'               # kody instalacji osób (poza repozytorium)
$ServerUrlFile = Join-Path $InstallDir 'serwer.txt'      # adres wdrożonego serwera (poza repozytorium)

# Node.js bez instalacji w systemie: zmienna PGG_NODE (folder z node.exe) albo folder narzędzi w profilu użytkownika.
function Use-Node {
    $dir = $env:PGG_NODE
    if (-not $dir) {
        $dir = Get-ChildItem (Join-Path $env:USERPROFILE 'tools') -Directory -Filter 'node-v*' -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $dir -or -not (Test-Path (Join-Path $dir 'node.exe'))) {
        throw "Nie znaleziono Node.js. Rozpakuj przenośny Node (zip z nodejs.org) do $env:USERPROFILE\tools\ albo ustaw zmienną PGG_NODE."
    }
    $env:Path = "$dir;$env:Path"
}

function Invoke-Wrangler {
    $in = @($input)
    Use-Node
    $w = Join-Path $ServerDir 'node_modules\.bin\wrangler.cmd'
    if (-not (Test-Path $w)) { Push-Location $ServerDir; try { npm install --no-audit --no-fund } finally { Pop-Location } }
    Push-Location $ServerDir
    try { if ($in.Count) { $in | & $w @args } else { & $w @args } } finally { Pop-Location }
}

function New-Key {
    $b = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create(); $rng.GetBytes($b); $rng.Dispose()
    [Convert]::ToBase64String($b).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Get-Sha256Hex([string]$Text) {
    $h = [Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($Text))
    -join ($h | ForEach-Object { $_.ToString('x2') })
}

function Read-Members {
    if (-not (Test-Path $MembersFile)) { return @() }
    $raw = (Get-Content $MembersFile -Raw -Encoding UTF8).Trim()
    if (-not $raw -or $raw -eq '[]') { return @() }
    $parsed = $raw | ConvertFrom-Json
    foreach ($m in $parsed) { [pscustomobject]@{ id = [string]$m.id; name = [string]$m.name; hash = [string]$m.hash } }
}

function Save-Members($List) {
    $items = foreach ($m in @($List)) { ConvertTo-Json -InputObject ([ordered]@{ id = [string]$m.id; name = [string]$m.name; hash = [string]$m.hash }) -Compress }
    $json = '[' + (@($items) -join ',') + ']'
    [IO.File]::WriteAllText($MembersFile, $json, (New-Object Text.UTF8Encoding($false)))
}

# Kod instalacji: "PGG1." + JSON {s: adres serwera, k: klucz, i: id} w base64url
function New-InstallCode([string]$Server, [string]$Key, [string]$Id) {
    $json = ConvertTo-Json -InputObject ([ordered]@{ s = $Server; k = $Key; i = $Id }) -Compress
    'PGG1.' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}
