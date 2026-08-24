[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$databaseRoot = "C:\CS2Server\mariadb"
$distribution = Get-ChildItem -Directory -LiteralPath $databaseRoot -Filter "mariadb-*-winx64" |
    Sort-Object Name -Descending |
    Select-Object -First 1

if ($null -eq $distribution) {
    throw "Portable MariaDB was not found under $databaseRoot."
}

$serverExe = Join-Path $distribution.FullName "bin\mariadbd.exe"
$dataDirectory = Join-Path $databaseRoot "data"
$configuration = Join-Path $dataDirectory "my.ini"
$existing = Get-Process mariadbd -ErrorAction SilentlyContinue |
    Where-Object Path -eq $serverExe

if ($existing) {
    Write-Host "Local MariaDB is already running (PID $($existing.Id -join ', '))."
    return
}

$arguments = @(
    "--defaults-file=`"$configuration`""
    "--bind-address=127.0.0.1"
    "--port=3306"
    "--console"
)

$process = Start-Process `
    -FilePath $serverExe `
    -ArgumentList $arguments `
    -WorkingDirectory $dataDirectory `
    -WindowStyle Hidden `
    -PassThru

for ($attempt = 0; $attempt -lt 30; $attempt++) {
    if ($process.HasExited) {
        throw "MariaDB exited during startup with code $($process.ExitCode)."
    }

    if (Get-NetTCPConnection -State Listen -LocalPort 3306 -ErrorAction SilentlyContinue) {
        Write-Host "Local MariaDB started (PID $($process.Id), 127.0.0.1:3306)."
        return
    }

    Start-Sleep -Milliseconds 250
}

throw "MariaDB did not begin listening on port 3306 within the startup timeout."
