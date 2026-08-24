[CmdletBinding()]
param(
    [string]$SteamId = "76561198079156085"
)

$ErrorActionPreference = "Stop"
$permissionPath = "C:\CS2Server\server\game\csgo\addons\swiftlys2\configs\permissions.jsonc"
if (-not (Test-Path -LiteralPath $permissionPath)) {
    throw "SwiftlyS2 permissions file was not found at $permissionPath."
}

$configuration = Get-Content -Raw -LiteralPath $permissionPath | ConvertFrom-Json
$players = $configuration.Permissions.Players
$existing = $players.PSObject.Properties[$SteamId]
if ($null -eq $existing) {
    $players | Add-Member -NotePropertyName $SteamId -NotePropertyValue @("surftimer.admin")
}
elseif ($existing.Value -notcontains "surftimer.admin") {
    $existing.Value = @($existing.Value) + "surftimer.admin"
}

$configuration | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $permissionPath -Encoding utf8
Write-Host "Granted surftimer.admin to SteamID $SteamId in the local SwiftlyS2 permissions file."
