# Daily wallet endpoint patch specification (not applied)

Base prerequisite verified: 2665625, `Backend: keyed rate limits, server-authoritative inventory, JWKS single-flight, bounded export + retention`. Backend source is concurrently owned; this task adds this spec only. Current production contract lacks daily_puzzle, a date parameter, rotating gifts and the cloud daily section. Do not enable daily wallet claims until this patch is applied, tested and deployed. No client fallback grants are permitted.

## Wire change

In src/economy.rs Action add `#[serde(default)] utc_day: Option<String>`. Include utc_day in request fingerprint only when Some, preserving existing ledger fingerprints for legacy sink/reward callers. Spend rejects Some(utc_day). Non-daily grants reject Some(utc_day). Daily grants require yyyy-MM-dd exactly (reject malformed dates and trailing time/offset); keep legacy daily_gift without utc_day behaving as v1 until clients migrate.

The new POST /v1/wallet/grant body is:

```json
{"player_id":"UUID","idempotency_key":"UUID","reason":"daily_puzzle","ref_id":"UUID","utc_day":"2026-11-15"}
```

reason is daily_puzzle or daily_gift; booster must be absent/null. The date and reason derive deterministic client UUIDs using the first 16 SHA256 bytes of UTF8 `orcub/daily/v1/<reason>/<date>`, interpreted by .NET Guid(byte[]). Server uniqueness must use parsed date, **not** trust/ref_id/idempotency key. Same key with different reason/ref/date conflicts. Same reason/date with any key returns success/current wallet with original receipt, without another grant (this also supports devices with different old local IDs). Return:

```json
{"wallet":{"player_id":"UUID","gold":30,"inventory":{},"lives":5,"unlimited_until":null,"remove_ads":false},"daily":{"utc_day":"2026-11-15","reason":"daily_puzzle","streak":3,"freeze":false,"reward":{"gold":30,"inventory":{},"unlimited_minutes":0}}}
```

All existing authentication, keyed rate limits, no-store handling, canonical ownership and owner lock remain. Do not allow client amounts or streak/freeze parameters. This client intentionally requires daily.utc_day on 200 and never fabricates a successful claim on v1.

## Migration (next available migration number)

```sql
CREATE TABLE daily_claims (
  player_id uuid NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  reason text NOT NULL CHECK (reason IN ('daily_puzzle','daily_gift')),
  utc_day date NOT NULL,
  receipt jsonb NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY(player_id,reason,utc_day)
);
CREATE TABLE daily_streaks (
  player_id uuid PRIMARY KEY REFERENCES players(id) ON DELETE CASCADE,
  last_day date,
  streak integer NOT NULL DEFAULT 0 CHECK(streak>=0),
  freeze boolean NOT NULL DEFAULT false,
  freeze_earned boolean NOT NULL DEFAULT false
);
```

Before enabling v2 gift claims, backfill prior ledger daily_gift rows into daily_claims using their UTC created_at day; row receipt records 20 gold. Group duplicate historical/merged claims into one receipt. This prevents a second gift on upgrade day. Ledger retention/export must not remove these durable claim tombstones.

## Replace daily grant branch algorithm

Inside grant, after owner resolution and `lock(&mut tx,id)` and before existing generic `replay`:

1. For daily_gift/daily_puzzle with utc_day Some, parse date. Reject dates after `(now() AT TIME ZONE 'UTC')::date`. Do replay lookup first for already claimed dates, even if old. Reject new gift claims unless date == server UTC today (409 daily date expired). A puzzle completion may finish the following UTC day; do not award arbitrary historical days based solely on a phone clock.
2. `SELECT receipt FROM daily_claims WHERE player_id=$1 AND reason=$2 AND utc_day=$3`. If present, return current snapshot plus stored daily receipt, with no mutation. First enforce any existing idempotency-key fingerprint check, so keys reused for a different action still return 409.
3. For new daily_puzzle, require a stored daily_puzzle_end event owned by the canonical player, with payload metric_success == true and metric_day == date's integer ordinal since 1970-01-01. received_at UTC date must be date or date+1. Let queued telemetry flush before retry. This is the same client-reported evidence boundary as level_reward, not attestation. Reject absent evidence 409 daily completion pending. If telemetry retention has expired, return 410 daily completion expired; UI should expose this rather than mint a local reward. Existing client leaves pending failure visible; parent may explicitly dismiss an expired reward.
4. For daily_puzzle, insert daily_streaks if missing. Read row FOR UPDATE. Reject a new puzzle day <= last_day (409 daily history out of order); the replay in step 2 has already handled duplicates. Require earlier pending client days to be sent in order. Compute day gap using SQL dates, never local timezone:
   - no last day or gap>2: streak=1;
   - gap=1: streak=old+1;
   - gap=2 and freeze=true: streak=old+1, freeze=false;
   - gap=2 without freeze: streak=1.
   If streak>=7 and freeze_earned=false set both freeze and freeze_earned true. Save last_day=date.
5. Compute daily_puzzle reward from server streak: 3 => 30 gold; 7 => inventory hint +1; 14 => inventory undo +2; 30 => 100 gold; otherwise 0. Milestones are reached within a streak; reset streaks can earn milestones again on new dates. Freeze is earned once ever, not once per reset.
6. Compute daily_gift reward from `((date - DATE '1970-01-01') % 3 + 3) % 3`: 0 => 20 gold; 1 => hint +1; 2 => 30 minutes unlimited lives. Set unlimited_until to `greatest(coalesce(unlimited_until,now()),now()) + interval '30 minutes'`. Never accept duration from client. For inventory use JSONB integer upsert, preserving other booster entries. Reject overflow before mutation.
7. Call existing `entry(tx,id,gold_delta,reason,ref_id,key,request)` exactly once, including zero gold for booster/unlimited awards, to retain the idempotency ledger. Insert daily_claims receipt and update daily_streaks in the same transaction. Return snapshot + stored daily receipt only after commit. Unique primary key plus the existing canonical owner row lock serializes parallel claims.
8. Legacy daily_gift must count both v1 ledger entries and v2 daily_claims for today's cap. Its successful claim must also insert today's daily_claims receipt. New v2 branches must run before the old fixed-gold daily_gift branch; other sinks/rewards keep existing behaviour.

## Account lifecycle and cloud section

Add daily_claims and daily_streaks to account export and delete coverage (FK cascade applies to both). On verified guest link under the existing two-player lock: union claim rows by canonical reason/date with canonical receipt winning overlap; preserve ledger aliases/keys using existing merge handling. Rebuild streak state from the sorted union of puzzle claim dates (same gap rules), preserve freeze_earned if either source earned it, and consume grace only once per skipped day. Do not add streak integers or grant duplicate milestones while rebuilding; existing wallet merge already carries granted balances. Both old histories remain cap evidence.

In backend SaveData (strict serde type) add optional `daily` JSON object, default absent for old clients. Accept only the exact PascalCase fields of DailySaveSection v1. Validate Version=1; date strings exactly UTC date keys or empty; Completed/PendingPuzzleGrants arrays bounded to 3660 and unique; pending entries must be completed; PuzzleStreak/WinStreak/BonusRotation/Sessions nonnegative, WinStreak<=3, BonusRotation<=2; notification timestamps nonnegative; strings bounded 128. Unknown fields rejected. Save/export this section unchanged at revision equality; it cannot overwrite wallet/streak grant tables. Populate cloud-visible PuzzleStreak/Freeze from daily_streaks on reconciliation rather than trusting client values. Keep server mutable daily state on stale revision; client replays only completion union/pending IDs. DailySaveSection local notification opt-out remains device-local on restore so another device's enabled state cannot authorise this device.

## Required backend checks before deployment

- Database tests: parallel same-date calls with different keys produce one ledger/claim and one award; replay same key after midnight/restart; wrong fingerprint conflicts; v1/v2 interop and backfill; future/old gift dates reject; puzzle evidence missing/present, midnight finish and expiry; 3/7/14/30 rewards; one-day grace once and two-day gap reset; 30-min interval stack; UTC date unaffected by server timezone/DST; canonical guest link overlapping dates; save old/new roundtrip and invalid bounded values; account export/deletion.
- Contract test asserts client response daily.utc_day and wallet exactly; no anonymous unauthenticated grants; all SQL mutations roll back together on failure.
- cargo fmt --check, cargo test, existing integration tests against isolated PostgreSQL. This spec has not changed or tested Rust source and does not claim deployment proof.
