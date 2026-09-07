[CmdletBinding()]
param(
    [string]$DotNetPath,
    [string]$PackagesPath,
    [string]$NodePath = 'node',
    [switch]$IncludeLiveDatabase,
    [switch]$IncludeDownloadedMaps,
    [switch]$IncludeReleasePackage
)

$ErrorActionPreference = "Stop"
$workspace = Split-Path -Parent $PSScriptRoot
$dotnet = & (Join-Path $PSScriptRoot "Resolve-DotNet.ps1") -DotNetPath $DotNetPath
Push-Location $workspace
try {
    foreach ($project in @('SurfTimer.csproj', 'web/SurfTimer.Web.csproj',
        'tools/lifecycle-tests/LifecycleTests.csproj', 'tools/replay-tests/ReplayTests.csproj', 'tools/catalog-tests/CatalogTests.csproj')) {
        $restoreArgs = @('restore', $project, '--ignore-failed-sources')
        if ($PackagesPath) { $restoreArgs += @('--packages', $PackagesPath) }
        & $dotnet @restoreArgs
        if ($LASTEXITCODE -ne 0) { throw "$project restore failed." }
    }
    & $dotnet build -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Plugin build failed." }
    foreach ($project in @(
        "tools/lifecycle-tests/LifecycleTests.csproj",
        "tools/replay-tests/ReplayTests.csproj",
        "tools/catalog-tests/CatalogTests.csproj"
    )) {
        & $dotnet run --project $project -c Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw "$project failed." }
    }
    & $dotnet build web/SurfTimer.Web.csproj -c Release --no-restore -o build/web-verify
    if ($LASTEXITCODE -ne 0) { throw "Website build failed." }
    & $NodePath (Join-Path $PSScriptRoot 'web-api\Test-Frontend.cjs')
    if ($LASTEXITCODE -ne 0) { throw 'Website frontend regression checks failed.' }
    & (Join-Path $PSScriptRoot 'release\Test-PayloadSwap.ps1')
    if ($IncludeDownloadedMaps) { & ".\tools\local-server\Test-DownloadedMapTriggers.ps1" }
    if ($IncludeReleasePackage) {
        $buildInfo = Get-Content -Raw -LiteralPath ".\src\BuildInfo.cs"
        $version = [regex]::Match($buildInfo, 'Version\s*=\s*"([^"]+)"').Groups[1].Value
        & ".\tools\release\Test-ReleasePackage.ps1" -PackagePath ".\artifacts\SurfTimer-$version.zip"
    }
    if ($IncludeLiveDatabase) {
        $restoreArgs = @('restore', 'tools/storage-tests/StorageTests.csproj', '--ignore-failed-sources')
        if ($PackagesPath) { $restoreArgs += @('--packages', $PackagesPath) }
        & $dotnet @restoreArgs
        if ($LASTEXITCODE -ne 0) { throw 'Storage integration harness restore failed.' }
        & $dotnet run --project tools/storage-tests/StorageTests.csproj -c Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Storage integration checks failed.' }
        & ".\tools\local-server\Test-DatabaseConsistency.ps1"
        & ".\tools\web-api\Test-WebApi.ps1"
    }
    Write-Host "ALL SELECTED SURFTIMER CHECKS PASSED"
}
finally { Pop-Location }





