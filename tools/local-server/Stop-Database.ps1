[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$databaseRoot = "C:\CS2Server\mariadb"

$processes = Get-Process mariadbd -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($databaseRoot, [StringComparison]::OrdinalIgnoreCase) }

if (-not $processes) {
    Write-Host "Local MariaDB is not running."
    return
}

foreach ($process in $processes) {
    Stop-Process -Id $process.Id
    Wait-Process -Id $process.Id -ErrorAction SilentlyContinue
    Write-Host "Stopped local MariaDB PID $($process.Id)."
}
