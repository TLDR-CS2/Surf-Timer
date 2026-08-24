CREATE TABLE IF NOT EXISTS st_players (
    steam_id BIGINT UNSIGNED NOT NULL,
    last_name VARCHAR(64) NOT NULL,
    first_seen_at DATETIME(6) NOT NULL,
    last_seen_at DATETIME(6) NOT NULL,
    first_server_id VARCHAR(64) NOT NULL,
    last_server_id VARCHAR(64) NOT NULL,
    total_connections INT UNSIGNED NOT NULL DEFAULT 1,
    PRIMARY KEY (steam_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS st_maps (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    name VARCHAR(255) NOT NULL,
    workshop_id VARCHAR(32) NULL,
    checkpoint_count SMALLINT UNSIGNED NOT NULL DEFAULT 0,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_st_maps_name (name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS st_records (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    map_id BIGINT UNSIGNED NOT NULL,
    player_steam_id BIGINT UNSIGNED NOT NULL,
    route_type VARCHAR(16) NOT NULL DEFAULT 'main',
    route_index SMALLINT UNSIGNED NOT NULL DEFAULT 0,
    style SMALLINT UNSIGNED NOT NULL DEFAULT 0,
    mode VARCHAR(24) NOT NULL DEFAULT 'surf',
    best_time_us BIGINT UNSIGNED NOT NULL,
    completions INT UNSIGNED NOT NULL DEFAULT 1,
    first_completed_at DATETIME(6) NOT NULL,
    last_completed_at DATETIME(6) NOT NULL,
    pb_updated_at DATETIME(6) NOT NULL,
    last_server_id VARCHAR(64) NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_st_records_route (map_id, player_steam_id, route_type, route_index, style, mode),
    KEY ix_st_records_leaderboard (map_id, route_type, route_index, style, mode, best_time_us),
    CONSTRAINT fk_st_records_map FOREIGN KEY (map_id) REFERENCES st_maps(id),
    CONSTRAINT fk_st_records_player FOREIGN KEY (player_steam_id) REFERENCES st_players(steam_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS st_record_splits (
    record_id BIGINT UNSIGNED NOT NULL,
    checkpoint SMALLINT UNSIGNED NOT NULL,
    split_time_us BIGINT UNSIGNED NOT NULL,
    PRIMARY KEY (record_id, checkpoint),
    CONSTRAINT fk_st_record_splits_record FOREIGN KEY (record_id) REFERENCES st_records(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
