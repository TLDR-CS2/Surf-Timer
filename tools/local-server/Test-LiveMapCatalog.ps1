[CmdletBinding()]
param(
    [string[]]$Maps = @(),
    [int]$TimeoutSeconds = 45,
    [switch]$DownloadMissing,
    [switch]$SkipDeploy,
    [switch]$SkipStaticScan
)

$ErrorActionPreference = "Stop"
$workspace = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$mapConfigRoot = Join-Path $workspace "resources\configs\maps"
$cfgRoot = Join-Path $PSScriptRoot "maps"
$workshopRoot = "C:\CS2Server\server\game\bin\win64\steamapps\workshop\content\730"
$steamCmd = "C:\CS2Server\steamcmd\steamcmd.exe"
$consoleLogRoot = "C:\CS2Server\server\game\csgo\addons\swiftlys2\logs\console"
$startServer = Join-Path $PSScriptRoot "Start-LocalServer.ps1"
$stopServer = Join-Path $PSScriptRoot "Stop-LocalServer.ps1"
$deployPlugin = Join-Path $PSScriptRoot "Deploy-Plugin.ps1"
$staticScan = Join-Path $PSScriptRoot "Test-DownloadedMapTriggers.ps1"
$failures = [System.Collections.Generic.List[string]]::new()
$results = [System.Collections.Generic.List[object]]::new()

if ($Maps.Count -eq 0) {
    $Maps = @(Get-ChildItem -File -LiteralPath $mapConfigRoot -Filter "surf_*.json" |
        Sort-Object BaseName | ForEach-Object BaseName)
}

$catalog = foreach ($map in $Maps) {
    $configPath = Join-Path $mapConfigRoot ($map + ".json")
    $cfgPath = Join-Path $cfgRoot ($map + ".cfg")
    if (-not (Test-Path -LiteralPath $configPath)) { throw "Map configuration is missing: $configPath" }
    if (-not (Test-Path -LiteralPath $cfgPath)) { throw "Local server configuration is missing: $cfgPath" }
    $firstLine = Get-Content -LiteralPath $cfgPath -TotalCount 1
    if ($firstLine -notmatch 'Workshop\s+(\d+)') { throw "$map has no Workshop ID in $cfgPath." }
    [pscustomobject]@{ Map = $map; WorkshopId = $Matches[1] }
}

try {
    & $stopServer
    if (-not $SkipDeploy) { & $deployPlugin }

    foreach ($item in $catalog) {
        $downloadDirectory = Join-Path $workshopRoot $item.WorkshopId
        $hasVpk = @(Get-ChildItem -File -LiteralPath $downloadDirectory -Filter "*.vpk" -ErrorAction SilentlyContinue).Count -gt 0
        if (-not $hasVpk -and $DownloadMissing) {
            if (-not (Test-Path -LiteralPath $steamCmd)) { throw "SteamCMD was not found at $steamCmd." }
            Write-Host "Downloading $($item.Map) (Workshop $($item.WorkshopId))..."
            & $steamCmd +force_install_dir "C:\CS2Server\server" +login anonymous +workshop_download_item 730 $item.WorkshopId validate +quit
            if ($LASTEXITCODE -ne 0) { $failures.Add("$($item.Map): SteamCMD exited with $LASTEXITCODE"); continue }
        }
        elseif (-not $hasVpk) {
            $failures.Add("$($item.Map): Workshop content is absent; rerun with -DownloadMissing")
            continue
        }

        if (-not $SkipStaticScan) {
            try { & $staticScan -Maps $item.Map }
            catch { $failures.Add("$($item.Map): static trigger scan failed: $($_.Exception.Message)"); continue }
        }

        Write-Host "Boot-certifying $($item.Map) (Workshop $($item.WorkshopId))..."
        $startedAt = Get-Date
        & $startServer -Map $item.Map -WorkshopId $item.WorkshopId
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        $certified = $false
        $rendered = $false
        $runtimeFailure = $null
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
            $log = Get-ChildItem -File -LiteralPath $consoleLogRoot -ErrorAction SilentlyContinue |
                Where-Object LastWriteTime -ge $startedAt.AddSeconds(-2) |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($null -eq $log) { continue }
            $content = Get-Content -Raw -LiteralPath $log.FullName
            $certified = $content -match [regex]::Escape("Map compatibility certified for $($item.Map):")
            $rendered = $content -match ([regex]::Escape("Rendered finish-zone box for $($item.Map)") + ".*12 beams")
            if ($content -match [regex]::Escape("Map compatibility failed for $($item.Map):")) {
                $runtimeFailure = "runtime compatibility failed"
                break
            }
            if ($certified -and $rendered) { break }
        }
        & $stopServer
        if (-not $certified -or -not $rendered) {
            $detail = if ($runtimeFailure) { $runtimeFailure } else { "timed out after $TimeoutSeconds seconds" }
            $failures.Add("$($item.Map): $detail (compatibility=$certified, finishRenderer=$rendered)")
        }
        else {
            Write-Host "$($item.Map): LIVE CERTIFIED (compatibility + 12-beam finish zone)."
        }
        $results.Add([pscustomobject]@{ Map = $item.Map; Compatibility = $certified; FinishZone12Beams = $rendered })
    }
}
finally {
    & $stopServer
}

$results | Format-Table -AutoSize
if ($failures.Count -gt 0) { throw "Live map certification failed:`n$($failures -join "`n")" }
Write-Host "LIVE MAP CATALOG CERTIFICATION PASSED ($($catalog.Count) maps)"
