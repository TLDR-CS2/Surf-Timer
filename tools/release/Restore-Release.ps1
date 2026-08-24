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

$serverExe = "C:\CS2Server\server\game\bin\win64\cs2.exe"
$running = Get-Process cs2 -ErrorAction SilentlyContinue | Where-Object Path -eq $serverExe
if ($running) { throw "Stop all local CS2 server instances before rollback. Running PID(s): $($running.Id -join ', ')." }

$safetyRoot = Join-Path $deploymentRoot ("pre-rollback-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
New-Item -ItemType Directory -Force -Path $safetyRoot | Out-Null
foreach ($swiftlyRootValue in $SwiftlyRoots) {
    $swiftlyRoot = [System.IO.Path]::GetFullPath($swiftlyRootValue)
    $pluginDirectory = [System.IO.Path]::GetFullPath((Join-Path $swiftlyRoot "plugins\SurfTimer"))
    $expectedParent = [System.IO.Path]::GetFullPath((Join-Path $swiftlyRoot "plugins"))
    if ([System.IO.Path]::GetDirectoryName($pluginDirectory) -ne $expectedParent) {
        throw "Resolved plugin directory escaped its expected parent: $pluginDirectory"
    }
    $instanceName = Split-Path -Leaf $swiftlyRoot
    $source = Join-Path $SnapshotPath $instanceName
    if (-not (Test-Path -LiteralPath (Join-Path $source "SurfTimer.dll"))) {
        throw "Snapshot payload is missing for $instanceName."
    }
    if (Test-Path -LiteralPath $pluginDirectory) {
        $safety = Join-Path $safetyRoot $instanceName
        New-Item -ItemType Directory -Force -Path $safety | Out-Null
        Copy-Item -Path (Join-Path $pluginDirectory "*") -Destination $safety -Recurse -Force
        Remove-Item -LiteralPath $pluginDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $pluginDirectory | Out-Null
    Copy-Item -Path (Join-Path $source "*") -Destination $pluginDirectory -Recurse -Force
    Write-Host "Restored $instanceName from $source"
}

Write-Host "Rollback completed. Pre-rollback safety snapshot: $safetyRoot"
Write-Host "Plugin files, including the snapshotted per-server configuration, were restored."
