CREATE TABLE IF NOT EXISTS st_competitive_rulesets (
    map_name VARCHAR(255) NOT NULL PRIMARY KEY,
    fingerprint VARCHAR(128) NOT NULL,
    approved_by_server VARCHAR(64) NOT NULL,
    approved_at DATETIME(6) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
