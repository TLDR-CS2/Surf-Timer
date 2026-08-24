[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BackupPath
)

$ErrorActionPreference = "Stop"
$BackupPath = [System.IO.Path]::GetFullPath($BackupPath)
if (-not (Test-Path -LiteralPath $BackupPath -PathType Leaf) -or [System.IO.Path]::GetExtension($BackupPath) -ne ".sql") {
    throw "Backup must be an existing .sql file: $BackupPath"
}
$file = Get-Item -LiteralPath $BackupPath
if ($file.Length -lt 1024) { throw "Backup is unexpectedly small ($($file.Length) bytes)." }

$hashPath = $BackupPath + ".sha256"
if (-not (Test-Path -LiteralPath $hashPath -PathType Leaf)) { throw "Backup checksum is missing: $hashPath" }
$expected = ((Get-Content -LiteralPath $hashPath -First 1) -split '\s+')[0].ToLowerInvariant()
$actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $BackupPath).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw "Backup SHA-256 verification failed." }

$requiredMarkers = @(
    'st_schema_migrations',
    'st_players',
    'st_maps',
    'st_records',
    'st_stage_records',
    'st_replays'
)
$text = [System.IO.File]::ReadAllText($BackupPath)
if ($text -notmatch '(?i)(MariaDB|MySQL) dump') { throw "File does not look like a MariaDB/MySQL dump." }
foreach ($marker in $requiredMarkers) {
    if ($text.IndexOf($marker, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Backup does not contain required schema marker: $marker"
    }
}

Write-Host "SurfTimer database backup validation passed: $($file.Name), $($file.Length) bytes, sha256=$actual"
