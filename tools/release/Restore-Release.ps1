[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SnapshotPath,
    [switch]$ConfirmRollback,
    [string[]]$SwiftlyRoots = @(
        "C:\CS2Server\server\game\csgo\addons\swiftlys2",
        "C:\CS2Server\server\game\csgo\addons\swiftlys2-surf3"
    )
)

$ErrorActionPreference = "Stop"
$workspace = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$deploymentRoot = [System.IO.Path]::GetFullPath((Join-Path $workspace "backups\deployments"))
$SnapshotPath = [System.IO.Path]::GetFullPath($SnapshotPath)
if (-not $ConfirmRollback) { throw "Rollback replaces plugin binaries. Re-run with -ConfirmRollback." }
if (-not $SnapshotPath.StartsWith($deploymentRoot + [System.IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $SnapshotPath -PathType Container)) {
    throw "Snapshot must be an existing child of ${deploymentRoot}: $SnapshotPath"
}

$serverExecutables = @($SwiftlyRoots | ForEach-Object {
    [IO.Path]::GetFullPath((Join-Path $_ '..\..\..\bin\win64\cs2.exe'))
})
$running = Get-Process cs2 -ErrorAction SilentlyContinue | Where-Object { $_.Path -in $serverExecutables }
if ($running) { throw "Stop all local CS2 server instances before rollback. Running PID(s): $($running.Id -join ', ')." }

$safetyRoot = Join-Path $deploymentRoot ("pre-rollback-" + (Get-Date -Format "yyyyMMdd-HHmmss") + "-" + [guid]::NewGuid().ToString("N"))
. (Join-Path $PSScriptRoot 'PluginPayloadSwap.ps1')
$targetManifest = Join-Path $SnapshotPath 'deployment-targets.json'
$recordedTargets = if (Test-Path -LiteralPath $targetManifest) { @(Get-Content -Raw -LiteralPath $targetManifest | ConvertFrom-Json) } else { @() }
$targets = @()
$legacyNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($swiftlyRootValue in $SwiftlyRoots) {
    $swiftlyRoot = [IO.Path]::GetFullPath($swiftlyRootValue).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ($recordedTargets.Count -gt 0) {
        $matchesForRoot = @($recordedTargets | Where-Object Root -eq $swiftlyRoot)
        if ($matchesForRoot.Count -ne 1 -or -not $matchesForRoot[0].Existed) { throw "No previous plugin payload was recorded for $swiftlyRoot." }
        $source = [IO.Path]::GetFullPath((Join-Path $SnapshotPath $matchesForRoot[0].Snapshot))
        if ([IO.Path]::GetDirectoryName($source) -ne $SnapshotPath.TrimEnd([IO.Path]::DirectorySeparatorChar)) { throw "Snapshot payload escaped its snapshot directory." }
    }
    else {
        $instanceName = Split-Path -Leaf $swiftlyRoot
        if (-not $legacyNames.Add($instanceName)) { throw "Legacy snapshot names collide; select one instance at a time." }
        $source = Join-Path $SnapshotPath $instanceName
    }
    $targets += [pscustomobject]@{ Root = $swiftlyRoot; Source = $source }
}
Invoke-PluginPayloadSwap -Targets $targets -SnapshotRoot $safetyRoot
Write-Host "Rollback completed. Pre-rollback safety snapshot: $safetyRoot"
Write-Host "Effective SwiftlyS2 configs and data outside the plugin payload were preserved."

