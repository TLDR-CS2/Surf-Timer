[CmdletBinding()]
param(
    [string]$DestinationDirectory = ""
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "DatabaseTools.Common.ps1")
$context = Get-SurfTimerDatabaseToolContext

if ([string]::IsNullOrWhiteSpace($DestinationDirectory)) {
    $DestinationDirectory = Join-Path $context.Workspace "backups\mariadb"
}
$DestinationDirectory = [System.IO.Path]::GetFullPath($DestinationDirectory)
New-Item -ItemType Directory -Force -Path $DestinationDirectory | Out-Null

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backupPath = Join-Path $DestinationDirectory "surftimer-$timestamp.sql"
$defaultsFile = New-SurfTimerClientDefaultsFile -Connection $context.Connection
try {
    & $context.DumpExe `
        "--defaults-extra-file=$defaultsFile" `
        "--single-transaction" `
        "--quick" `
        "--skip-lock-tables" `
        "--triggers" `
        "--hex-blob" `
        "--databases" $context.Connection.database `
        "--result-file=$backupPath"
    if ($LASTEXITCODE -ne 0) {
        Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
        throw "mariadb-dump failed with exit code $LASTEXITCODE. Incomplete output was removed."
    }
}
finally {
    Remove-Item -LiteralPath $defaultsFile -Force -ErrorAction SilentlyContinue
}

$file = Get-Item -LiteralPath $backupPath
if ($file.Length -eq 0) { throw "Backup output is empty: $backupPath" }
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $backupPath).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($backupPath + ".sha256") -Value "$hash  $($file.Name)" -Encoding ASCII
& (Join-Path $PSScriptRoot "Test-DatabaseBackup.ps1") -BackupPath $backupPath
if ($LASTEXITCODE -ne 0) { throw "Backup validation failed." }

Write-Host "SurfTimer database backup completed."
Write-Host "Backup: $backupPath"
Write-Host "Size: $($file.Length) bytes"
Write-Host "SHA-256: $hash"
