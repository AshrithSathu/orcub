CREATE TABLE wallets (
 player_id uuid PRIMARY KEY REFERENCES players(id) ON DELETE CASCADE,
 gold bigint NOT NULL DEFAULT 0,
 inventory jsonb NOT NULL DEFAULT '{}', lives integer NOT NULL DEFAULT 5,
 unlimited_until timestamptz
);
CREATE TABLE wallet_ledger (
 id uuid PRIMARY KEY, player_id uuid NOT NULL REFERENCES players(id) ON DELETE CASCADE,
 delta bigint NOT NULL, reason text NOT NULL, ref_id text NOT NULL,
 idempotency_key text NOT NULL, request jsonb NOT NULL, balance_after bigint NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now(), UNIQUE(player_id,idempotency_key)
);
CREATE INDEX ledger_player_time ON wallet_ledger(player_id,created_at);
CREATE TABLE purchases (
 transaction_id text PRIMARY KEY, original_transaction_id text NOT NULL,
 player_id uuid NOT NULL REFERENCES players(id) ON DELETE CASCADE,
 product_id text NOT NULL, environment text NOT NULL,
 price_milliunits bigint, currency text, storefront text,
 gold bigint NOT NULL, benefits jsonb NOT NULL, purchased_at timestamptz NOT NULL,
 revoked boolean NOT NULL DEFAULT false, created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX purchases_player ON purchases(player_id);
CREATE TABLE purchase_revocations (
 transaction_id text PRIMARY KEY, player_id uuid REFERENCES players(id) ON DELETE CASCADE,
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE economy_offers (
 player_id uuid PRIMARY KEY REFERENCES players(id) ON DELETE CASCADE,
 first_shown_at timestamptz NOT NULL DEFAULT now(), last_shown_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE wallet_aliases (
 token uuid PRIMARY KEY, player_id uuid NOT NULL REFERENCES players(id) ON DELETE CASCADE,
 created_at timestamptz NOT NULL DEFAULT now()
);
