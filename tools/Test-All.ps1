[CmdletBinding()]
param(
    [switch]$IncludeLiveDatabase,
    [switch]$IncludeDownloadedMaps,
    [switch]$IncludeReleasePackage
)

$ErrorActionPreference = "Stop"
$workspace = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $workspace ".research\.dotnet\dotnet.exe"
Push-Location $workspace
try {
    & $dotnet build -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Plugin build failed." }
    foreach ($project in @(
        "tools/lifecycle-tests/LifecycleTests.csproj",
        "tools/replay-tests/ReplayTests.csproj",
        "tools/catalog-tests/CatalogTests.csproj"
    )) {
        & $dotnet run --project $project -c Release
        if ($LASTEXITCODE -ne 0) { throw "$project failed." }
    }
    & $dotnet build web/SurfTimer.Web.csproj -c Release --no-restore -o build/web-verify
    if ($LASTEXITCODE -ne 0) { throw "Website build failed." }
    if ($IncludeDownloadedMaps) { & ".\tools\local-server\Test-DownloadedMapTriggers.ps1" }
    if ($IncludeReleasePackage) {
        $buildInfo = Get-Content -Raw -LiteralPath ".\src\BuildInfo.cs"
        $version = [regex]::Match($buildInfo, 'Version\s*=\s*"([^"]+)"').Groups[1].Value
        & ".\tools\release\Test-ReleasePackage.ps1" -PackagePath ".\artifacts\SurfTimer-$version.zip"
    }
    if ($IncludeLiveDatabase) {
        & ".\tools\local-server\Test-DatabaseConsistency.ps1"
        & ".\tools\web-api\Test-WebApi.ps1"
    }
    Write-Host "ALL SELECTED SURFTIMER CHECKS PASSED"
}
finally { Pop-Location }
