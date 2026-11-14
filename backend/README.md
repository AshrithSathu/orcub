# Orcub backend

Original Rust/axum service for first-party telemetry, deterministic config/experiments and Firebase-owned cloud saves. No deployment or cloud resources are created by this repository. Unity remains independent; see [the exact client contract](clients/unity-contract.md).

## Local run

Rust stable required. Copy `.env.example` to `.env` and generate a real local admin secret (`openssl rand -hex 32`). Do not commit `.env`.

```sh
cd backend
export ADMIN_TOKEN="$(openssl rand -hex 32)"
docker compose up --build
```

The API listens on localhost:8080; PostgreSQL on localhost:55432. Alternatively start only `db`, export DATABASE_URL, then `cargo run`. Cargo does not automatically load `.env`: export variables in the shell. Migrations run before readiness at startup; database role must own/create tables. Use a separate migration step/role for tightly restricted production roles. SQL uses bound parameters throughout.

```sh
export DATABASE_URL=postgres://orcub:local-development-only@127.0.0.1:55432/orcub
export SQLX_OFFLINE=true
cargo test --locked
cargo clippy --all-targets --locked -- -D warnings
cargo fmt --check
```

Tests create/drop disposable databases using sqlx::test; DATABASE_URL must point to a **test server** and the user needs CREATEDB. Never point tests at production. Docker is unavailable in the implementation environment (daemon socket absent); tests also run against disposable Homebrew PostgreSQL 17.11. Dynamic `sqlx::query` statements plus embedded migrations permit `SQLX_OFFLINE=true` builds without a database or `.sqlx` metadata. Real SQL is exercised by integration tests; offline compilation alone does not validate SQL. Fixture RSA keys are public test-only material, never production credentials.

## API

All game routes are `/v1`. POST/PUT JSON, UTC RFC3339 timestamps. Errors are non-sensitive JSON except framework parsing/body/timeout errors. Known schema violations return 400/422, missing credentials 401, missing link 404, conflicting save revision 409, excessive body 413, rate limit 429 (Retry-After: 1), dependencies unavailable 503. Request IDs are returned in X-Request-Id. There is no CORS middleware or permissive cross-origin response.

```sh
curl -i http://localhost:8080/healthz
curl -i http://localhost:8080/readyz
curl -s http://localhost:8080/v1/install -H 'Content-Type: application/json' --data \
'{"install_id":"ba62d04b-8ded-48db-b0cd-2bca52e4b569","app_version":"1.0","build":"1","platform":"ios","os_version":"18","device_model":"iPhone17.1","device_ram_mb":8192,"screen":{"w":390,"h":844,"dpi":460},"locale":"en-IN","language":"en","timezone":"Asia/Kolkata","first_open_ts":"2026-11-14T12:00:00Z"}'
# Store response.player_id and response.install_token securely, then:
curl -i "http://localhost:8080/v1/config?player_id=$PLAYER_ID" -H "X-Install-Token: $INSTALL_TOKEN"
curl -i "http://localhost:8080/v1/config?player_id=$PLAYER_ID" -H "X-Install-Token: $INSTALL_TOKEN" -H 'If-None-Match: "previous-etag"'
gzip -c events.json | curl http://localhost:8080/v1/events -H 'Content-Type: application/json' -H 'Content-Encoding: gzip' -H "X-Install-Token: $INSTALL_TOKEN" --data-binary @-
curl http://localhost:8080/v1/link -H "Authorization: Bearer $FIREBASE_ID_TOKEN" -H "X-Install-Token: $INSTALL_TOKEN" -H 'Content-Type: application/json' --data "{\"player_id\":\"$PLAYER_ID\"}"
curl http://localhost:8080/v1/save -H "Authorization: Bearer $FIREBASE_ID_TOKEN"
curl -X PUT http://localhost:8080/v1/save -H "Authorization: Bearer $FIREBASE_ID_TOKEN" -H 'Content-Type: application/json' --data @save.json
curl http://localhost:8080/v1/account/export -H "Authorization: Bearer $FIREBASE_ID_TOKEN" -o account.ndjson
curl -X DELETE http://localhost:8080/v1/account -H "Authorization: Bearer $FIREBASE_ID_TOKEN"
for endpoint in funnel retention ads export/attempts; do
  curl "http://localhost:8080/v1/admin/$endpoint" -H "ADMIN_TOKEN: $ADMIN_TOKEN"
done
```

Install returns a fresh random bearer secret, stored only as SHA-256. Repeat registration requires that credential. Config and ingestion require X-Install-Token; UUID knowledge never grants access. `/v1/link` requires both this credential and Firebase ID token. The verified UID alone selects save/export/delete ownership. Google securetoken RSA JWKS are fetched over TLS and cached according to Cache-Control; only RS256, matching kid, aud `orcub-f0091`, issuer `https://securetoken.google.com/orcub-f0091`, unexpired exp, nonempty bounded sub and nonfuture iat/auth_time are accepted. Verification follows [Firebase's ID token requirements](https://firebase.google.com/docs/auth/admin/verify-id-tokens). It does not check Firebase revocation/disabled-user state; previously issued ID tokens remain valid until expiry. Add revocation checks before policies require immediate invalidation. Keys are never refreshed per unknown kid, preventing attacker-driven fetch storms; rotation is picked up at cache expiry.

`GET /healthz` is process liveness; `/readyz` checks PostgreSQL (schema is migrated before serving). Admin APIs require a constant-time verified ADMIN_TOKEN header. Use ingress access control too; the admin token must never ship in Unity.

Analytics accept `start` inclusive and `end` exclusive RFC3339, default last 30 days, maximum window 93 days. All days/cohorts are UTC. Funnel reports starts, ends, wins, losses, quits, losses/ends, median moves/seconds and summed boosters from completed attempt records. Retention uses server-created player cohorts and session-start activity on exactly day 1/day 7; rates for immature cohorts are null. Returning guest linkage retains the canonical account's cohort. Ads reports impressions, rewarded completions per rewarded offer and paid revenue/ARPDAU by currency; DAU counts session-start players. Estimates are client-reported, not settlement or anti-fraud proof. IAP has no entitlement handling.

Attempt export is NDJSON containing exactly AttemptRecord's field names, with no player identifiers. Page up to 1,000 records with `X-Next-Cursor`; pass it as `after=UUID`, keeping start/end fixed. Order is event UUID for pagination; quiesce ingestion or export a settled window to avoid newly arriving IDs sorting behind the cursor. Per-account export is NDJSON from a repeatable-read database snapshot and includes all retained rows but no credential hashes. Large exports tie up one DB connection; bound concurrent exports at ingress.

## Save conflicts and identity merges

PUT sends the revision last read (`0` for first save). Under a player row lock, the server requires equality, increments by one and returns the canonical full snapshot. Stale/future/replayed revisions return 409/current state. Progress is maximum highest_level and maximum stars per level (0–3), even when a newer save sends lower values. Inventory, workshop, lives, preferences and skill state replace the previous snapshot only on an accepted revision. This prevents stale writes from restoring spent inventory; it is not server-authoritative purchase/economy validation. On conflict the client must reapply legitimate pending actions rather than replay an old snapshot at a new revision.

Link is serialised by UID and locks player rows in UUID order. A player already owned by a different UID cannot be stolen. If UID exists, installations and telemetry move to the canonical player, canonical assignments/consumables remain, maximum progress/stars merge, revision increments and the obsolete guest row is removed. Client updates its player_id. Firebase client `LinkWithCredential` preserves an anonymous UID where possible; signing in to an existing account uses the merge path. DELETE cascades all backend rows; receipt is returned only. It does not delete Firebase Auth/Analytics/AdMob/Apple data: implement the external deletion and Apple revocation workflow before App Store submission, as documented in the contract/inventory.

## Storage, operations and deployment

Tables: players; installs (hashed secrets and coarse metadata); experiments (persisted SHA-256 bucket assignments); events (typed JSONB and UUID idempotency); raw_events (numeric-only unknown payloads); saves (revisioned validated JSONB). Foreign keys cascade on deletion. BRIN(received_at), player/time, kind/time and partial level-expression indexes support time-series and account access without monthly partition maintenance. No raw addresses or advertising IDs. See [the full inventory](docs/privacy-data-inventory.md).

Environment: required DATABASE_URL and ADMIN_TOKEN (32+ characters); PORT default 8080; RETENTION_DAYS default 400, allowed 1–400; TRUST_COUNTRY_HEADERS default false; RUST_LOG default orcub_backend=info. Tuning, model and ad policy are versioned with the image (JSON files in config; ad policy in new_state); publish changes by rebuilding after client validation, not by writing user-specific spend rules. Buckets are stored even if allocation code changes. Both baseline buckets currently receive the same shipped policy.

Limits: 2 MiB compressed/expanded body; 500-event transaction; 100 requests/second per process with burst 200 using governor, no IP keys; 30-second HTTP timeout; 10 DB connections, 5-second acquisition timeout. Add ingress quotas/App Check or equivalent abuse controls before public launch, since install registration is intentionally unauthenticated. Multi-instance rate quotas are independent. Streaming exports have bounded memory but HTTP header timeout does not cap their full stream lifetime; use ingress stream/concurrency limits. JSON logs omit request bodies, tokens, query strings and IPs. Termination drains HTTP requests and stops the retention task. TLS terminates at controlled ingress.

Hourly retention deletes events older than RETENTION_DAYS by received_at and cascades raw payloads. `cargo run -- retention` or container arg `retention` runs a one-shot purge. Large deployments should schedule it off-peak and monitor delete/vacuum costs; BRIN is the chosen time-series strategy. Event UUID deduplication expires with retained rows.

**Recommended deployment:** Cloud Run in `orcub-f0091` and managed PostgreSQL. The owner must choose **Cloud SQL Postgres** (same-region private connectivity/IAM network setup, more GCP operations/cost control) or **Supabase Postgres** (external managed Postgres, TLS connection/pooler and regional latency considerations). This implementation uses plain Postgres, no Supabase-specific services. Use TLS and proper certificate verification for external DB connections; do not disable TLS verification.

The owner must supply/approve: Firebase/GCP billing with Blaze enabled, database provider/region and DATABASE_URL, random ADMIN_TOKEN via Secret Manager, service account and network access, HTTPS endpoint/DNS, Firebase Google/Apple providers/client configuration, infrastructure log privacy/retention, backup/deletion policy and external identity deletion workflow. Set Cloud Run concurrency/pool/maximum instances so total DB connections fit the provider. Startup migrations require a migration-capable role; use a release migration job for strict production role separation. Cloud Run's idle CPU/scale-to-zero means the hourly task is not a scheduling guarantee: provision a daily Cloud Run Job running `retention`. Firebase public key verification needs outbound HTTPS, not a service-account private key. Do not enable country trust until ingress strips/spoof-proofs both country headers.

Docker uses a multi-stage Rust build and a nonroot Debian slim runtime with CA certificates. Build/test the image on CI before promotion: Docker daemon was unavailable here. No cloud deployment, live Firebase login, signed device request, or production load test has been performed.

## Load-test note

Against a disposable staging DB only, run k6/vegeta with realistic install credentials, 20–100 events/batch, retries and gzip. Ramp to expected launch throughput and observe p50/p95/p99, 429s, DB pool wait, CPU, memory, WAL, index/vacuum growth and retention purge latency. Target p95 <250 ms ingestion at the chosen regional DB and planned peak, no lost acknowledged events and exactly one event per UUID after retries. Include concurrent save conflicts, slow/oversized gzip, export streams and retention; measure rather than treating these targets as achieved. Capacity and quotas require owner traffic estimates.

When an already linked Firebase anonymous user signs into an existing account with a different UID, preserve its ID token before sign-in and supply it in `X-Previous-Firebase-Token` on `/v1/link`. The server verifies both tokens and requires the previous token to be from the anonymous provider and to own the source player. Omit it when UID is unchanged or the install has never linked. Expired previous tokens cannot authorise a merge; refresh before switching identities.
