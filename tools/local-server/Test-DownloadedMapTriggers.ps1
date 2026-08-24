[CmdletBinding()]
param(
    [string[]]$Maps = @(
        "surf_aquaflow", "surf_newbie", "surf_zeitgeist", "surf_jive",
        "surf_cannonball", "surf_sippysip", "surf_lt_omnific"
    )
)

$ErrorActionPreference = "Stop"
$workspace = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$mapConfigs = Join-Path $workspace "resources\configs\maps"
$workshopRoot = "C:\CS2Server\server\game\bin\win64\steamapps\workshop\content\730"
$cfgRoot = Join-Path $PSScriptRoot "maps"
$failures = [System.Collections.Generic.List[string]]::new()

foreach ($map in $Maps) {
    $config = Get-Content -Raw -LiteralPath (Join-Path $mapConfigs ($map + ".json")) | ConvertFrom-Json
    $cfgFirstLine = Get-Content -LiteralPath (Join-Path $cfgRoot ($map + ".cfg")) -TotalCount 1
    if ($cfgFirstLine -notmatch 'Workshop\s+(\d+)') { throw "$map has no Workshop ID in its local cfg." }
    $workshopId = $Matches[1]
    $directory = Join-Path $workshopRoot $workshopId
    $vpks = @(Get-ChildItem -File -LiteralPath $directory -Filter "*.vpk" -ErrorAction SilentlyContinue)
    if ($vpks.Count -eq 0) { $failures.Add("${map}: Workshop VPK $workshopId is not downloaded"); continue }
    $text = [System.Text.StringBuilder]::new()
    foreach ($vpk in $vpks) {
        [void]$text.Append([System.Text.Encoding]::Latin1.GetString([System.IO.File]::ReadAllBytes($vpk.FullName)))
    }
    $vpkText = $text.ToString()
    $required = [System.Collections.Generic.List[string]]::new()
    $required.Add([string]$config.StartTrigger); $required.Add([string]$config.EndTrigger)
    for ($index = 1; $index -le [int]($config.CheckpointCount ?? 0); $index++) { $required.Add("$($config.CheckpointPrefix)$index") }
    for ($index = 2; $index -le [int]($config.StageCount ?? 0); $index++) { $required.Add("$($config.StagePrefix)${index}_start") }
    for ($index = 1; $index -le [int]($config.BonusCount ?? 0); $index++) {
        $required.Add("$($config.BonusPrefix)${index}_start"); $required.Add("$($config.BonusPrefix)${index}_end")
    }
    $missing = @($required | Where-Object { $vpkText.IndexOf($_, [StringComparison]::Ordinal) -lt 0 })
    if ($missing.Count -gt 0) { $failures.Add("${map}: missing $($missing -join ', ')") }
    else { Write-Host "${map}: VPK trigger metadata certified ($($required.Count) required names)." }
}

if ($failures.Count -gt 0) { throw "Downloaded map trigger certification failed:`n$($failures -join "`n")" }
Write-Host "DOWNLOADED MAP TRIGGER CERTIFICATION PASSED"
