[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackagePath,
    [string[]]$SwiftlyRoots = @(
        "C:\CS2Server\server\game\csgo\addons\swiftlys2",
        "C:\CS2Server\server\game\csgo\addons\swiftlys2-surf3"
    )
)

$ErrorActionPreference = "Stop"
$workspace = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$PackagePath = [System.IO.Path]::GetFullPath($PackagePath)
if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf) -or [System.IO.Path]::GetExtension($PackagePath) -ne ".zip") {
    throw "Release package must be an existing .zip file: $PackagePath"
}
$hashPath = $PackagePath + ".sha256"
if (-not (Test-Path -LiteralPath $hashPath)) { throw "Release checksum is missing: $hashPath" }
$expected = ((Get-Content -LiteralPath $hashPath -First 1) -split '\s+')[0].ToLowerInvariant()
$actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $PackagePath).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw "Release SHA-256 verification failed." }

$serverExecutables = @($SwiftlyRoots | ForEach-Object {
    [IO.Path]::GetFullPath((Join-Path $_ '..\..\..\bin\win64\cs2.exe'))
})
$running = Get-Process cs2 -ErrorAction SilentlyContinue | Where-Object { $_.Path -in $serverExecutables }
if ($running) { throw "Stop all local CS2 server instances before upgrading. Running PID(s): $($running.Id -join ', ')." }

$temporary = Join-Path ([System.IO.Path]::GetTempPath()) ("surftimer-release-" + [guid]::NewGuid().ToString("N"))
$rollbackRoot = Join-Path $workspace ("backups\deployments\" + (Get-Date -Format "yyyyMMdd-HHmmss") + "-" + [guid]::NewGuid().ToString("N"))
try {
    Expand-Archive -LiteralPath $PackagePath -DestinationPath $temporary
    $manifestPath = Join-Path $temporary "release-manifest.json"
    $payload = Join-Path $temporary "SurfTimer"
    if (-not (Test-Path -LiteralPath $manifestPath) -or -not (Test-Path -LiteralPath (Join-Path $payload "SurfTimer.dll"))) {
        throw "Release package is missing its manifest or SurfTimer payload."
    }
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    if (-not $manifest.version -or -not $manifest.databaseSchemaVersion) {
        throw "Release manifest is missing version or databaseSchemaVersion."
    }
    $migrationFiles = @(Get-ChildItem -File -LiteralPath (Join-Path $payload "resources\migrations\mysql") -Filter "*.sql")
    $packagedSchemaVersion = ($migrationFiles | ForEach-Object {
        if ($_.BaseName -notmatch '^(\d+)_') { throw "Invalid packaged migration filename: $($_.Name)" }
        [int]$Matches[1]
    } | Measure-Object -Maximum).Maximum
    if ([int]$manifest.databaseSchemaVersion -ne $packagedSchemaVersion) {
        throw "Release manifest schema version does not match packaged migrations."
    }
    . (Join-Path $PSScriptRoot 'PluginPayloadSwap.ps1')
    $targets = @($SwiftlyRoots | ForEach-Object { [pscustomobject]@{ Root = $_; Source = $payload } })
    Invoke-PluginPayloadSwap -Targets $targets -SnapshotRoot $rollbackRoot
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $rollbackRoot "installed-release-manifest.json")
    Write-Host "Rollback snapshot: $rollbackRoot"
    Write-Host "Effective SwiftlyS2 configs and data directories are outside the plugin payload and were not modified."
}
finally {
    $tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($temporary)) -ne $tempParent) { throw "Invalid temporary cleanup path." }
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}


