ALTER TABLE st_player_titles MODIFY color_pattern VARCHAR(255) NULL;
ALTER TABLE st_player_titles ADD COLUMN name_color VARCHAR(16) NOT NULL DEFAULT 'default' AFTER color_pattern
