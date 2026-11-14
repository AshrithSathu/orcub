-- no-transaction
CREATE INDEX CONCURRENTLY IF NOT EXISTS unlimited_windows_player_ends ON unlimited_windows(player_id,ends_at);
