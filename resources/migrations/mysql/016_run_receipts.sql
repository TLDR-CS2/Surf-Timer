CREATE TABLE IF NOT EXISTS st_run_receipts (
    run_id CHAR(32) CHARACTER SET ascii NOT NULL PRIMARY KEY,
    result_json LONGTEXT NULL,
    created_at DATETIME(6) NOT NULL
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS st_catalog_authority (
    id TINYINT NOT NULL PRIMARY KEY,
    server_id VARCHAR(64) NOT NULL
) ENGINE=InnoDB;
