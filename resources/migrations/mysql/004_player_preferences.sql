CREATE TABLE IF NOT EXISTS st_player_preferences (
    player_steam_id BIGINT UNSIGNED NOT NULL,
    hud_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    speed_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    status_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    keys_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    sounds_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    replay_hud_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (player_steam_id),
    CONSTRAINT fk_st_player_preferences_player
        FOREIGN KEY (player_steam_id) REFERENCES st_players(steam_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
