CREATE TABLE IF NOT EXISTS st_replays (
    record_id BIGINT UNSIGNED NOT NULL,
    format_version SMALLINT UNSIGNED NOT NULL,
    sample_rate_hz SMALLINT UNSIGNED NOT NULL,
    frame_count INT UNSIGNED NOT NULL,
    duration_us BIGINT UNSIGNED NOT NULL,
    compressed_frames LONGBLOB NOT NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (record_id),
    CONSTRAINT fk_st_replays_record FOREIGN KEY (record_id) REFERENCES st_records(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
