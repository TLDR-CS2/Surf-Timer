ALTER TABLE st_records
    DROP INDEX ix_st_records_leaderboard,
    ADD KEY ix_st_records_leaderboard
        (map_id, route_type, route_index, style, mode, best_time_us, pb_updated_at, player_steam_id);

ALTER TABLE st_stage_records
    DROP INDEX ix_st_stage_records_leaderboard,
    ADD KEY ix_st_stage_records_leaderboard
        (map_id, stage, best_time_us, pb_updated_at, player_steam_id);
