CREATE TABLE IF NOT EXISTS st_stage_replays (
    stage_record_id BIGINT UNSIGNED NOT NULL,
    format_version INT UNSIGNED NOT NULL,
    sample_rate_hz SMALLINT UNSIGNED NOT NULL,
    frame_count INT UNSIGNED NOT NULL,
    duration_us BIGINT UNSIGNED NOT NULL,
    compressed_frames LONGBLOB NOT NULL,
    recorded_at DATETIME(6) NOT NULL,
    PRIMARY KEY (stage_record_id),
    CONSTRAINT fk_stage_replays_record FOREIGN KEY (stage_record_id) REFERENCES st_stage_records(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
