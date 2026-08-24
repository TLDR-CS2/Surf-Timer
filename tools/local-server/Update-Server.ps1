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
