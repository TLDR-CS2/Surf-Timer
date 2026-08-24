[CmdletBinding()]
param(
    [string]$DestinationDirectory = "",
    [ValidateRange(2, 365)]
    [int]$KeepLatest = 14
)

$ErrorActionPreference = "Stop"
$workspace = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($DestinationDirectory)) {
    $DestinationDirectory = Join-Path $workspace "backups\mariadb"
}
$backupRoot = [System.IO.Path]::GetFullPath($DestinationDirectory)
$logRoot = Join-Path $backupRoot "logs"
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
$logPath = Join-Path $logRoot ("backup-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".log")

function Write-BackupLog([string]$Message) {
    $line = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $Message"
    $line | Tee-Object -FilePath $logPath -Append
}

try {
    Write-BackupLog "Starting SurfTimer MariaDB backup."
    $before = @(Get-ChildItem -File -LiteralPath $backupRoot -Filter "surftimer-*.sql" -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty FullName)
    & (Join-Path $PSScriptRoot "Backup-Database.ps1") -DestinationDirectory $backupRoot *>&1 |
        ForEach-Object { Write-BackupLog $_.ToString() }
    $after = @(Get-ChildItem -File -LiteralPath $backupRoot -Filter "surftimer-*.sql" |
        Sort-Object LastWriteTime -Descending)
    $newBackup = $after | Where-Object FullName -notin $before | Select-Object -First 1
    if ($null -eq $newBackup) { throw "Backup command did not produce a new .sql file." }
    if (-not (Test-Path -LiteralPath ($newBackup.FullName + ".sha256"))) {
        throw "New backup is missing its SHA-256 sidecar: $($newBackup.FullName)"
    }

    Write-BackupLog "Running post-backup consistency audit."
    & (Join-Path $PSScriptRoot "Test-DatabaseConsistency.ps1") *>&1 |
        ForEach-Object { Write-BackupLog $_.ToString() }

    $retained = @($after | Select-Object -First $KeepLatest)
    $expired = @($after | Select-Object -Skip $KeepLatest)
    foreach ($file in $expired) {
        $resolved = [System.IO.Path]::GetFullPath($file.FullName)
        if (-not $resolved.StartsWith($backupRoot + [System.IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove backup outside ${backupRoot}: $resolved"
        }
        if ($retained.Count -eq 0 -or $resolved -eq $retained[0].FullName) {
            throw "Refusing to remove the newest valid backup."
        }
        Remove-Item -LiteralPath $resolved -Force
        Remove-Item -LiteralPath ($resolved + ".sha256") -Force -ErrorAction SilentlyContinue
        Write-BackupLog "Removed expired backup $($file.Name)."
    }
    Write-BackupLog "Backup job succeeded; retained $($retained.Count) backup(s), policy=$KeepLatest."
}
catch {
    Write-BackupLog "BACKUP JOB FAILED: $($_.Exception.Message)"
    exit 1
}
