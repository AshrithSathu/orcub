-- A scoped capability minted only while the source Firebase identity is anonymous.
-- Install possession is also required at redemption; deletion/merge cascades revoke it.
CREATE TABLE guest_merge_proofs (
 player_id uuid PRIMARY KEY REFERENCES players(id) ON DELETE CASCADE,
 firebase_uid text NOT NULL,
 credential_hash text NOT NULL,
 proof_hash text NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now()
);
