ALTER TABLE st_run_receipts
    ADD COLUMN ruleset_fingerprint VARCHAR(128) NOT NULL DEFAULT 'legacy',
    ADD COLUMN player_steam_id BIGINT UNSIGNED NULL,
    ADD COLUMN map_name VARCHAR(255) NULL,
    ADD COLUMN route_type VARCHAR(16) NULL,
    ADD COLUMN route_index SMALLINT UNSIGNED NULL;
