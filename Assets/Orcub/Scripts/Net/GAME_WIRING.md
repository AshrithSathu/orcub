# Review D Game integration

No Game files were edited by this implementation.

1. BoardView.PlayLevel out-of-lives callback (currently around line 212), and BoardView.WithLife retry callback (currently around 927): prefer the awaitable result and stop on failure:
   ```csharp
   // Inside the existing ShowRewarded("refill_lives", ...) reward callback:
   async () => {
       if (!await Lives.RefillFromAd("refill_lives") && Lives.Count <= 0) return;
       PlayLevel(index); // PlayLevel path; use `if (Lives.TrySpend()) action();` in WithLife
   }
   ```
   AdService already applies the provisional refill before invoking onReward, so the legacy Lives.Refill + synchronous read remains safe. The API returns immediately after local persistence. Do not await network confirmation or charge gold in this callback.
2. Explicit gold button only: after confirmation of the displayed 90-gold price, `if (await Lives.RefillWithGold()) PlayLevel(index);`. Show pending state during this server request and a retry message on false. Do not call it from an ad callback.
3. The level_end telemetry hook now applies deferred policy automatically. For an attempt termination branch that bypasses BoardView.EndAttempt (currently lines 1034-1050), call `AdService.Instance?.EndLevel()` after recording the attempt result. RecordWin also calls this for wins; level_start marks policy deferral automatically. This ensures a queued policy is applied only between attempts.
4. Existing review B visibility hook still applies: call RewardedOfferShown when the rewarded button becomes visible, once per sheet opening. No extra pending/reservation UI is needed for TrySpend; it is optimistic and durably queues before returning true.

Keep existing AccountGateway error/deferred-deletion presentation. Deferred identity retry now preserves the current guest's local progress; it does not reset the install. A matching account revoke marker cannot revoke a different signed-in Google UID.

## Review f: win rewards and infinite progress

After emitting `level_end` through `ClientGateway.Event`, read
`ClientServices.Instance.LastLevelEndEventId` and call
`await StoreRuntime.ClaimLevelReward(levelIndex, eventId)`. The returned integer is
the new gold grant (5 for a first normal clear, 50 for a first boss clear, 0 on
replay). Boss means `levelIndex % 20 == 19`, including infinite chapters. The API
flushes evidence first; retry the same event ID if upload/claim fails. Show the
boss reward panel using the returned grant. Game owns wiring `LevelRewards.Live`.

`ConsumableGateway.HasPendingSpends()` reports the durable spend queue. Link
refuses to merge until it drains, preventing guest uses from charging account
inventory. `Account.SignOut(true)` explicitly discards unsynced progress after the
protective default fails. `Account.CancelReauthentication()` cancels the pending
native reauthentication prompt. An unaccepted delete intent never prompts on boot.

SaveSnapshot uses a sparse stars object through index 100000, including infinite
levels, and restores the full saved frontier. Level start/end telemetry adds
`gen_version` (0 curated, InfiniteGenerator.Version otherwise) and `curated`.
Settings can offer “Finish deleting your account?” when
`Account.DeletionNeedsConfirmation` is true; call `DeleteAccount()` only after the
player chooses to continue. Failed final saves can offer the explicit
`SignOut(true)` discard option. These Settings controls remain owned by Game.

### Review g contract (2026-11-15)
- First-clear gold requires every earlier zero-based level to have a reward marker (zero tolerance; no legacy save-based bypass). Accepted range: 0..99999. Players with historical gaps must replay/claim the gaps. Normal daily cap remains 20; boss cap is 3 per UTC day. These are telemetry safeguards, not proof of a genuine solve.
- ClaimLevelReward throws StoreHttpException for transient refusals: 409 `reward_pending` / `reward_sequence_pending` are retryable after event upload / earlier claims; 410 `reward_expired` / `reward_cap` are final for this claim. The runtime converts 410 (and invalid 400/422) into terminal `granted=0`, so the existing Game retry queue drops that claim. A capped first clear may be claimed with fresh evidence on a later day.
- All lives placements, including Game's `refill_lives`, normalise to `rewarded_lives`. Backend additionally accepts legacy `rewarded_refill_lives`.
- Google sign-in drains spends for up to five seconds BEFORE requesting credentials/switching Firebase users. A nonempty queue throws `guest_spends_pending`; present “Finishing your last moves…” and allow retry. Offline attempts stay on the guest. The previous anonymous proof is durably retained until /link succeeds; later Link calls reuse it. Before switching, `/v1/link/prepare` also mints a durable merge proof while Firebase still verifies the guest as anonymous. It is bound to the guest UID and exact install credential, stored alongside the old guest token, and sent in the existing previous-token header as `merge:...`. It survives Firebase token expiry. Merge/deletion revokes it; another install or permanent account cannot mint/redeem it. Lost /link acknowledgements recover through the install-bound canonical alias. Never log this preference or include it in diagnostics.
- Ambiguous deletion intent resumes once per boot without reauthentication, before link/save. Missing/disabled identity during interactive reauthentication continues deletion. Failures retain intent and block link/save for retry.
- consume/adgrants writes flush immediately on all platforms; focus loss, pause and quit flush other dirty preferences.
- Wallet responses omit historical applied keys unless GET sends `applied_since` (oldest pending spend creation time). Refresh supplies that bound; successful consume responses acknowledge the submitted item directly. Server replay remains idempotent after lost responses.
- Provisional ad lives remain for ten minutes OR until a successful server wallet refresh after that deadline, whichever is later. Offline/cached data cannot make them disappear at the deadline. The next successful refresh is authoritative; SSV still pending at that point may reduce the visible count, and tagged spends fall back to real lives under the existing server policy. Do not promise an unlimited offline refill or confirmed reward before SSV.
- Merge carries remaining first-clear gold once per level not already owned by the account. Gold-funded boosters remain classified as granted inventory, including partial grant funding. Refund debits no longer count as consuming granted gold. Life accounting remains honour-based (Info I1); no new stronger anti-cheat claim.
- Added integration/test seams: `AccountService.DrainBeforeSwitch(Func<Task>, Func<bool>)` performs the bounded queue barrier; `AdService.NormalizeRewardedPlacement(string)` canonicalises lives placements; `Wallet.OldestPending` supplies the refresh acknowledgement bound; `Wallet.LastConfirmedRefresh` tracks authoritative refresh time for provisional expiry. Existing public call signatures remain unchanged.
