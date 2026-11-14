# Store and wallet client

Native Apple StoreKit 2 (iOS 15+, Swift 5 language mode), no additional UPM package. Unity 6000.6.9f1 already targets iOS 15. This small bridge uses `VerificationResult<Transaction>.jwsRepresentation`, `Product.displayPrice`, `appAccountToken`, `Transaction.updates` and `Transaction.unfinished` directly. This avoids receipt-format conversion and Unity IAP auto-confirmation differences: only this client's explicit backend-acknowledged `Finish` calls `Transaction.finish()`. See [Apple verification results](https://developer.apple.com/documentation/storekit/verificationresult) and [unfinished transactions](https://developer.apple.com/documentation/storekit/transaction/unfinished).

Existing dependencies: Newtonsoft JSON 3.2.2, Firebase Unity 13.17.1, Google Mobile Ads 11.7.0. No package versions changed.

## UI integration (no UI included)

`StoreRuntime.Service` boots automatically; wait until non-null. Subscribe/unsubscribe to `Changed` to redraw `Products` (`Id`, `Title`, `LocalizedPrice`) and `Offers` (backend objects). Product prices are exclusively StoreKit strings; missing products remain unavailable. Editor never simulates successful money purchases. Inject `IStoreKit`, `IStoreBackend`, `IStoreCache` for deterministic tests.

- `await Purchase(productId)`; `Purchased(productId)` means server accepted, wallet refreshed and StoreKit finished. `Pending(productId)` means approval/backend/transport is unresolved, and the transaction remains unfinished. `Failed(productId)` includes cancellation or unavailable product. Subscribe before calling. Never grant on a purchase button or Pending event.
- `await Restore()` calls AppStore.sync, submits current non-consumable entitlements atomically to `/v1/iap/restore`, refreshes wallet, then finishes. Failure emits `Failed("restore")`.
- `Wallet.Balance`, `Snapshot` (inventory/lives/unlimited_until), `RemoveAds`, `IsCached`, `Changed`; cached balances are display-only and scoped to the player. No offline spending/grants. Remove Ads feeds AdPolicy and suppresses only interstitials.
- `await Wallet.Spend(new SpendSink("continue", acknowledgedLevelStartEventId), actionUuid)` returns the backend effect (`extra_moves`, `attempt_id`). Server owns cost escalation (+5 moves at ceil(60*1.5^previous)), not UI. Use `refill_lives` with an action UUID, or `booster` with action UUID and `hint`/`undo`/`unclamp`. Retain exactly the same action key and fields after network failure; a new intentional purchase needs a new key.
- Apply a returned continue effect once to its matching live attempt, increment the gameplay continue counter, and never reset the attempt ID. Replay returns null, even after relaunch. The effect marker is persisted before returning: a crash in between may lose an effect, but cannot duplicate it. Gameplay integration must persist its own matching attempt state if recovery of that crash window is desired.
- `Offers` and `Economy` come from fresh `/v1/config`. Use `CanPresentOffer(offer, UtcNow)` and `MarkOfferPresented(offer)` to honour expiry and persist repeat_after per id/expiry. Do not invent offers when the array is empty. Refresh between attempts/sessions and after purchases. Wallet/config API does not alter adaptive tuning.

Before StoreKit purchase the client explicitly links any current Firebase identity and sends the resulting canonical player UUID as appAccountToken. Account changes must complete before opening purchase UI. Pending transactions are replayed on next launch/foreground using the StoreKit-owned JWS, with no JWS saved in PlayerPrefs or telemetry. HTTP install auth uses ClientServices.Request. Telemetry uses its existing durable queue with `iap_purchase`, `iap_failed`, `wallet_spend` and schema-compatible numeric/bool metric fields (no JWS or credentials).

## Device testing with a free Apple ID

The build post-process copies `Orcub.storekit` into the exported Xcode project, adds it as a project file, copies the Editor-only Swift source into the framework build, links StoreKit and enables Swift runtime embedding. It includes all six backend products, including non-consumable starter_pack and remove_ads. No iOS build was performed by this implementation.

1. Export iOS normally, open the xcworkspace, choose a Personal Team for signing and connect a Developer Mode iPhone. A paid membership is unnecessary for Xcode-local StoreKit testing.
2. Xcode: Product > Scheme > Edit Scheme > Run > Options > StoreKit Configuration > Orcub.storekit. Add the copied file to the project if an existing scheme does not list it. Run from Xcode; this setting belongs to the owner's scheme and is deliberately not enabled for release.
3. Check localised prices, purchase cancellation, Ask to Buy, interrupted purchase and restore using Debug > StoreKit > Manage Transactions. Relaunch to exercise unfinished delivery. Disable the configuration for App Store sandbox testing.

**Verification boundary:** Xcode-local StoreKit JWS is signed with Xcode test certificates, not Apple's App Store signing chain. The production backend intentionally rejects it. Therefore local device testing can exercise StoreKit products/prompts/pending delivery without a paid account, but cannot grant a production wallet. The fake HTTP EditMode tests prove server acknowledgement ordering and idempotency. Full device-to-server grants require Apple sandbox setup with a paid account, or an independently isolated test backend that trusts only an exported Xcode test certificate; no verification bypass is added to the production client/server.

## Review fixes

Assisted wins (continues or boosters) preserve Elo and the failure streak, and assisted samples cannot contribute pace/model rating evidence. Both ad and gold continues use the same counters. UMP completes before ATT; UMP has a 30s callback deadline and ATT a 10s wait. Interrupted/failed flows retry next foreground. ATT timeout permits contextual ads only when UMP allows requests. No rewards on unsupported platforms; Editor simulation stays Editor-only. Reward close waits 0.5s for a late earned callback, once. Daily UTC ad caps persist and are covered by a relaunch-policy regression test. Missing config disables ads with an error. An existing Podfile post_install hook now produces a warning to verify deployment targets.
