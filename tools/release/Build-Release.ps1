[CmdletBinding()]
param([string]$DotNetPath)

$ErrorActionPreference = "Stop"
$workspace = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dotnet = & (Join-Path $workspace "tools\Resolve-DotNet.ps1") -DotNetPath $DotNetPath
$buildInfo = Get-Content -Raw -LiteralPath (Join-Path $workspace "src\BuildInfo.cs")
$match = [regex]::Match($buildInfo, 'Version\s*=\s*"([^"]+)"')
if (-not $match.Success) { throw "Could not read BuildInfo.Version." }
$version = $match.Groups[1].Value
$migrationVersions = Get-ChildItem -File -LiteralPath (Join-Path $workspace "resources\migrations\mysql") -Filter "*.sql" |
    ForEach-Object {
        $migrationMatch = [regex]::Match($_.BaseName, '^(\d+)_')
        if (-not $migrationMatch.Success) { throw "Migration filename does not begin with a numeric version: $($_.Name)" }
        [int]$migrationMatch.Groups[1].Value
    }
if (-not $migrationVersions) { throw "No database migrations were found." }
$databaseSchemaVersion = ($migrationVersions | Measure-Object -Maximum).Maximum
$artifactRoot = Join-Path $workspace "artifacts"
$stagingRoot = Join-Path $artifactRoot "staging-$([guid]::NewGuid().ToString('N'))"
$packageRoot = Join-Path $stagingRoot "SurfTimer"
$publishRoot = Join-Path $stagingRoot "publish"
$packagePath = Join-Path $artifactRoot "SurfTimer-$version.zip"

try {
    Push-Location $workspace
    try {
        & $dotnet publish (Join-Path $workspace "SurfTimer.csproj") -c Release -o $publishRoot
        if ($LASTEXITCODE -ne 0) { throw "Release publish failed with exit code $LASTEXITCODE." }
    }
    finally { Pop-Location }

    New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
    Copy-Item -Path (Join-Path $publishRoot "*") -Destination $packageRoot -Recurse -Force
    Remove-Item -LiteralPath (Join-Path $packageRoot "SurfTimer.pdb") -Force -ErrorAction SilentlyContinue
    $manifest = [ordered]@{
        name = "SurfTimer"
        version = $version
        databaseSchemaVersion = $databaseSchemaVersion
        targetFramework = "net10.0"
        swiftlyS2 = "1.4.5 or compatible newer"
        database = "MariaDB/MySQL through SwiftlyS2"
        requiredPlugins = @("BotController shared API ABI 18")
        builtAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stagingRoot "release-manifest.json") -Encoding UTF8
    New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
    Compress-Archive -Path $packageRoot, (Join-Path $stagingRoot "release-manifest.json") -DestinationPath $packagePath -CompressionLevel Optimal -Force
}
finally {
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($stagingRoot)) -ne [IO.Path]::GetFullPath($artifactRoot)) { throw "Invalid staging cleanup path." }
    if (Test-Path -LiteralPath $stagingRoot) { Remove-Item -LiteralPath $stagingRoot -Recurse -Force }
}

$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $packagePath).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($packagePath + ".sha256") -Value "$hash  $([System.IO.Path]::GetFileName($packagePath))" -Encoding ASCII
& (Join-Path $PSScriptRoot "Test-ReleasePackage.ps1") -PackagePath $packagePath
if ($LASTEXITCODE -ne 0) { throw "Release package validation failed." }
Write-Host "Release package: $packagePath"
Write-Host "SHA-256: $hash"



