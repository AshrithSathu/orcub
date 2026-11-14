# Opus review: daily backend, commit e914a98 (2026-11-15c)

Scope: `git show e914a98`, which covers `backend/src/economy/daily.rs`, the grant, merge and save hooks in `economy.rs`, `lib.rs`, `model.rs`, migration `0005_daily.sql`, `clients/unity-contract.md` and `docs/privacy-data-inventory.md`. I checked it against `backend/docs/daily-endpoints.md` and `Assets/Orcub/Scripts/Daily/`.

This was a read-only review. Nothing in the repo was edited and production was not touched.

**Verification.** I ran `cargo test --locked` against a throwaway Homebrew PostgreSQL 17 in the session scratchpad, with a separate `CARGO_TARGET_DIR`. All 26 tests pass, including the 5 new `economy::daily` tests.

I confirmed H1, M1 and L1 with a repro test in a **scratch copy** of the backend (outside the repo). It printed:

```
REPRO H1 today=day+2:   409 Conflict daily completion pending
REPRO H1 today=day+30:  409 Conflict daily completion pending
REPRO H1 today=day+300: 409 Conflict daily completion pending
REPRO H1b out-of-order: 409 Conflict daily history out of order
REPRO M1 guest puzzle reward: {"gold":0,"inventory":{},"unlimited_minutes":0}
REPRO M1 canonical after merge: inventory={} unlimited=null      (guest had hint/undo/unclamp 3 + 1h)
REPRO L1 v2 after legacy ledger gift: ok=true gold=40
```

M2 is already asserted by the existing test `daily.rs:478-492`.

**No double grant was found.** Requests with the same key or the same date, run in parallel or replayed, produce one ledger row and one receipt. Separately, one hole can grant a second gift on deploy day (L1).

---

## Findings (most severe first)

### H1. Permanent puzzle failures return 409, the same code as "retry later", and the client queue stalls at the first one
- **Server:** `backend/src/economy/daily.rs:143-150` (evidence) and `:22-24` (out of order).
- **Client:** `Assets/Orcub/Scripts/Daily/DailyPuzzleService.cs:95-97` and `DailyWalletClient.cs:20`.

**The problem.** Evidence must have `received_at::date` equal to `day` or `day+1`, and `events.received_at` is always `now()`. So once `today > day+1`, no evidence can ever arrive for that day. The server still answers `409 daily completion pending`, and keeps doing so for up to 395 days until it finally answers 410. `daily history out of order` is also permanent and also 409.

The client reacts badly to this:
- It throws on any non-200 response and never reads the `error` body.
- `RetryRewards` walks `PendingPuzzleGrants` in order and stops at the first exception.

So one unclaimable day at the head of the queue blocks every later daily-puzzle reward on that device indefinitely.

**Failure scenarios:**
1. A player finishes day D at 23:30 UTC on a plane and reconnects on D+2. The telemetry arrives with `received_at = D+2`, and D stays at 409 for good. D+1, D+2 and later complete locally, but their grants queue behind D and are never sent. The local streak keeps counting while the server streak never advances, so milestones 3/7/14/30 are never paid.
2. Device B holds a pending D4 while device A, or a guest merge, has already advanced `last_day` to D5. D4 returns `409 out of order` forever and blocks B's queue the same way.
3. The telemetry is lost: the queue is dropped, or the 10k/day event cap is hit. That ends the same way as scenario 1.

**Fix (server):**
- When `!evidence && today > day + 1`, return 410, for example `daily completion expired`, instead of 409.
- Return a distinct permanent status for out-of-order, for example `410 daily history out of order` or 422.
- Keep 409 for "not yet, retry" only.
- Decide whether offline completion should count. If it should, match evidence on `occurred_at` within `[day, day+1]` and accept `received_at` up to about `day+3`. The out-of-order rule plus per-date receipts already stop replay abuse.
- Add a test that `day+2` without evidence returns 410.

**Fix (client):**
- Parse the status code and `error`.
- On a permanent failure, drop that day from `PendingPuzzleGrants` and continue the loop.
- Only stop on a transient 409, 429 or 5xx.

### M1. Any guest daily-puzzle claim, even a zero-gold one, wipes the guest's *purchased* boosters and unlimited lives on sign-in
- **Location:** `backend/src/economy.rs:734-738`

**The problem.** The merge used to wipe the guest's inventory only when `sum(delta) > 0`. Now it wipes inventory and `unlimited_until` whenever the guest has **any** ledger row for `daily_gift`, `daily_puzzle` or `level_reward`.

Every daily-puzzle completion writes a ledger row, and on non-milestone days that row is zero gold. So a guest who bought the starter pack (3 hint / 3 undo / 3 unclamp / 1h) and then played one daily puzzle loses all of it when signing in.

Restore cannot bring it back. `restore_items` skips transactions that already exist in `purchases`, which were moved to the canonical account. The gold side is fine (`portable = gold - grants`); only inventory and unlimited are lost.

This all-or-nothing wipe already existed for the gift and level reward. This commit extends it to an action players take every day.

**Fix:** subtract only the amounts actually granted, and clamp at zero:
- Inventory: sum `receipt->'reward'->'inventory'` over the guest's `daily_claims` and subtract that per booster.
- Unlimited: subtract `sum(unlimited_minutes)` from `unlimited_until`.
- Level rewards and gifts are gold only, so the existing gold subtraction already covers them.

Add a merge test with a starter-pack guest who has one zero-gold puzzle claim.

### M2. A merged guest claim blocks the account's own claim for that date, but the guest's reward was thrown away, so the player gets nothing
- **Location:** `daily.rs:191` (union), `daily.rs:124-134` (replay), `economy.rs:727-738` (exclusion).
- **Already asserted by:** `daily.rs:478-492`, where the canonical claim "succeeds", the canonical ledger holds only the moved guest row, and gold is 0.

**Failure scenario.** A player on a new phone claims today's gift as a guest (for example, a hint), then signs in. The merge copies the guest receipt into the account and wipes the guest's value. The account's gift for today now replays the guest receipt: it returns 200 with `reward: {"inventory":{"hint":1}}` but credits nothing. The same applies to puzzle milestone days, such as a guest reaching streak 7 or 30.

The client shows a reward the wallet never got. That looks like a bug and will generate support tickets.

**Fix:** make value portable per date instead of all-or-nothing.
- For each guest `daily_claims` row whose `(reason, utc_day)` the canonical account has *not* claimed, port that receipt's reward.
- For dates the account already claimed, exclude the reward, as now.

The date union already makes this farm-proof: each canonical date counts once, however many guests are merged.

Simpler alternative: store merged-only receipts with `"credited": false` and zero reward, so replays never claim a reward that wasn't credited.

### M3. Rolling back after 0005 breaks startup
- **Locations:** `backend/src/main.rs:45`, `migrations/0005_daily.sql`

**The problem.** Migrations run at startup through `sqlx::migrate!().run` (sqlx 0.8.6), and missing migrations are not ignored. If production is rolled back to the previous image after 0005 is applied, the old binary fails with `VersionMissing(5)`. Railway then restart-loops it under ON_FAILURE ×10, so a rollback becomes an outage.

The same migration also does two things that need watching:
- It builds `events_daily_evidence` without `CONCURRENTLY`, inside the migration transaction. That blocks event inserts while the index builds; this is small today.
- It seq-scans `wallet_ledger` for the backfill.

**Fix:** pick one of these and document it in the README deploy section:
- Make the policy forward-fix only.
- Ship a release whose migrator uses `set_ignore_missing(true)` before migrations that cannot be rolled back.
- Take a PITR or snapshot before deploying.

### L1. A gift claimed through the old binary during the deploy overlap can be claimed again through v2
- **Location:** `daily.rs:124-138`

**The problem.** The v2 gift branch only checks `daily_claims`. The backfill runs when the new binary starts. During Railway's 15 s overlap, legacy `daily_gift` grants served by the **old** binary write a ledger row with no receipt. A v2 claim for the same UTC day then grants again. The repro shows gold going 20 → 40.

This is a one-time, 20-gold-sized hole, but it breaks "Legacy and v2 share durable caps".

**Fix:** in the v2 gift branch, also check today's `wallet_ledger` `daily_gift` rows with the same predicate the legacy branch uses. If one exists, write a receipt from it and replay it.

### L2. Old app builds, and sign-in, discard the cloud daily section
- **Locations:** `backend/src/model.rs:453-459` (`merge_progress`), `backend/src/lib.rs:493` and `:597-606`

**The problems:**
- `SaveData.daily` is absent for pre-daily clients. A save at the current revision from an older build on a second device stores `daily = None`, which erases the `Completed` / `PendingPuzzleGrants` backup. The contract says the server "preserves the section".
- On link, when the account already has a save, the guest's `daily` section is dropped. The guest's pending puzzle grants are also stored in PlayerPrefs under the guest id, so they are lost on both sides.

**Fix:**
- In `merge_progress`: if `self.daily.is_none()`, take `old.daily`.
- On link, union `Completed` and `PendingPuzzleGrants` from the guest into the canonical section.

### L3. Reconciled streak fields are stale or move backward
- **Location:** `daily.rs:200-208`

**The problems:**
- `save_fields` writes the stored `streak` even after the run has broken (`last_day < today-2`, or `today-2` with no freeze), so the cloud and restore show, for example, "🔥 12" weeks later.
- It overwrites `LastPuzzleDay` with the server's last *awarded* day, which lags behind days still pending locally. After an accepted save is applied, `DailyPuzzleService.Complete` computes the gap from the older day. That wrongly consumes the freeze or resets the local streak, which is display only; the server stays correct.

**Fix:**
- Return the effective streak, which is 0 once broken.
- Set `LastPuzzleDay = max(server last_day, max(PendingPuzzleGrants))`, or expose the server value under a separate field.

### L4. Client calls grant before telemetry is flushed, and nothing retries afterward
- **Location:** `DailyPuzzleService.cs:85-87`

**The problem.** `Track("daily_puzzle_end")` only adds the event to the queue. `RetryRewards()` runs immediately afterward, so the first grant always gets `409 pending`. `RetryRewards` has no other caller, so the reward waits until the next daily completion. With H1, that is often never.

This is a client wiring gap against the contract's "Flush telemetry before retry".

**Fix:**
- `await ClientServices.Flush()` before granting.
- Call `RetryRewards` on boot/resume and after each successful flush.

### Nits / informational
- `daily.rs:434`: the test comment says a long gap "resets even an unused freeze". The code keeps the freeze on gaps over 2 days, as the spec and client do. The test sets it false by hand. Fix the comment.
- `today` is `now()` at transaction start. A request that waits on the player row lock across UTC midnight uses the earlier day. This is harmless.
- Evidence is client-reported. Anyone with an install token can POST `daily_puzzle_end` with `metric_success=true` for today or yesterday and claim. This is documented and accepted; the ceiling is about 30 + 100 gold, 1 hint and 2 undo per 30-day cycle.
- `Assets/Orcub/Scripts/Net/SaveSnapshot.cs` (uncommitted) does not send `daily` yet, so the save-section contract is unexercised end to end.
- `save_get` writes: it inserts a `daily_streaks` row and runs regenerate. This matches the existing pattern, but a GET should ideally be read-only.

---

## Checked and found correct
- **Double grant, concurrency.** `lock()` takes `players … FOR UPDATE` before any read, so same-player claims serialize under READ COMMITTED. The `(player_id, reason, utc_day)` primary key and `UNIQUE(player_id, idempotency_key)` are backstops. The parallel test passes, and each failure path rolls back atomically: the CHECK-constraint test and the inventory-overflow test both leave the ledger and receipts unchanged.
- **Idempotency.** Keys are unique per player, so deterministic client keys shared across all players don't collide. Reusing a key with a different reason, date or ref returns 409. Date replay returns the stored receipt plus the current wallet. Merged `guest:KEY` rows match via the `LIKE '%:'` replay, with identical fingerprints.
- **Server date.** `(now() AT TIME ZONE 'UTC')::date` is authoritative and verified under `SET TIME ZONE America/Los_Angeles`. Future dates return 409. New gifts must be today. Strict `yyyy-MM-dd` parsing rejects `2026-1-01`, `2026-02-30`, timestamps and empty strings. No client streak, freeze or amount is accepted (`deny_unknown_fields` on `Action`).
- **Streak rules.** A gap of 1 increments. A gap of 2 consumes the freeze and increments. Larger gaps reset to 1. The freeze is earned once ever at the first 7. Milestones are 3/7/14/30. Gift rotation `rem_euclid(3)` matches the client `((n%3)+3)%3`. Unlimited time stacks from `max(now, expiry)`.
- **Merge.** Streaks rebuild deterministically from the sorted union, so integers are never summed and `freeze_earned` is ORed. Guest gold minus all daily/level grants is excluded. Link merges before reconciling saves (the reordering is correct). Events, installs and ledger move to the canonical account, and the guest is deleted, with claims and streaks cascading. Delete-and-recreate guests only produce fresh, separate wallets whose daily value can't be ported. I found no merge-farming path; M1 and M2 are both cases of excluding too much.
- **Authorization.** `owner()` requires `x-install-token` matching `player_id`, or Firebase auth that resolves to the canonical account. An unauthenticated grant fails (tested). One player cannot grant another.
- **Rate limits, no-store, injection, panics.** The routes sit under the existing IP/token/uid limiters. `/v1/wallet*` sets no-store. All SQL uses bound parameters; the only `format!` interpolates constant table names. No `unwrap` depends on input.
- **Export and deletion.** `daily_claims` and `daily_streaks` are in the export list and cascade on delete (tested). Retention deletes events only, so claim tombstones survive (tested).
- **Backfill.** One receipt per player and UTC date, using `DISTINCT` and `ON CONFLICT DO NOTHING`; tested over populated v1 data. The legacy branch counts both ledger and claims and writes a receipt.
- **Save validation.** Exact PascalCase field set, `Version == 1`, date formats, unique arrays capped at 3660, pending ⊆ completed, numeric bounds, strings up to 128 bytes. It matches `DailySaveSection` as `JsonUtility` serializes it (all fields always present, `long` timestamps). Stale-revision responses reconcile server state.
- **Contract.** The request field names and UUID formats match `DailyWalletClient`, `ref_id` equals `idempotency_key` equals `GrantKey`, and responses include `daily.utc_day` and `wallet`. The mismatches are the error semantics in H1 and L4.
