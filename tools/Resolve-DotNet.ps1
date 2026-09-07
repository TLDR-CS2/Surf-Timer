[CmdletBinding()]
param([string]$DotNetPath)
$ErrorActionPreference = "Stop"
if ($DotNetPath) {
    $resolved = Get-Command $DotNetPath -CommandType Application -ErrorAction Stop
    return $resolved.Source
}
$installed = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
if ($installed) { return $installed.Source }
$bundled = Join-Path (Split-Path -Parent $PSScriptRoot) ".research\.dotnet\dotnet.exe"
if (Test-Path -LiteralPath $bundled -PathType Leaf) { return $bundled }
throw "Install the .NET 10 SDK or supply -DotNetPath with a dotnet executable."
