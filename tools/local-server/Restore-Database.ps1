[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BackupPath,
    [switch]$ConfirmRestore
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "DatabaseTools.Common.ps1")

if (-not $ConfirmRestore) {
    throw "Restore is destructive. Re-run with -ConfirmRestore after verifying the backup path."
}
$BackupPath = [System.IO.Path]::GetFullPath($BackupPath)
if (-not (Test-Path -LiteralPath $BackupPath -PathType Leaf) -or [System.IO.Path]::GetExtension($BackupPath) -ne ".sql") {
    throw "Backup must be an existing .sql file: $BackupPath"
}

$serverExe = "C:\CS2Server\server\game\bin\win64\cs2.exe"
$runningServers = Get-Process cs2 -ErrorAction SilentlyContinue | Where-Object Path -eq $serverExe
if ($runningServers) {
    throw "Stop every local CS2 server before restoring. Running PID(s): $($runningServers.Id -join ', ')."
}

$hashPath = $BackupPath + ".sha256"
if (-not (Test-Path -LiteralPath $hashPath -PathType Leaf)) { throw "Restore requires the backup checksum sidecar: $hashPath" }
& (Join-Path $PSScriptRoot "Test-DatabaseBackup.ps1") -BackupPath $BackupPath
if ($LASTEXITCODE -ne 0) { throw "Backup validation failed." }

$context = Get-SurfTimerDatabaseToolContext
$defaultsFile = New-SurfTimerClientDefaultsFile -Connection $context.Connection
try {
    $process = Start-Process -FilePath $context.ClientExe `
        -ArgumentList "--defaults-extra-file=$defaultsFile" `
        -RedirectStandardInput $BackupPath `
        -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "MariaDB restore failed with exit code $($process.ExitCode)." }
}
finally {
    Remove-Item -LiteralPath $defaultsFile -Force -ErrorAction SilentlyContinue
}

Write-Host "SurfTimer database restored from $BackupPath."
Write-Host "Run Test-DatabaseConsistency.ps1 before restarting the game servers."
