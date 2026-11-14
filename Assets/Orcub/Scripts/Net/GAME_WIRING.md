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
