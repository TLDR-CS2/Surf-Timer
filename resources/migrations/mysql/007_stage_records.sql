CREATE TABLE IF NOT EXISTS st_stage_records (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    map_id BIGINT UNSIGNED NOT NULL,
    player_steam_id BIGINT UNSIGNED NOT NULL,
    stage SMALLINT UNSIGNED NOT NULL,
    best_time_us BIGINT UNSIGNED NOT NULL,
    completions INT UNSIGNED NOT NULL DEFAULT 1,
    first_completed_at DATETIME(6) NOT NULL,
    last_completed_at DATETIME(6) NOT NULL,
    pb_updated_at DATETIME(6) NOT NULL,
    last_server_id VARCHAR(64) NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_st_stage_records_player (map_id, player_steam_id, stage),
    KEY ix_st_stage_records_leaderboard (map_id, stage, best_time_us),
    CONSTRAINT fk_st_stage_records_map FOREIGN KEY (map_id) REFERENCES st_maps(id),
    CONSTRAINT fk_st_stage_records_player FOREIGN KEY (player_steam_id) REFERENCES st_players(steam_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
