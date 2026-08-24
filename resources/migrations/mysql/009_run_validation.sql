CREATE TABLE IF NOT EXISTS st_run_validation (
    record_id BIGINT UNSIGNED NOT NULL,
    validation_version SMALLINT UNSIGNED NOT NULL,
    maximum_speed DOUBLE NOT NULL,
    overspeed_samples INT UNSIGNED NOT NULL,
    maximum_frame_distance DOUBLE NOT NULL,
    position_jump_count INT UNSIGNED NOT NULL,
    flags VARCHAR(255) NOT NULL,
    analyzed_at DATETIME(6) NOT NULL,
    PRIMARY KEY (record_id),
    KEY ix_st_run_validation_flags (flags),
    CONSTRAINT fk_st_run_validation_record FOREIGN KEY (record_id) REFERENCES st_records(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
