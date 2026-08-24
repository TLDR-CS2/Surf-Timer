[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$pidPath = Join-Path (Join-Path $PSScriptRoot ".runtime") "surf-3.pid"

if (-not (Test-Path -LiteralPath $pidPath)) {
    Write-Host "surf-3 is not running (no PID file)."
    return
}

$serverPid = [int](Get-Content -LiteralPath $pidPath)
$process = Get-Process -Id $serverPid -ErrorAction SilentlyContinue
if ($process) {
    Stop-Process -Id $serverPid
    Write-Host "Stopped surf-3 PID $serverPid."
}
else {
    Write-Host "surf-3 PID $serverPid was no longer running."
}
Remove-Item -LiteralPath $pidPath -Force
