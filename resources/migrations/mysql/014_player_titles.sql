CREATE TABLE IF NOT EXISTS st_player_titles (
    player_steam_id BIGINT UNSIGNED NOT NULL,
    is_vip BOOLEAN NOT NULL DEFAULT FALSE,
    custom_title VARCHAR(10) NULL,
    color_pattern VARCHAR(31) NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (player_steam_id),
    CONSTRAINT fk_st_player_titles_player
        FOREIGN KEY (player_steam_id) REFERENCES st_players(steam_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
