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

$serverExe = "C:\CS2Server\server\game\bin\win64\cs2.exe"
$running = Get-Process cs2 -ErrorAction SilentlyContinue | Where-Object Path -eq $serverExe
if ($running) { throw "Stop all local CS2 server instances before upgrading. Running PID(s): $($running.Id -join ', ')." }

$temporary = Join-Path ([System.IO.Path]::GetTempPath()) ("surftimer-release-" + [guid]::NewGuid().ToString("N"))
$rollbackRoot = Join-Path $workspace ("backups\deployments\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
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
    New-Item -ItemType Directory -Force -Path $rollbackRoot | Out-Null
    foreach ($swiftlyRootValue in $SwiftlyRoots) {
        $swiftlyRoot = [System.IO.Path]::GetFullPath($swiftlyRootValue)
        if (-not (Test-Path -LiteralPath $swiftlyRoot)) { throw "Swiftly root does not exist: $swiftlyRoot" }
        $pluginDirectory = Join-Path $swiftlyRoot "plugins\SurfTimer"
        $instanceName = Split-Path -Leaf $swiftlyRoot
        $snapshot = Join-Path $rollbackRoot $instanceName
        if (Test-Path -LiteralPath $pluginDirectory) {
            New-Item -ItemType Directory -Force -Path $snapshot | Out-Null
            Copy-Item -Path (Join-Path $pluginDirectory "*") -Destination $snapshot -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path $pluginDirectory | Out-Null
        Copy-Item -Path (Join-Path $payload "*") -Destination $pluginDirectory -Recurse -Force
        Write-Host "Installed SurfTimer $($manifest.version) to $pluginDirectory"
    }
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $rollbackRoot "installed-release-manifest.json")
    Write-Host "Rollback snapshot: $rollbackRoot"
    Write-Host "Effective SwiftlyS2 configs and data directories are outside the plugin payload and were not modified."
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
