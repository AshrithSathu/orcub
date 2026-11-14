CREATE TABLE booster_unlock_claims (
 player_id uuid NOT NULL REFERENCES players(id) ON DELETE CASCADE,
 kind text NOT NULL CHECK(kind IN ('hint','undo','unclamp')),
 receipt jsonb NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(player_id,kind)
);
