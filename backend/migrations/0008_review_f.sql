-- no-transaction
CREATE INDEX CONCURRENTLY IF NOT EXISTS rewarded_nonces_player_confirmed ON rewarded_nonces(player_id,confirmed_at);
