# Unity contract v1

HTTPS only in production. JSON UTF-8, UTC timestamps RFC3339, UUIDs canonical strings. Unknown properties on known types are rejected. Store `install_token` in Keychain; never log tokens. Responses with credentials/saves are no-store. No IDFA, IDFV, names, emails, ad-unit IDs, receipt blobs, stack traces or free-form messages. Error/context/reason and all other string labels are bounded codes, never user input.

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
{"revision":0,"data":{"highest_level":4,"stars":{"0":3,"1":2},"inventory":{"hint":2},"workshop":{"theme":1},"lives":5,"settings":{"sound":true,"haptics":true},"skill":{"rating":1000,"games":2,"recent":[{"won":true,"efficiency":0.8,"seconds_per_move":2.1,"boosters":0,"continues":0}]}}}
```
Send the revision last read; server increments it on success. A `409` returns current `revision` and `data`; refresh mutable state, merge pending progress and retry. Never blindly replay old consumable state at a fresh revision. Progress/stars use maxima, inventory/workshop/lives/settings/skill use the accepted revision's complete snapshot. Revision equality is required even for a replay; a lost successful response is resolved by GET. Recent samples max 8. This is a cloud backup, not a cheat-resistant economy.

`GET /v1/account/export`: NDJSON (`table`, `row`), including installs, assignments, saves and retained events. Never export credentials. `DELETE /v1/account`: hard-deletes backend rows and returns `receipt_id`, `deleted_at`, `scope:first_party_backend`, `deleted:true`. After success delete the Firebase user with the client SDK, clear local account/install credentials and reset to a new guest. Google/Apple identity and Firebase Analytics data are separate systems; the owner must implement their deletion workflow before release. Apple sign-in token revocation must also be handled by the identity integration. This endpoint alone does not delete those external records.

When an already linked Firebase anonymous user signs into an existing account with a different UID, preserve its ID token before sign-in and supply it in `X-Previous-Firebase-Token` on `/v1/link`. The server verifies both tokens and requires the previous token to be from the anonymous provider and to own the source player. Omit it when UID is unchanged or the install has never linked. Expired previous tokens cannot authorise a merge; refresh before switching identities.
