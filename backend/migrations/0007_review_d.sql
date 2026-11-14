ALTER TABLE wallets ADD COLUMN consume_floor timestamptz NOT NULL DEFAULT now();
CREATE TABLE rewarded_nonces (
 nonce uuid PRIMARY KEY, player_id uuid NOT NULL REFERENCES players(id) ON DELETE CASCADE,
 placement text NOT NULL, created_at timestamptz NOT NULL DEFAULT now(),
 transaction_id text UNIQUE, confirmed_at timestamptz
);
CREATE TABLE deletion_tombstones (subject_hash text PRIMARY KEY, deleted_at timestamptz NOT NULL DEFAULT now());
CREATE TABLE unlimited_windows (
 player_id uuid NOT NULL REFERENCES players(id) ON DELETE CASCADE,
 starts_at timestamptz NOT NULL, ends_at timestamptz NOT NULL
);
INSERT INTO unlimited_windows SELECT player_id,consume_floor,unlimited_until FROM wallets WHERE unlimited_until IS NOT NULL;
CREATE FUNCTION remember_unlimited_window() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NEW.unlimited_until IS NOT NULL AND NEW.unlimited_until IS DISTINCT FROM OLD.unlimited_until AND NEW.unlimited_until>now() THEN
  INSERT INTO unlimited_windows VALUES(NEW.player_id,now(),NEW.unlimited_until);
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER wallet_unlimited_window AFTER UPDATE OF unlimited_until ON wallets FOR EACH ROW EXECUTE FUNCTION remember_unlimited_window();

ALTER TABLE events ADD COLUMN dev boolean NOT NULL DEFAULT false;
