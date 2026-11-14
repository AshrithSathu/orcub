ALTER TABLE wallets ADD COLUMN lives_updated_at timestamptz NOT NULL DEFAULT now();
ALTER TABLE wallet_ledger ADD COLUMN environment text;
UPDATE wallet_ledger l SET environment=p.environment FROM purchases p WHERE l.ref_id=p.transaction_id AND l.reason IN ('iap','refund');
CREATE TABLE entitlement_transfers (
 id bigserial PRIMARY KEY,
 original_transaction_id text NOT NULL,
 environment text NOT NULL,
 from_player uuid REFERENCES players(id) ON DELETE SET NULL,
 player_id uuid NOT NULL REFERENCES players(id) ON DELETE CASCADE,
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX entitlement_transfer_original ON entitlement_transfers(environment,original_transaction_id,created_at);
CREATE INDEX purchase_original ON purchases(environment,original_transaction_id);
CREATE TABLE player_value_cache (
 player_id uuid PRIMARY KEY REFERENCES players(id) ON DELETE CASCADE,
 value jsonb NOT NULL, updated_at timestamptz NOT NULL DEFAULT now()
);
