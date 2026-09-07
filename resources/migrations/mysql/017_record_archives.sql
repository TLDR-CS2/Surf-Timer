CREATE TABLE IF NOT EXISTS st_record_archives (
    archive_id CHAR(32) CHARACTER SET ascii NOT NULL PRIMARY KEY,
    player_steam_id BIGINT UNSIGNED NOT NULL,
    map_name VARCHAR(255) NOT NULL,
    route_type VARCHAR(16) NOT NULL,
    route_index SMALLINT UNSIGNED NOT NULL,
    actor_steam_id BIGINT UNSIGNED NOT NULL,
    reason VARCHAR(1024) NOT NULL,
    payload LONGTEXT NOT NULL,
    invalidated_at DATETIME(6) NOT NULL,
    restored_at DATETIME(6) NULL,
    restored_by BIGINT UNSIGNED NULL
) ENGINE=InnoDB;
