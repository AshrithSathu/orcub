# Daily client integration followups

Changes required outside this implementation scope:

- In Net/SaveSnapshot, capture the current DailySaveSection as PascalCase JSON under SaveData.daily; on accepted/conflict/GET save responses, restore the reconciled section through DailyPersistence before replacing DailyServices.
- In Net/AccountService account switching, preserve the guest daily section until link succeeds, union Completed and PendingPuzzleGrants into the canonical section, then rebuild DailyServices for the canonical ID. Keep pending dates as the LastPuzzleDay maximum when applying a cloud section.
- The boot/resume composition owner in Game must call DailyServices.OnAppOpen on both boot and foreground resume (after ClientServices is ready). This now flushes and retries pending puzzle rewards.
- After each successful Net/ClientServices.Flush, notify the current DailyServices to retry pending rewards. Avoid recursion: DailyPuzzleService guards concurrent/reentrant retries before invoking Flush.
- Device/Firebase save, account-switch and foreground lifecycle flows need end-to-end validation after these changes land.

Policy: evidence must arrive on the completion date or the following UTC day. Missing proof after that window is permanently expired. This preserves the existing telemetry policy.

Validation blocker on 2026-11-15: shared MCP returned no_unity_session and instances showed zero connected editors. Refresh(if_dirty) timed out waiting for readiness. Logs/Editor.log reported Game compilation errors (LoaderScreen.SetProgress, MechanicIntro, LevelHud.ShowMechanicIntro/SetBoss, ShowPause.onInfo). Those files belong to the Game agent; resolve and reconnect MCP, then rerun all EditMode tests and reopen Assets/Scenes/Game.unity.

## Integrated in review h

DailyRuntime now owns composition and the gateway hooks above. Net captures/restores the daily section and preserves pending/completed dates on merge; successful telemetry flushes trigger retry. Account-scoped DailyWalletClient rejects stale-account continuations. Device lifecycle validation remains pending; see review-h-resolution.md.
