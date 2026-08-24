[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$serverRoot = "C:\CS2Server\server"
$sourceSwiftly = Join-Path $serverRoot "game\csgo\addons\swiftlys2"
$surf3Swiftly = Join-Path $serverRoot "game\csgo\addons\swiftlys2-surf3"
$workspace = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$surf3Config = Join-Path $workspace "deploy\servers\surf-3\config.jsonc"

if (-not (Test-Path -LiteralPath $sourceSwiftly)) {
    throw "The primary SwiftlyS2 installation was not found at $sourceSwiftly."
}

New-Item -ItemType Directory -Force -Path $surf3Swiftly | Out-Null
Copy-Item -Path (Join-Path $sourceSwiftly "*") -Destination $surf3Swiftly -Recurse -Force

$pluginConfigDirectory = Join-Path $surf3Swiftly "configs\plugins\surf_timer"
New-Item -ItemType Directory -Force -Path $pluginConfigDirectory | Out-Null
Copy-Item -LiteralPath $surf3Config -Destination (Join-Path $pluginConfigDirectory "config.jsonc") -Force

Write-Host "surf-3 SwiftlyS2 instance initialized at $surf3Swiftly."
Write-Host "It shares the CS2 binaries and MariaDB database, but has isolated plugins, configuration, and logs."
