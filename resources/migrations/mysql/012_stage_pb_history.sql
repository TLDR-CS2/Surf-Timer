CREATE TABLE IF NOT EXISTS st_stage_pb_history (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    stage_record_id BIGINT UNSIGNED NOT NULL,
    player_steam_id BIGINT UNSIGNED NOT NULL,
    map_id BIGINT UNSIGNED NOT NULL,
    stage SMALLINT UNSIGNED NOT NULL,
    previous_time_us BIGINT UNSIGNED NULL,
    new_time_us BIGINT UNSIGNED NOT NULL,
    achieved_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    KEY ix_stage_pb_history_player_recent (player_steam_id, achieved_at),
    KEY ix_stage_pb_history_map_recent (map_id, achieved_at),
    CONSTRAINT fk_stage_pb_history_record FOREIGN KEY (stage_record_id) REFERENCES st_stage_records(id) ON DELETE CASCADE,
    CONSTRAINT fk_stage_pb_history_player FOREIGN KEY (player_steam_id) REFERENCES st_players(steam_id) ON DELETE CASCADE,
    CONSTRAINT fk_stage_pb_history_map FOREIGN KEY (map_id) REFERENCES st_maps(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

INSERT INTO st_stage_pb_history
    (stage_record_id,player_steam_id,map_id,stage,previous_time_us,new_time_us,achieved_at)
SELECT sr.id,sr.player_steam_id,sr.map_id,sr.stage,NULL,sr.best_time_us,sr.pb_updated_at
FROM st_stage_records sr
WHERE NOT EXISTS (SELECT 1 FROM st_stage_pb_history h WHERE h.stage_record_id=sr.id);
