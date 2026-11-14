# First-party data inventory and policy inputs

No raw IP, IDFA, IDFV, name, email, email hash, OAuth profile, advertising identifier, purchase receipt, access token or Firebase token is persisted. Only SHA-256 of a cryptographically random install secret is stored. Firebase tokens are checked in memory; their optional email/name claims are ignored. Unknown payloads allow numeric metrics only. Schema enforcement cannot identify personal information misused as an allowed label: clients must send enumerated machine codes, never user input.

| Collection / every field | Purpose | Retention |
|---|---|---|
| players: id (random UUID), firebase_uid (nullable), created_at | Account ownership, cohorts | Until account deletion; owner should set an inactive-account policy |
| installs: install_id (client random UUID), player_id, credential_hash, created_at | Install continuity, telemetry authentication | Until account deletion |
| install metadata: install_id, app_version, build, platform, os_version, device_model, device_ram_mb, screen.w/h/dpi, locale, language, timezone, first_open_ts | Compatibility, display/performance analysis, language support | Until account deletion; no exact location |
| installs.country | Optional two-letter coarse country from trusted CF-IPCountry/X-Country | Until account deletion |
| experiments: player_id, experiment, bucket, assigned_at | Reproducible experiments and stable allocation | Until account deletion |
| events: event_id, player_id, kind, occurred_at, received_at | Deduplication, event ordering, first-party analytics | Configurable 1–400 days from server receipt, default 400; hourly purge or scheduled job |
| session payload: session_id, duration | Activity, session duration, D1/D7 retention | Event retention |
| level_start: LevelIndex, Variant, Plan.variant/extraMoves/freeHintGlow/reason/moveLimit | Attempt funnel and policy evaluation | Event retention |
| level_end: Version, Variant, TuningVersion, ModelVersion, LevelIndex, StartedUtc, LevelRating, RatingBefore, RatingAfter, ModelPrediction, SkillEstimate, SkillConfidence, Plan (above) | Skill calibration, adaptive difficulty, first-party ML training | Event retention |
| level_end Features: Moves[].Useful/Undo/ThinkSeconds/LockedTouches, Count, Useful, Think, Locked, Undo | Early move quality, thinking time, assistance needs | Event retention |
| level_end Result: Outcome, MovesUsed, Seconds, BoostersUsed, Continues | Completion quality and difficulty | Event retention |
| booster_used: level, booster | Assistance usage | Event retention |
| ad_offer/shown/reward: placement, network; ad_failed: placement, network, code | Ad delivery, opt-in reward funnel | Event retention |
| ad_paid: placement, network, value_micros, currency, precision | AdMob callback revenue estimates, per-currency ARPDAU | Event retention |
| iap: product_id, value_micros, currency | Future purchase analytics; no transaction verification | Event retention |
| settings_changed: sound, haptics | Accessibility/preference insights | Event retention |
| tutorial_step: step, completed | Onboarding funnel | Event retention |
| error: code, context | Bounded diagnostic codes; no text, stack trace or URL | Event retention |
| raw_events: event_id, payload.metric_* scalar numbers/bools/null | Forward-compatible game metrics | Cascades with event retention |
| saves: player_id, revision, updated_at; data.highest_level, stars[level], inventory[code], workshop[code], lives | Cross-device progress, economy snapshot | Until account deletion |
| saves.data.settings.sound/haptics; skill.rating/games/recent[].won/efficiency/seconds_per_move/boosters/continues | Preference and adaptive skill continuity | Until account deletion |
| HTTP operational logs: method, status, elapsed_ms; errors: DB error code only | Operations without request contents or addresses | Owner configures infrastructure logs, suggested 30 days |
| deletion receipt: receipt_id, deleted_at, scope, deleted | Client-visible confirmation | Returned only, not stored |

## Handling and policy draft inputs

Data is linked to a pseudonymous player; once linked, Firebase UID associates installations across devices. Purposes are app functionality, product analytics, diagnostics and personalisation. No first-party data is sold or shared for cross-app advertising by this service. Ad revenue is not used in difficulty selection. The owner must assess AdMob/Firebase SDK collection separately for App Store privacy labels, tracking declarations, ATT, age/consent and regional consent requirements. This inventory covers this backend only and is input for review, not a finished privacy policy or certification.

Export and deletion require a valid Firebase token for the linked UID (including anonymous Firebase accounts). Link guests early to support deletion of their first-party data. Deletion cascades immediately through all backend tables, including raw events. No retained aggregate tables contain deleted players. PostgreSQL backups/PITR and external ML exports are outside live-table deletion: owner must configure expiry, access controls and a restore/deletion process, and delete exported training records as appropriate. A suggested backup maximum is 30 days. Firebase Auth, Firebase Analytics, Remote Config identifiers, AdMob and Apple identity revocation require their own deletion integration; this API does not silently claim to delete them.

Trust country headers only if a controlled ingress strips client-supplied copies and sets the authoritative value. Default is disabled/null. No GeoLite database or raw-address processing is included. Application logs omit IPs, headers, query strings and bodies; Cloud Run/load balancer/Postgres/proxy access logs must be configured separately to meet the no-IP requirement. Keep ADMIN_TOKEN and DATABASE_URL in a secret manager. Restrict exports to authorised operators, encrypt in transit/at rest, and do not retain exports indefinitely.

Hourly purge runs while the server has CPU. For Cloud Run scale-to-zero, schedule the same image with argument `retention` as a Cloud Run Job at least daily; backlog is removed at next execution. Deduplication lasts only while event IDs are retained. Ingestion rejects client timestamps older than 400 days, and future clock skew above five minutes. Cohorts/analytics query retained data and cannot recover expired events.
