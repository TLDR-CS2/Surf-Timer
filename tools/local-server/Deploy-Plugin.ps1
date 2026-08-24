[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$workspace = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dotnet = Join-Path $workspace ".research\.dotnet\dotnet.exe"
$publishDirectory = Join-Path $workspace "build\publish\SurfTimer"
$pluginDirectories = @(
    "C:\CS2Server\server\game\csgo\addons\swiftlys2\plugins\SurfTimer",
    "C:\CS2Server\server\game\csgo\addons\swiftlys2-surf3\plugins\SurfTimer"
)

if (-not (Test-Path -LiteralPath $dotnet)) {
    throw "The workspace-local .NET SDK was not found at $dotnet."
}

Push-Location $workspace
try {
    & $dotnet publish -c Release
    if ($LASTEXITCODE -ne 0) {
        throw "SurfTimer publish failed with code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

foreach ($pluginDirectory in $pluginDirectories) {
    $swiftlyRoot = Split-Path -Parent (Split-Path -Parent $pluginDirectory)
    if ($pluginDirectory -like "*swiftlys2-surf3*" -and -not (Test-Path -LiteralPath $swiftlyRoot)) {
        continue
    }

    New-Item -ItemType Directory -Force -Path $pluginDirectory | Out-Null
    Copy-Item -Path (Join-Path $publishDirectory "*") -Destination $pluginDirectory -Recurse -Force
    Write-Host "SurfTimer deployed to $pluginDirectory"
}
