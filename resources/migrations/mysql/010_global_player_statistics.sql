CREATE TABLE IF NOT EXISTS st_player_run_stats (
    player_steam_id BIGINT UNSIGNED NOT NULL,
    tracked_completions BIGINT UNSIGNED NOT NULL DEFAULT 0,
    tracked_time_us BIGINT UNSIGNED NOT NULL DEFAULT 0,
    tracking_started_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (player_steam_id),
    CONSTRAINT fk_player_run_stats_player FOREIGN KEY (player_steam_id) REFERENCES st_players(steam_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS st_pb_history (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    record_id BIGINT UNSIGNED NOT NULL,
    player_steam_id BIGINT UNSIGNED NOT NULL,
    map_id BIGINT UNSIGNED NOT NULL,
    route_type VARCHAR(16) NOT NULL,
    route_index SMALLINT UNSIGNED NOT NULL DEFAULT 0,
    previous_time_us BIGINT UNSIGNED NULL,
    new_time_us BIGINT UNSIGNED NOT NULL,
    achieved_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    KEY ix_pb_history_player_recent (player_steam_id, achieved_at),
    KEY ix_pb_history_map_recent (map_id, achieved_at),
    CONSTRAINT fk_pb_history_record FOREIGN KEY (record_id) REFERENCES st_records(id) ON DELETE CASCADE,
    CONSTRAINT fk_pb_history_player FOREIGN KEY (player_steam_id) REFERENCES st_players(steam_id) ON DELETE CASCADE,
    CONSTRAINT fk_pb_history_map FOREIGN KEY (map_id) REFERENCES st_maps(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
