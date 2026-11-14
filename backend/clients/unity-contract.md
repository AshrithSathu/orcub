# Unity contract v1

HTTPS only in production. JSON UTF-8, UTC timestamps RFC3339, UUIDs canonical strings. Unknown properties on known types are rejected. Store `install_token` in Keychain; never log tokens. Responses with credentials/saves are no-store. No IDFA, IDFV, names, emails, ad-unit IDs, stack traces or free-form messages. Signed transaction JWS is accepted only on IAP endpoints and is never retained. Error/context/reason and all other string labels are bounded codes, never user input.

## Install and config

`POST /v1/install`, `Content-Type: application/json`:
```json
{"install_id":"ba62d04b-8ded-48db-b0cd-2bca52e4b569","app_version":"1.0.0","build":"1","platform":"ios","os_version":"18.0","device_model":"iPhone17.1","device_ram_mb":8192,"screen":{"w":390,"h":844,"dpi":460},"locale":"en-IN","language":"en","timezone":"Asia/Kolkata","first_open_ts":"2026-11-14T12:00:00Z"}
```
Response `{"player_id":"UUID","install_token":"SECRET","experiments":{"difficulty_baseline_v1":"control"}}` (or `baseline_b`; both use the shipped baseline). Subsequent install calls require `X-Install-Token`; UUID alone cannot recover credentials. Persist credentials before other calls. A lost first response requires a fresh install UUID; there is intentionally no insecure recovery path.

`GET /v1/config?player_id=UUID`, header `X-Install-Token: SECRET`. Response keys: `version`, `tuning` (PascalCase Tuning), `skill_model` (PascalCase PlayerModel), `ad_policy` (`interstitial_min_interval_seconds`, `interstitial_daily_cap`, `rewarded_daily_cap`), `experiments`. Cache ETag and send `If-None-Match`; 304 has no JSON. Apply tuning/model between attempts. Experiments do not depend on ad revenue or spend.

## Batched telemetry

`POST /v1/events`, headers `X-Install-Token`, `Content-Type: application/json`, optionally `Content-Encoding: gzip`. Maximum 500 events and 2 MiB both compressed and expanded. Batch is validated before any insertion, transactionally accepted; retry the **same event IDs**. Response `{"accepted":1,"duplicates":0}`. Duplicate IDs are immutable, including within a batch. Retry 429 using Retry-After and back off on 5xx. Discard/fix 400/422; do not retry unchanged.

```json
{"player_id":"UUID","events":[{"event_id":"UUID","kind":"session_start","occurred_at":"2026-11-14T12:00:00Z","data":{"session_id":"UUID"}}]}
```

`data` is exactly the following for each kind (all listed fields required):

| kind | data |
|---|---|
| session_start | `{"session_id":"UUID"}` |
| session_end | `{"session_id":"UUID","duration":60.5}` (seconds) |
| level_start | `{"LevelIndex":3,"Variant":0,"Plan":{"variant":0,"extraMoves":0,"freeHintGlow":false,"reason":"baseline","moveLimit":20}}` |
| level_end | Exact `JsonUtility.ToJson(AttemptRecord)` object below |
| booster_used | `{"level":3,"booster":"hint"}` |
| ad_offer / ad_shown / ad_reward | `{"placement":"rewarded_continue","network":"admob"}` |
| ad_failed | `{"placement":"rewarded_continue","network":"admob","code":"no_fill"}` |
| ad_paid | `{"placement":"rewarded_continue","network":"admob","value_micros":1234,"currency":"USD","precision":"estimated"}` |
| iap | `{"product_id":"coins_small","value_micros":990000,"currency":"USD"}` (analytics only, no entitlement grant) |
| settings_changed | `{"sound":true,"haptics":false}` |
| tutorial_step | `{"step":1,"completed":true}` |
| error | `{"code":"board_load_failed","context":"level_start"}` |
| unknown future kinds | `{"metric_count":5,"metric_success":true}`; only scalar numeric/bool/null fields beginning `metric_`, max 32 |

`ad_paid` must come from AdMob's paid event callback. Precision is `unknown`, `estimated`, `publisher_provided`, or `precise`. Currency is three uppercase letters; no conversion or cross-currency sums. Log rewarded `ad_offer` and `ad_reward` once each, and use a `rewarded_` placement prefix to distinguish rewarded placements. These are client-reported analytics, not payment verification.

Attempt data, preserving Unity's casing and enum ordinal (`Win=0`, `Lose=1`, `Quit=2`):
```json
{"Version":2,"Variant":0,"TuningVersion":"v2.1","ModelVersion":"sim-logistic-v1","Features":{"Moves":[{"Useful":true,"Undo":false,"ThinkSeconds":2.5,"LockedTouches":0}],"Count":1,"Useful":1,"Think":2.5,"Locked":0,"Undo":0},"ModelPrediction":0.7,"SkillEstimate":1000,"SkillConfidence":0.1,"LevelIndex":3,"StartedUtc":"2026-11-14T12:00:00Z","Plan":{"variant":0,"extraMoves":0,"freeHintGlow":false,"reason":"baseline","moveLimit":20},"LevelRating":900,"RatingBefore":1000,"RatingAfter":1005,"Result":{"Outcome":0,"MovesUsed":12,"Seconds":45.5,"BoostersUsed":0,"Continues":0}}
```
Attempt strings must be populated (not null). Aggregated early features are recomputed from at most 32 moves. ThinkSeconds capped at 30 and LockedTouches at 10. Most counters capped at 1,000,000, levels at 100,000, variants at 100, boosts/continues at 100, rating at 2,500, probabilities at 1, duration at one day, monetary micros at 10^12. Negative signed values in unsigned counters are rejected; other numeric values clamp to nonnegative. Timestamps older than 400 days or over five minutes ahead are rejected. Do not send logs containing personal data.

## Account, link and saves

Use a **Firebase ID token**, not a Google/Apple OAuth token, in `Authorization: Bearer TOKEN`. Anonymous Firebase tokens work too. Refresh using Firebase SDK on 401.

`POST /v1/link`, also `X-Install-Token`, body `{"player_id":"UUID"}`. Response `{"player_id":"canonical UUID","experiments":{...}}`. Replace stored player_id with the response; keep install_token. Link after anonymous auth, and again after upgrading/signing in. Existing accounts merge guest history and maximum progress. Canonical experiments and consumables win; guest consumables are not added, preventing duplicate rewards.

`GET /v1/save` returns `{"revision":0,"data":null}` if absent, otherwise the full save. To write, `PUT /v1/save`:
```json
{"revision":0,"data":{"highest_level":4,"stars":{"0":3,"1":2},"workshop":{},"settings":{"sound":true,"haptics":true},"skill":{"rating":1000,"games":2,"recent":[{"won":true,"efficiency":0.8,"seconds_per_move":2.1,"boosters":0,"continues":0}]}}}
```
Send the revision last read; server increments it on success. A `409` returns current `revision` and `data`; refresh mutable state, merge pending progress and retry. Never blindly replay old consumable state at a fresh revision. Progress/stars use maxima; settings/skill use the accepted revision. Inventory and lives are ignored on input and omitted on output; use the wallet exclusively. Workshop is empty. Revision equality is required even for a replay; a lost successful response is resolved by GET. Recent samples max 8. Economy state is accessed only through wallet routes.

`GET /v1/account/export`: NDJSON (`table`, `row`), including installs, assignments, saves and retained events. Never export credentials. `DELETE /v1/account`: hard-deletes backend rows and returns `receipt_id`, `deleted_at`, `scope:first_party_backend`, `deleted:true`. After success delete the Firebase user with the client SDK, clear local account/install credentials and reset to a new guest. Google/Apple identity and Firebase Analytics data are separate systems; the owner must implement their deletion workflow before release. Apple sign-in token revocation must also be handled by the identity integration. This endpoint alone does not delete those external records.

When an already linked Firebase anonymous user signs into an existing account with a different UID, preserve its ID token before sign-in and supply it in `X-Previous-Firebase-Token` on `/v1/link`. The server verifies both tokens and requires the previous token to be from the anonymous provider and to own the source player. Omit it when UID is unchanged or the install has never linked. Expired previous tokens cannot authorise a merge; refresh before switching identities.

## Economy v1 (canonical gold-bar wallet)

All wallet and purchase routes require `X-Install-Token`. GET uses `?player_id=UUID`; POST includes `player_id`. Optional `Authorization: Bearer FIREBASE_ID_TOKEN` performs the existing link flow first, and responses contain the canonical wallet player_id. Preserve X-Previous-Firebase-Token when switching from anonymous accounts as described above. Link explicitly **before** purchasing and set StoreKit's `appAccountToken` to the returned canonical UUID. Never send admin credentials. Wallet responses are no-store. Gold/inventory here are authoritative for economy acquisitions; cloud-save inventory cannot overwrite them. Apply returned effects once per idempotency_key; consume calls authorise booster/life usage during gameplay.

`GET /v1/wallet?player_id=UUID` returns this wallet shape (also includes lives_updated_at as described below) (inventory initially `{}`, unlimited_until nullable):
```json
{"player_id":"UUID","gold":500,"inventory":{"hint":3,"undo":3,"unclamp":3},"lives":5,"unlimited_until":"2026-11-15T12:00:00Z","remove_ads":false}
```
Gold is signed integer gold bars and can be negative after a refund; disable spend when balance is insufficient. Timestamp is UTC RFC3339. Entitlement remove_ads suppresses interstitials only; rewarded ads stay available.

`POST /v1/wallet/spend`:
```json
{"player_id":"UUID","idempotency_key":"UUID","reason":"continue","ref_id":"LEVEL_START_EVENT_UUID","booster":null}
```
Allowed reason: `continue`, `refill_lives`, `booster`. For continue, ref_id is the level_start event_id already acknowledged by ingestion; cost is ceil(60 × 1.5^previous_continues_on_that_ref), maximum 20. For refill_lives, ref_id is a client action UUID (90 gold, sets lives to 5). For booster, ref_id is an action UUID and booster is exactly `hint`, `undo` or `unclamp` (40 gold, adds one). Booster may be omitted for other actions. Response:
```json
{"wallet":{"player_id":"UUID","gold":440,"inventory":{},"lives":5,"unlimited_until":null,"remove_ads":false},"effect":{"extra_moves":5,"attempt_id":"LEVEL_START_EVENT_UUID"}}
```
`effect` is null for non-continue sinks. Same key and same fields returns the current wallet and the same effect with no new debit. Do not apply the effect twice. Key reused for a different action or insufficient balance gives 409 `{"error":"idempotency key reused"}` / `{"error":"insufficient gold"}`. New continue purchases need a new idempotency_key but the same attempt ref_id.

`POST /v1/wallet/grant` uses the same request shape. `daily_gift`: ref_id is an action UUID, 20 gold once per UTC day. `level_reward`: ref_id is an acknowledged winning level_end event_id from the last day, 5 gold once/event, max 20 rewards per UTC day. `booster` omitted/null. Response `{"wallet":WALLET}`. Server chooses the amount; arbitrary gold or starter-pack grant requests fail 400. A previously claimed daily gift with a new key returns 409. Reward telemetry remains client-reported; install auth is not gameplay attestation.

`POST /v1/iap/apple`:
```json
{"player_id":"UUID","signed_transaction":"STOREKIT_2_JWS"}
```
Response `{"wallet":WALLET,"transaction_id":"APPLE_TRANSACTION_ID"}`. Finish StoreKit transaction only after 200; retry the same JWS after transport/5xx failures. Invalid signature/chain/bundle/environment/product/appAccountToken returns 400. A consumable transaction already owned by another wallet or repeat starter pack under a distinct original transaction ID returns 409. Product IDs: `com.orcub.puzzle.starter_pack`, `.gold_small`, `.gold_medium`, `.gold_large`, `.gold_huge`, `.remove_ads` (all suffixes use the full prefix). Read gold amounts, US reference prices, booster codes and sink costs from config.economy; present StoreKit's localised price, never fabricate a regional price from USD.

`POST /v1/iap/restore`:
```json
{"player_id":"UUID","signed_transactions":["STOREKIT_2_JWS","STOREKIT_2_JWS"]}
```
At most 100 transactions; each is verified and applied independently. Response `{"wallet":WALLET,"processed":1,"results":[{"index":0,"ok":true,"transaction_id":"123"},{"index":1,"ok":false,"status":400,"error":"invalid Apple signature"}]}`. Results preserve input order; processed counts successes including idempotent replays. Retry only failed items. Non-consumable entitlement ownership follows `(environment, originalTransactionId)` to the latest presenting player, even a new guest with a different appAccountToken. Transfers have a 24-hour cooldown (429), are audit-logged, and revoke the old wallet's entitlement. Already-granted starter gold/boosters/unlimited time are never reissued on transfer. Consumables always require the current appAccountToken or a merged alias and never replay credits. Quantity must equal 1. Online checks skip recorded IDs, run four at a time, and share an 18-second deadline; timed-out items report 503.

Server callback only: `POST /v1/iap/apple/notifications`, body `{"signedPayload":"APPLE_V2_JWS"}`, no install token. Response `{"accepted":true}`. Verifies outer JWS plus inner transaction on REFUND/REVOKE/REFUND_REVERSED; Apple should retry failed deliveries. Other verified notifications acknowledge without mutation. Do not call this from Unity.

Config gains the following keys; existing tuning, skill_model and experiments are unchanged by monetisation:
```json
{
 "economy":{"version":"economy-v1","currency":"gold_bars","products":[{"id":"com.orcub.puzzle.gold_small","kind":"consumable","price_usd_cents":99,"gold":100}],"boosters":["hint","undo","unclamp"],"sinks":{"refill_lives":{"gold":90,"lives":5},"continue":{"gold":60,"moves":5,"multiplier":1.5},"booster":{"gold":40}},"grants":{"daily_gift":20,"level_reward":5,"level_reward_daily_cap":20},"offers":{"starter_min_level":10,"duration_hours":48,"repeat_cooldown_hours":24}},
 "ad_policy":{"interstitial_enabled":true,"interstitial_min_level":8,"interstitial_wins_only":true,"interstitial_min_interval_seconds":150,"interstitial_min_levels_apart":3,"interstitial_session_cap":6,"interstitial_daily_cap":6,"rewarded_daily_cap":20},
 "player_value":{"ltv_iap":{"USD":1990},"ltv_ads":{"USD":1234},"payer":true,"remove_ads":false,"sessions":1,"days_active":1,"rewarded_acceptance":0.5,"interstitial_churn":0.0,"highest_level":10},
 "offers":[{"id":"starter_pack","product_id":"com.orcub.puzzle.starter_pack","expires_at":"2026-11-17T12:00:00Z","repeat_after":"2026-11-16T12:00:00Z"}]
}
```
Products array contains all six products (example abbreviated). ltv_iap uses Apple milliunits per currency, ltv_ads uses AdMob micros per currency; maps can be empty. These are retained-event activity/revenue estimates, not settlement totals. Null/zero denominator rates are returned as 0 in player_value. Offers can be `[]`; honour expires_at and persist repeat_after locally. Eligible config at level >=10 returns an offer with nullable expires_at/repeat_after until shown. Call `POST /v1/offers/shown {"player_id":"UUID"}` when actually presenting it (same install/auth headers). The first acknowledgement starts the catalog's 48-hour window; later acknowledgements enforce its 24-hour cooldown. The acknowledgement returns `{"accepted":true,"expires_at":"UTC RFC3339","repeat_after":"UTC RFC3339"}`; persist these absolute timestamps. GET config never starts an offer window. Config ETag includes personalised policy/offers and varies with player state; refresh between sessions/attempts and after purchases. The ad_policy is an override; never derive difficulty from player_value.

Use `interstitial_` placement prefixes for interstitial shown/paid events, `rewarded_` for offers/rewards/paid. Default policy requires all gates; payers/Remove Ads disable interstitials, rewarded acceptance >=0.6 lowers cap to 3/240s, churn >=0.25 lowers cap to 2/300s. Rewards remain available in each case. Apply no policy change mid-attempt.

Link merge: guest grant gold is excluded from portable value. Portable gold = current negative balance if in debt, otherwise max(0, current gold - cumulative daily_gift/level_reward credits). If any such grants exist, guest booster inventory and unlimited time are also excluded, preventing conversion of farmed grants into portable items. This deliberately conservative rule can forfeit mixed-source guest inventory; purchase entitlements still move. Without grants, remaining purchased inventory and unlimited time transfer. Canonical lives win; free refill lives never merge. A zero/negative merge adjustment records excluded value without rewriting historical balance_after. Keys/purchases remain deduplicated. Daily caps count all merged history, so linking never resets them. Direct linking without a canonical account keeps its wallet and existing daily caps. Grant limits: one daily gift and 20 winning-event rewards per player/install per UTC day. Telemetry is not attestation; DeviceCheck/App Check remains future work.

`POST /v1/wallet/consume`:
```json
{"player_id":"UUID","kind":"booster_hint","count":1,"attempt_ref":"ATTEMPT_UUID","idempotency_key":"UUID"}
```
Kinds: `booster_hint`, `booster_undo`, `booster_unclamp`, `life`; count 1–100. attempt_ref is a client attempt UUID for audit correlation. Response `{"wallet":WALLET}`. Atomic under the player lock; insufficient inventory gives 409, never negative. Exact replay returns the current wallet without another debit; changed fields with the same key give 409. Spending gold on a booster adds inventory, consuming subtracts it. Authorise gameplay only after success; retry transport failures with the same key. Offline usage cannot be trusted/authorised by a local save. Unlimited lives make life consume an audited no-op.

Wallet includes `lives_updated_at` (UTC RFC3339). Regeneration: n=floor((server_now-lives_updated_at)/1800 seconds); lives=min(5,lives+n). Below 5 advance the anchor by n*1800 seconds; when reaching 5 reset it to server_now. Consuming from 5 starts the timer now, consuming below 5 preserves fractional progress. Refill sets 5 and resets the anchor. Mirror for display, then reconcile with server responses. SaveData omits inventory/lives; never apply them from saves.

Production accepts Apple-signed Production and Sandbox purchases/notifications. Both grant wallet benefits. Purchases and associated ledger rows retain environment; only Production contributes to ltv_iap, payer, and admin IAP revenue. Sandbox Remove Ads still suppresses ads. Xcode unsigned testing requires APP_ENV=development, APPLE_ALLOW_XCODE=true, APPLE_ENVIRONMENT=Sandbox; unsafe combinations refuse startup. Refund reversal restores benefits idempotently. Consumption-request notifications are acknowledged without automatic submission of unverifiable gameplay data. Default interstitial caps are 6/session and 6/day, rewarded 20/day. Player value caches for up to one hour (purchase mutations invalidate it); ad_offer means button shown, not button tapped. Churn requires no session restart within five minutes after an ad-adjacent end.

## Daily loop (v2)

POST /v1/wallet/grant accepts daily_puzzle or daily_gift with player_id,
idempotency_key, ref_id (UUIDs), and utc_day (exact yyyy-MM-dd). Booster must be
absent/null. Authentication, canonical ownership, rate limits and no-store match
the other wallet routes. Client deterministic UUIDs use the first 16 SHA256 bytes
of UTF8 orcub/daily/v1/<reason>/<date> via .NET Guid(byte[]). The server deduplicates
by canonical player/reason/date regardless of those UUIDs. A reused ledger key
with a changed request returns 409; a date replay returns the original daily
receipt and current wallet. No client amount, streak or freeze is accepted.
utc_day is rejected on spending and non-daily grants; absent utc_day preserves
legacy 20-gold daily_gift and its UTC cap. Legacy and v2 share durable caps,
including backfilled historical gifts and merged histories.

Response: {"wallet":WALLET,"daily":{"utc_day":"2026-11-15","reason":"daily_puzzle","streak":3,"freeze":false,"reward":{"gold":30,"inventory":{},"unlimited_minutes":0}}}.
Unity must require daily.utc_day on success and refresh balances from wallet.

The database UTC date is authoritative. Future dates return 409. New gifts must
be today's date; old receipts remain replayable indefinitely. Puzzle claims
require owned daily_puzzle_end telemetry with metric_success=true, metric_day
equal to the epoch-day ordinal, and server receipt on that day or the next.
Unknown-kind numeric metrics remain in raw_events. Flush telemetry before retry;
missing evidence returns 409 daily completion pending, or 410 daily completion
expired once outside RETENTION_DAYS. Evidence is client-reported, not attestation.
New puzzle claims must follow date order; a missing earlier day cannot be
inserted behind the last awarded day.

Consecutive puzzle days advance streaks; exactly one skipped day can consume a
freeze, with no completion or increment for the skipped day. Longer gaps reset
to one. A freeze is earned once ever on first reaching seven. Rewards at streak
3/7/14/30 are 30 gold / one hint / two undo / 100 gold. Gifts rotate by epoch
ordinal modulo three: 20 gold / one hint / 30 minutes unlimited lives. Unlimited
time stacks from max(now, existing expiry). All rewards, including zero-gold
awards, write the ledger and durable receipt atomically. Guest daily_puzzle and
daily_gift grants are excluded from portable merge value, including booster and
unlimited awards. Histories union by reason/date with canonical receipts winning;
streaks rebuild without minting rewards or summing streak integers.

SaveData.daily is an optional DailySaveSection v1 PascalCase JSON object.
Unknown fields fail validation. Version must be 1; dates must be empty or exact
keys; Completed/PendingPuzzleGrants are unique bounded arrays (3660), with pending
a subset of completed. Counters/timestamps are nonnegative, WinStreak<=3,
BonusRotation<=2, strings<=128 bytes. Accepted revisions preserve the section
except authoritative PuzzleStreak/Freeze/FreezeEarned/LastPuzzleDay reconciliation.
Stale revisions return server mutable state; clients union completion/pending
IDs when retrying. This backup never grants wallet value. NotificationsEnabled
and permission/scheduling metadata are backup data only: restore must preserve
device-local notification opt-out and require local OS permission.

Account exports include daily_claims and daily_streaks. Account deletion cascades
both; telemetry retention never removes claim tombstones.
