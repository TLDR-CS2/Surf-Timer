[CmdletBinding()]
param(
    [string]$Map = "surf_boreas",
    [string]$WorkshopId = "",
    [int]$Port = 27015
)

$ErrorActionPreference = "Stop"

$serverRoot = "C:\CS2Server\server"
$serverExe = Join-Path $serverRoot "game\bin\win64\cs2.exe"
$startDatabase = Join-Path $PSScriptRoot "Start-Database.ps1"
$workshopMaps = @{
    "surf_boreas"  = "3133346713"
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

if ([string]::IsNullOrWhiteSpace($WorkshopId) -and $workshopMaps.ContainsKey($Map)) {
    $WorkshopId = $workshopMaps[$Map]
}

if (-not (Test-Path -LiteralPath $serverExe)) {
    throw "CS2 server executable was not found at $serverExe. Run Update-Server.ps1 first."
}

$sourceMapConfig = Join-Path (Join-Path $PSScriptRoot "maps") ($Map + ".cfg")
$serverConfigRoot = Join-Path $serverRoot "game\csgo\cfg\surftimer"
$serverMapConfigRoot = Join-Path $serverConfigRoot "maps"
New-Item -ItemType Directory -Force -Path $serverMapConfigRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "surf.cfg") -Destination (Join-Path $serverConfigRoot "surf.cfg") -Force
if (Test-Path -LiteralPath $sourceMapConfig) {
    Copy-Item -LiteralPath $sourceMapConfig -Destination (Join-Path $serverMapConfigRoot "active-map.cfg") -Force
}
else {
    Set-Content -LiteralPath (Join-Path $serverMapConfigRoot "active-map.cfg") -Value "// No local override for $Map."
}

& $startDatabase

Push-Location $serverRoot
try {
    $existing = Get-Process cs2 -ErrorAction SilentlyContinue |
        Where-Object Path -eq $serverExe

    if ($existing) {
        throw "The local CS2 server is already running (PID $($existing.Id -join ', '))."
    }

    $arguments = [System.Collections.Generic.List[object]]@(
        "-dedicated"
        "-console"
        "-usercon"
        "-insecure"
        "-port", $Port
        "-condebug"
        "-conclearlog"
        "-sw_loglevel", "INFO"
        "+sv_lan", "1"
        "+game_type", "0"
        "+game_mode", "0"
        "+exec", "surftimer/surf.cfg"
    )

    if ([string]::IsNullOrWhiteSpace($WorkshopId)) {
        $arguments.Add("+map")
        $arguments.Add($Map)
    }
    else {
        # A valid built-in bootstrap map is required while CS2 downloads/checks
        # the Workshop item. host_workshop_map changes to it when ready.
        $arguments.Add("+map")
        $arguments.Add("de_dust2")
        $arguments.Add("+host_workshop_map")
        $arguments.Add($WorkshopId)
    }

    $process = Start-Process `
        -FilePath $serverExe `
        -ArgumentList $arguments `
        -WorkingDirectory $serverRoot `
        -WindowStyle Hidden `
        -PassThru

    Write-Host "Local CS2 server started (PID $($process.Id))."
    Write-Host "Connect with: connect 127.0.0.1:$Port"
    Write-Host "Requested map: $Map$(if ($WorkshopId) { " (Workshop $WorkshopId)" })"
    Write-Host "SwiftlyS2 logs: C:\CS2Server\server\game\csgo\addons\swiftlys2\logs"
}
finally {
    Pop-Location
}
