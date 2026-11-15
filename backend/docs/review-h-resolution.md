# Review h resolution

Scope: backend, Net, Store, Daily and scoped tests. Game implementation is coordinator commit a6775f1; no Game changes in this commit.

| Finding | Resolution |
|---|---|
| H1 | Previous credentials are resolved only for a different source UID. Same-UID and alias replays ignore stale proofs/tokens. Stale/expired supplied credentials return 200 with `merge_skipped`, retain source data, and attach the install to the caller's account without moving source value. Missing transfer credentials still fail closed. |
| H2 | Accepted sequence entries advance `players.rewarded_frontier` and enqueue payment. Capped claims return 200 with `deferred=true`; wallet refresh and every grant pay deferred debt automatically, oldest first per reward class. No 24-hour expiry for retained saga win evidence. |
| M1 | Sequence gating reads one frontier; newly accepted levels use indexed win lookups. Ordinary sequential claims are constant work relative to lifetime level count. Recovering K dropped claims costs K indexed lookups once, rather than rescanning the lifetime prefix on every claim. Deferred payment reads at most 43 rows and uses indexed daily counts. |
| M2 | Proof redemption has a 30-day TTL and verifies current Firebase account providers through Admin lookup, including for old anonymous tokens. Permanent/deleted/disabled identities cannot transfer. Same-UID links revoke proofs; the client also clears them immediately on same-UID credential upgrade. |
| M3 | Wallet responses include an opaque server ISO `applied_since`. Pending consumes capture and persist that exact string. The runtime JSON adapter and queue/cache readers disable automatic date parsing. Relaunch test compares the exact escaped query string. Missing legacy watermarks conservatively request history from the epoch. Malformed server query cursors are ignored. |
| M4 | Anonymous sign-in runs Link and prepares a proof regardless of the in-memory linked flag before switching identity. Network/auth failure aborts the switch while the guest remains recoverable. |
| M5 | Boot resets local state when deletion intent belongs to a missing/different identity. Resume treats Firebase UserNotFound/UserDisabled/UserTokenExpired as completion and resets. Install renewal is blocked during deletion intent/pending deletion. DeletionNeedsConfirmation remains a stable compatibility property returning false; deletion automatically resumes. |
| L1 | Raw guest tokens are no longer persisted; legacy keys are deleted at boot after migrating any saved proof to Keychain. A legacy proof without a stored source UID is sent once for the server to resolve lazily. Proof plus source UID live inside the existing ThisDeviceOnly install Keychain payload, avoiding native signature changes or a second secret-storage API. |
| L2 | Only boosters fully funded by held grant gold are excluded at merge; a small grant balance cannot erase a mostly paid booster. |
| L3 | LastConfirmedRefresh is persisted per wallet and restored on relaunch. |
| L4 | Forward migration 0011 rewrites legacy event UUID reward refs while evidence exists and backfills frontier from the greatest recorded rewarded level. Existing holes are grandfathered without replaying old rewards. Stable refs and frontier survive event retention. |
| L5 | Prepare is limited to 10 requests per UID per minute; proofs expire after 30 days. One proof row per player bounds storage. |
| L6 | Applied-key queries use server watermarks and an additional one-hour overlap. |

## Economy decisions

Normal clears pay 5 gold, at most 40 per UTC day (200 gold). Boss clears, every twentieth level, independently pay 50 gold at most three times per UTC day. Therefore 45 **ordinary** claims produce 40 paid and 5 deferred; indices 0–44 contain two bosses and only 43 ordinary levels. The test/smoke uses 47 total sequential levels to exercise 45 ordinary claims plus two bosses.

Accepted deferred debt never needs evidence again and is removed only when paid in the same transaction. Retained `level_end` wins remain valid regardless of age. Missing queue claims are recovered from contiguous retained wins when the next claim arrives. Safe alternative for evidence pruned **before any claim was accepted**: re-clear the next unrewarded level to supply a fresh win, then later claims can recover the rest. Never pay an unproven gap. Legacy rewarded gaps are grandfathered by migration. Honest accepted claims never lose gold; forged win sequences can accumulate debt but payout remains bounded each day. This remains an honour-based client-win trust boundary, not server puzzle execution.

## Coordinator wiring

- Net binds LastLevelEndEventId; Store binds ClaimLevelReward and BoosterUnlock at service boot.
- Store unlock requests use `source=booster_unlock`, `kind`, and one durably persisted UUID for both ref and idempotency key. Transient failures retain the key; runtime retries every 15 seconds, including after relaunch. Server receipts enforce one grant per kind even if local bookkeeping is lost.
- DailyRuntime boots in the Daily assembly (Daily references Net/Store; Runtime references neither). It binds DailyCapture/Restore, AppOpened and Flushed, scopes services to the canonical player, reconstructs on restore, preserves completed/pending days, and forwards wallet updates through an authoritative wallet refresh. The optional iOS assembly registers its notification provider; absent that package no permission is requested and notifications remain unavailable.
- Net notifies after successful queue uploads and empty flushes. Daily's reentrancy guard prevents reward/flush recursion.
- SaveSnapshot captures `daily`, reconciles completed/pending dates and date maxima, and restores accepted/GET/conflict data through the gateway. Mutable streak/notification state follows the server snapshot.
- AccountBridge implements all three IAccountRecovery forwarding members.
- SettingsScreen exposes a fixed busy state, with no status-text delegate/property. “Finishing your last moves…” needs a Game-side status hook; no Game edits were made. Spend drain still blocks the identity switch safely.
- `level_fallback` is accepted by the backend unknown-event validator with `metric_` fields. Existing bounding validation now explicitly tests that event name. Consent can omit it as optional analytics; it is not reward evidence.

## Verification

- `cargo fmt`, `cargo clippy --locked -- -D warnings`: pass.
- Local disposable PostgreSQL: 52 unit/database tests and 1 API integration test pass. Includes stale same-UID proof/token replays, TTL, upgraded identity rejection, prepare rate limit, legacy migration/pruning, mostly paid booster preservation, normal/boss caps, next-day debt payment, and dropped-queue recovery. Cluster stopped afterwards.
- Standalone compiler using Unity's existing response files: Net, Store, Store.Tests, Daily, Daily.Tests and Orcub.Tests pass. New client tests cover exact watermark relaunch, confirmed-refresh persistence, unlock retry identity, and daily date union.
- Unity EditMode: **pending**. MCP editor state reported Play Mode active, transitioning, and not ready. No Play Mode stop, editor restart, batch mode, or forced refresh was performed.
- Native Google sign-in, Keychain on device, notification delivery, and deletion after SDK identity loss remain device/runtime verification paths; compilation and pure tests do not prove them.

## Production verification

Railway api deployment `24f2d218-7c7a-41d2-ab6c-53a127f1f7e6`: SUCCESS. Production https://orcub.sathuashrith.com:

- health and readiness: 200.
- Anonymous Firebase-bound link succeeded; replay with the already revoked same-UID proof returned 200 again.
- 47 sequential saga clears (45 ordinary plus two bosses): 40 ordinary paid, 5 deferred, both bosses paid, wallet 300 gold.
- Wallet emitted a server ISO watermark.
- Test account deletion: 200; subsequent install wallet access: 401. The earlier failed Firebase-signup attempt's install was also deleted (200, then wallet 401). Firebase signup succeeded after sending the app's configured iOS bundle header.
- Next UTC-day payment was simulated only in the local database tests, never by modifying production clocks or reward timestamps.

