[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "DatabaseTools.Common.ps1")
$context = Get-SurfTimerDatabaseToolContext
$defaultsFile = New-SurfTimerClientDefaultsFile -Connection $context.Connection

$query = @"
SELECT 'schema_version' AS metric, COALESCE(MAX(version), 0) AS value FROM st_schema_migrations;
SELECT 'players' AS metric, COUNT(*) AS value FROM st_players;
SELECT 'maps' AS metric, COUNT(*) AS value FROM st_maps;
SELECT 'main_and_bonus_records' AS metric, COUNT(*) AS value FROM st_records;
SELECT 'stage_records' AS metric, COUNT(*) AS value FROM st_stage_records;
SELECT 'replays' AS metric, COUNT(*) AS value FROM st_replays;
SELECT 'stage_replays' AS metric, COUNT(*) AS value FROM st_stage_replays;
SELECT 'orphan_records' AS metric, COUNT(*) AS value FROM st_records r LEFT JOIN st_maps m ON m.id=r.map_id LEFT JOIN st_players p ON p.steam_id=r.player_steam_id WHERE m.id IS NULL OR p.steam_id IS NULL;
SELECT 'orphan_replays' AS metric, COUNT(*) AS value FROM st_replays rp LEFT JOIN st_records r ON r.id=rp.record_id WHERE r.id IS NULL;
SELECT 'orphan_splits' AS metric, COUNT(*) AS value FROM st_record_splits rs LEFT JOIN st_records r ON r.id=rs.record_id WHERE r.id IS NULL;
SELECT 'orphan_validation' AS metric, COUNT(*) AS value FROM st_run_validation v LEFT JOIN st_records r ON r.id=v.record_id WHERE r.id IS NULL;
SELECT 'orphan_player_stats' AS metric, COUNT(*) AS value FROM st_player_run_stats s LEFT JOIN st_players p ON p.steam_id=s.player_steam_id WHERE p.steam_id IS NULL;
SELECT 'orphan_pb_history' AS metric, COUNT(*) AS value FROM st_pb_history h LEFT JOIN st_records r ON r.id=h.record_id LEFT JOIN st_players p ON p.steam_id=h.player_steam_id LEFT JOIN st_maps m ON m.id=h.map_id WHERE r.id IS NULL OR p.steam_id IS NULL OR m.id IS NULL;
SELECT 'orphan_stage_replays' AS metric, COUNT(*) AS value FROM st_stage_replays rp LEFT JOIN st_stage_records sr ON sr.id=rp.stage_record_id WHERE sr.id IS NULL;
SELECT 'record_writer' AS metric, last_server_id AS server_id, COUNT(*) AS rows_written FROM st_records GROUP BY last_server_id ORDER BY last_server_id;
SELECT 'stage_writer' AS metric, last_server_id AS server_id, COUNT(*) AS rows_written FROM st_stage_records GROUP BY last_server_id ORDER BY last_server_id;
SELECT 'player_last_seen' AS metric, last_server_id AS server_id, COUNT(*) AS players FROM st_players GROUP BY last_server_id ORDER BY last_server_id;
"@

try {
    $output = $query | & $context.ClientExe "--defaults-extra-file=$defaultsFile" `
        "--database=$($context.Connection.database)" --batch --raw
    if ($LASTEXITCODE -ne 0) { throw "MariaDB consistency audit failed with exit code $LASTEXITCODE." }
    $output
    $joined = $output -join "`n"
    foreach ($metric in @("orphan_records", "orphan_replays", "orphan_splits", "orphan_validation", "orphan_player_stats", "orphan_pb_history", "orphan_stage_replays")) {
        if ($joined -notmatch "(?m)^$metric\s+0$") { throw "Consistency audit found a non-zero or missing $metric result." }
    }
}
finally {
    Remove-Item -LiteralPath $defaultsFile -Force -ErrorAction SilentlyContinue
}

Write-Host "SurfTimer database consistency audit passed."
