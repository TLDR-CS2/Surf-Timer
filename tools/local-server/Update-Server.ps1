[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$steamCmd = "C:\CS2Server\steamcmd\steamcmd.exe"
$serverRoot = "C:\CS2Server\server"

if (-not (Test-Path -LiteralPath $steamCmd)) {
    throw "SteamCMD was not found at $steamCmd."
}

& $steamCmd `
    +force_install_dir $serverRoot `
    +login anonymous `
    +app_update 730 validate `
    +quit

if ($LASTEXITCODE -ne 0) {
    throw "SteamCMD exited with code $LASTEXITCODE."
}

# Steam validation replaces gameinfo.gi, so restore both native plugin loaders
# before the updated server is started again.
$gameInfo = Join-Path $serverRoot "game\csgo\gameinfo.gi"
& (Join-Path $PSScriptRoot 'Repair-PluginLoadPaths.ps1') -GameInfoPath $gameInfo

# Metamod loads addons/BotController/bin/win64/BotController.dll before the
# managed bridge resolves it. Do not copy a second uninitialized DLL beside cs2.exe.
