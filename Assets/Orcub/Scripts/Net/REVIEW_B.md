# Review b implementation and integration handoff

## Account deletion

`DeleteAccount(): Task`, `Changed` and `Failed(string)` remain compatible. Google users acquire a fresh native Google credential and call Firebase ReauthenticateAsync before backend DELETE. Cancellation or reauthentication failure leaves backend data intact. After backend success, Firebase deletion failure records the UID, signs out, clears account data and boots a new guest. The matching UID is retried before linking on its next authenticated session. Pending cleanup markers are deliberately preserved across the account-data wipe. New `IdentityDeletionDeferred` event means local deletion completed but external Firebase cleanup remains; do not show the generic failure sheet for this event.

Google disconnect is requested before reset; failed/time-out revocations retain a retry marker. Firebase Analytics data is reset. The new native cleanup plugin is under Net; the existing native plugin is patched in the generated iOS project by the build postprocessor for atomic Keychain update and a non-null Google result. An actual signed-device deletion/revocation round trip remains unverified.

## Wallet and synchronous board API

`BoosterInventory.Count/TrySpend`, `Lives.Count/TrySpend/Refill` retain signatures and delegate to the Store wallet. `Add` no longer grants local boosters. Refill requests the server's 90-gold sink asynchronously; it does not fabricate a local refill.

Conflict rule: save snapshots neither capture nor apply inventory/lives. A wallet response replaces its cache; outstanding consumes retain their UUID keys, subtract reservations for display, and replay once connectivity returns. HTTP 400/409 rejects the reservation and refreshes the wallet; never replay a stale cloud balance. Linked canonical balances win. The server's 1800-second regeneration formula drives display, including preserved fractional progress and unlimited lives.

**Owner decision:** the contract explicitly requires server acknowledgement before gameplay authorisation. The synchronous bool API therefore returns false while reserving/queuing, and true on retry after acknowledgement. Offline requests remain queued; offline gameplay use is not authorised. The Game owner must add a pending/retry presentation or await an asynchronous adapter before repeating TrySpend. `await StoreRuntime.Consumption.Spend(kind)` provides that asynchronous adapter (`life`, `booster_hint`, `booster_undo`, `booster_unclamp`). This preserves the contract but changes first-tap behaviour. It must be resolved before release. Acknowledged reservations belong only to the active attempt; abandoned attempts do not refund server usage. Attempt UUIDs are rotated from level_start even with analytics denied.

## Consent and telemetry

Firebase Analytics and Crashlytics default off in the iOS plist. Collection and explicit Firebase events remain disabled until successful UMP resolution permits storage/measurement; errors/timeouts do not grant consent. Required-consent regions use TCF purpose bits 1, 7, 9 and 10; NotRequired permits collection. Privacy-options completion updates the gate. No pre-consent gameplay/ad/revenue/session analytics enter the backend queue. Previously queued optional events are held while consent is unresolved.

Pre-consent traffic: first-party install registration (random install ID and the contract's app/build/device/screen/locale/timezone metadata), config, authentication/link/save and wallet/purchase requests needed for requested functionality, UMP privacy resolution, and bounded operational `error {code,context}` events. No Firebase events or Crashlytics reports. Continue/level-reward sinks that require acknowledged gameplay telemetry are unavailable before consent; no gameplay proof is fabricated to bypass this gate. Operational Unity errors are rate-limited to one/minute, 20/session. The queue loads and writes on workers, coalesces disk snapshots, and retains stable event IDs. Background suspension may interrupt the final asynchronous write; device durability remains unverified.

Autosave skips identical acknowledged snapshots and spaces changed writes by at least 30 seconds. Sign-out/deletion hold the account gate. Stopped-client checks discard old registration/link/config continuations; queue stop drains its writer before a new queue is booted. Only orcub.* preferences are removed; consent and Ads.FirstLevelCompleted remain. Backend 401 renews install credentials once and relinks; invalid event batches are isolated one event at a time.

Editor/development builds use http://127.0.0.1:8081 by default. Set orcub.dev.api for a staging endpoint. Release builds use the production HTTPS endpoint. No live backend test was performed.

## UI hooks and remaining Game-owned changes

- **C14 remains Game-owned:** restore the BeginAttempt exception guard with a neutral variant-0 plan and a bounded error event. No Game file was edited.
- Call `AdService.RewardedOfferShown(placement)` exactly once when a rewarded button becomes visible, including sheet reopen. The old tap-time ad_offer emission was removed; **Game visibility hook remains required**. No interstitial ad_offer is emitted.
- `StoreService.MarkOfferPresented(offer)` is the existing presentation hook; it sends /v1/offers/shown and persists returned absolute dates. `AcknowledgeOfferPresented` is awaitable. Nullable eligibility dates do not start a window locally.
- Restore inspects per-item results, finishes successes only and retains failed transaction IDs for the next restore attempt.
- Backend ad policy takes precedence over global Remote Config. Wallet Remove Ads independently blocks interstitials; rewarded remains available. Backend enabled/session/min-level/spacing gates are applied.
- Mobile Notifications 2.5.0 is installed (Unity registry latest; minimum Unity 6000.0). The existing version-defined Orcub.Daily.iOS adapter is enabled. Supply `new Orcub.Daily.IosLocalNotifications()` to DailyServices as described in Daily/README.md. Build preprocessing disables launch-time permission, remote registration and push capability. Only NotificationService.ContinueOptIn requests permission. OS permission, delivery, quiet hours and account-reset scheduling need a device check.

C15 (owner's hashed AdMob test-device ID) remains an owner configuration requirement. No ID was invented.

## Test evidence

EditMode run 50dd6ecd8ef842d79f466db34ef055b2: 136 passed, 0 failed, 0 skipped (56.5 seconds). Final 138-test EditMode run is pending the coordinator-owned Unity restart/reconnection. Latest Net, Store and Store.Tests compile with Unity-bundled Roslyn; both build postprocessors also compile against the actual Notifications package sources; 11 managed Store/wallet tests pass under Unity-bundled Mono, including mixed restores and lost consume acknowledgements. Compilation was intermittently blocked by concurrent Game/Editor work; those files were not edited by this task.
