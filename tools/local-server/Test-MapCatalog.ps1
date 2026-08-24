[CmdletBinding()]
param(
    [string[]]$Maps = @(
        "surf_aquaflow", "surf_newbie", "surf_zeitgeist", "surf_jive",
        "surf_cannonball", "surf_sippysip", "surf_lt_omnific"
    ),
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = "Stop"
$start = Join-Path $PSScriptRoot "Start-LocalServer.ps1"
$stop = Join-Path $PSScriptRoot "Stop-LocalServer.ps1"
$logRoot = "C:\CS2Server\server\game\csgo\addons\swiftlys2\logs\managed"
$results = [System.Collections.Generic.List[object]]::new()

& $stop
foreach ($map in $Maps) {
    Write-Host "Certifying $map..."
    & $start -Map $map
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $outcome = $null
    $detail = "timed out waiting for compatibility report"
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
        $logs = @(Get-ChildItem -File -LiteralPath $logRoot -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 3)
        foreach ($log in $logs) {
            $matches = @(Select-String -LiteralPath $log.FullName -Pattern "Map compatibility (certified|failed) for $([regex]::Escape($map))" -ErrorAction SilentlyContinue)
            if ($matches.Count -eq 0) { continue }
            $line = $matches[-1].Line
            $outcome = if ($line -match "compatibility certified") { "certified" } else { "failed" }
            $detail = $line
            break
        }
        if ($outcome) { break }
    }
    & $stop
    $results.Add([pscustomobject]@{ Map = $map; Result = $outcome ?? "timeout"; Detail = $detail })
}

$results | Format-Table Map, Result -AutoSize
$reportPath = Join-Path (Join-Path $PSScriptRoot ".runtime") "map-catalog-report.json"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $reportPath) | Out-Null
$results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $reportPath -Encoding UTF8
Write-Host "Detailed report: $reportPath"

if ($results.Result -contains "failed" -or $results.Result -contains "timeout") {
    throw "One or more maps could not be certified. Review the detailed report."
}
Write-Host "MAP CATALOG CERTIFICATION PASSED"
