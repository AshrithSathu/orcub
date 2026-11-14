# Rewarded life verification

In AdMob, open rewarded unit `ca-app-pub-7345153763598739/3829033868`, then server-side verification settings. Set callback URL to `https://orcub.sathuashrith.com/v1/ads/ssv`, verify, and save. Production status (2026-11-15, owner verified): this callback is configured and verified. Do not supply a fixed user ID or custom data in production: the client sets both per impression.

The client requests `POST /v1/ads/nonce` with player_id and placement (`rewarded_lives`, `out_of_lives`, or `retry_lives`) before showing the ad. The server reserves a random, player/placement-bound nonce, capped at 20 reservations per UTC day. Cancelled impressions consume a reservation: this conservative choice prevents concurrent devices from exceeding the provisional cap. Nonces expire after 24 hours. On reward, the client immediately applies a durable provisional refill; no gold is spent. Only life spends in the first ten minutes carry rewarded_nonce and wait server-side for confirmation (retriable 503). After ten minutes, unconfirmed spends debit real balances normally. When no spends are queued, wallet polling reconciles rewarded_nonces and applied_keys. Editor simulated ads apply capped provisional refills only; they never mint a server grant. Unconfirmed local grants expire after ten minutes.

The SSV endpoint validates the exact encoded query with ECDSA/SHA256 and Google's public key, refreshes cached keys hourly or on an unknown key ID, restricts the rewarded unit, and atomically consumes the nonce and unique transaction ID under the player lock. Replays of the same nonce/transaction succeed without another grant. The ledger records delta=0, reason=rewarded_refill, revenue=false; this is excluded from purchase revenue.

Reference: https://developers.google.com/admob/unity/ssv

# Firebase identity deletion setup

Production status (2026-11-15, owner verified): `FIREBASE_ADMIN_SERVICE_ACCOUNT_JSON` is set on Railway and server-side deletion was smoke-tested end to end with `identity_deleted=true`. Credentials must never be logged or committed.

Without the variable, backend data deletion succeeds with identity_deleted=false and the client retains its matching-UID Firebase deletion fallback. With configured credentials, failed admin deletion returns success with identity_deleted=false after writing a UID-hash tombstone, enabling the client fallback; authenticated retries finish the identity deletion without requiring the old player row. Tombstones hold only SHA256 identity/credential subjects, not email or raw tokens. They survive player deletion for retry safety and expire after 30 days.

Lives placements accept `rewarded_lives`, `out_of_lives`, `retry_lives`, `rewarded_out_of_lives`, `rewarded_retry_lives`, and legacy `rewarded_refill_lives`. New clients normalise all lives placements to `rewarded_lives`.
