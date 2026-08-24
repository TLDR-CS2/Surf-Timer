[CmdletBinding()]
param(
    [string]$Map = "surf_cyka_ksf",
    [string]$WorkshopId = "",
    [int]$Port = 27016
)

$ErrorActionPreference = "Stop"

$serverRoot = "C:\CS2Server\server"
$serverExe = Join-Path $serverRoot "game\bin\win64\cs2.exe"
$swiftlyRoot = Join-Path $serverRoot "game\csgo\addons\swiftlys2-surf3"
$runtimeRoot = Join-Path $PSScriptRoot ".runtime"
$pidPath = Join-Path $runtimeRoot "surf-3.pid"
$workshopMaps = @{
    "surf_boreas" = "3133346713"
    "surf_kitsune" = "3076153623"
    "surf_mesa_revo" = "3076980482"
    "surf_mom" = "3282137145"
    "surf_prisma" = "3319154265"
    "surf_cyka_ksf" = "3263197243"
    "surf_elysium" = "3147764666"
    "surf_goliath" = "3448505317"
    "surf_mesa_aether" = "3125360522"
    "surf_aquaflow" = "3255589335"
    "surf_newbie" = "3263974751"
    "surf_zeitgeist" = "3265329080"
    "surf_jive" = "3318285030"
    "surf_cannonball" = "3152119098"
    "surf_sippysip" = "3246776437"
    "surf_lt_omnific" = "3660894345"
}

if (-not (Test-Path -LiteralPath $swiftlyRoot)) {
    throw "surf-3 has not been initialized. Run Initialize-Surf3.ps1 first."
}
if ([string]::IsNullOrWhiteSpace($WorkshopId) -and $workshopMaps.ContainsKey($Map)) {
    $WorkshopId = $workshopMaps[$Map]
}
if (Test-Path -LiteralPath $pidPath) {
    $oldPid = [int](Get-Content -LiteralPath $pidPath)
    if (Get-Process -Id $oldPid -ErrorAction SilentlyContinue) {
        throw "surf-3 is already running (PID $oldPid)."
    }
    Remove-Item -LiteralPath $pidPath -Force
}

& (Join-Path $PSScriptRoot "Start-Database.ps1")

$sourceMapConfig = Join-Path (Join-Path $PSScriptRoot "maps") ($Map + ".cfg")
$serverConfigRoot = Join-Path $serverRoot "game\csgo\cfg\surftimer-surf3"
$serverMapConfigRoot = Join-Path $serverConfigRoot "maps"
New-Item -ItemType Directory -Force -Path $serverMapConfigRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "surf.cfg") -Destination (Join-Path $serverConfigRoot "surf.cfg") -Force
(Get-Content -LiteralPath (Join-Path $serverConfigRoot "surf.cfg")) `
    -replace 'exec surftimer/maps/active-map.cfg', 'exec surftimer-surf3/maps/active-map.cfg' |
    Set-Content -LiteralPath (Join-Path $serverConfigRoot "surf.cfg")
Set-Content -LiteralPath (Join-Path $serverConfigRoot "server.cfg") -Value "exec surftimer-surf3/surf.cfg"
if (Test-Path -LiteralPath $sourceMapConfig) {
    Copy-Item -LiteralPath $sourceMapConfig -Destination (Join-Path $serverMapConfigRoot "active-map.cfg") -Force
}
else {
    Set-Content -LiteralPath (Join-Path $serverMapConfigRoot "active-map.cfg") -Value "// No local override for $Map."
}

$arguments = [System.Collections.Generic.List[object]]@(
    "-dedicated", "-console", "-usercon", "-insecure",
    "-port", $Port,
    "-sw_path", "addons/swiftlys2-surf3",
    "-sw_logpath", "addons/swiftlys2-surf3/logs",
    "-sw_loglevel", "INFO",
    "+hostname", "SurfTimer Hard | Tier 1-7",
    "+servercfgfile", "surftimer-surf3/server.cfg",
    "+sv_lan", "1",
    "+game_type", "0",
    "+game_mode", "0",
    "+exec", "surftimer-surf3/surf.cfg"
)
if ([string]::IsNullOrWhiteSpace($WorkshopId)) {
    $arguments.Add("+map")
    $arguments.Add($Map)
}
else {
    $arguments.Add("+map")
    $arguments.Add("de_dust2")
    $arguments.Add("+host_workshop_map")
    $arguments.Add($WorkshopId)
}

$process = Start-Process -FilePath $serverExe -ArgumentList $arguments `
    -WorkingDirectory $serverRoot -WindowStyle Hidden -PassThru
New-Item -ItemType Directory -Force -Path $runtimeRoot | Out-Null
Set-Content -LiteralPath $pidPath -Value $process.Id

Write-Host "surf-3 started (PID $($process.Id), port $Port)."
Write-Host "Connect with: connect 127.0.0.1:$Port"
Write-Host "Requested map: $Map$(if ($WorkshopId) { " (Workshop $WorkshopId)" })"
Write-Host "Logs: $swiftlyRoot\logs"
