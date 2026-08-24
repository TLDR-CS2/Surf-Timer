CREATE TABLE IF NOT EXISTS st_admin_audit (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    actor_steam_id BIGINT UNSIGNED NOT NULL,
    actor_name VARCHAR(64) NOT NULL,
    server_id VARCHAR(64) NOT NULL,
    action_name VARCHAR(64) NOT NULL,
    target_value VARCHAR(255) NOT NULL,
    details TEXT NOT NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    KEY ix_st_admin_audit_created (created_at),
    KEY ix_st_admin_audit_actor (actor_steam_id, created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
