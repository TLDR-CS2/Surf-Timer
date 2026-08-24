[CmdletBinding()]
param(
    [string]$Surf1Map = "surf_boreas",
    [string]$Surf3Map = "surf_cyka_ksf"
)

$ErrorActionPreference = "Stop"
$serverExe = "C:\CS2Server\server\game\bin\win64\cs2.exe"
$servers = @(Get-Process cs2 -ErrorAction SilentlyContinue | Where-Object Path -eq $serverExe)
if ($servers.Count -ne 2) { throw "Expected exactly two local CS2 server processes; found $($servers.Count)." }

$instances = @(
    [pscustomobject]@{ Name="surf-1"; Root="C:\CS2Server\server\game\csgo\addons\swiftlys2"; Map=$Surf1Map },
    [pscustomobject]@{ Name="surf-3"; Root="C:\CS2Server\server\game\csgo\addons\swiftlys2-surf3"; Map=$Surf3Map }
)
foreach ($instance in $instances) {
    $configPath = Join-Path $instance.Root "configs\plugins\surf_timer\config.jsonc"
    $config = Get-Content -Raw -LiteralPath $configPath | ConvertFrom-Json
    if ($config.SurfTimer.ServerId -ne $instance.Name) {
        throw "$($instance.Name) config reports ServerId '$($config.SurfTimer.ServerId)'."
    }
    $log = Get-ChildItem -File -LiteralPath (Join-Path $instance.Root "logs\managed") |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $text = Get-Content -Raw -LiteralPath $log.FullName
    if ($text -notmatch [regex]::Escape("Record repository ready on connection surftimer for server $($instance.Name).")) {
        throw "$($instance.Name) latest log does not show a ready shared database repository."
    }
    if ($text -notmatch [regex]::Escape("Map loaded: $($instance.Map)")) {
        throw "$($instance.Name) latest log does not show expected map $($instance.Map)."
    }
    if ($text -match '(?im)\| (Error|Critical) \|') {
        throw "$($instance.Name) latest managed log contains an error or critical entry."
    }
    Write-Host "$($instance.Name): healthy, map=$($instance.Map), config=$configPath"
}

& (Join-Path (Split-Path -Parent $PSScriptRoot) "local-server\Test-DatabaseConsistency.ps1")
if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "Database consistency audit failed." }
Write-Host "Local multi-server deployment smoke test passed."
