# Review B/D implementation handoff

Consumables are optimistic: TrySpend persists a UUID-keyed consume before returning true and subtracts pending uses locally. There is no first-tap reservation/pending UI. Unlimited life losses are no-ops. Requests carry occurred_at and retain keys through transport, 401 renewal, rate limits, and server failures; only explicit insufficient-inventory errors discard them. Guest-to-account linking migrates pending spends. Sign-out/deletion await an unthrottled save and best-effort queue drain before the scoped wipe.

Rewarded life refills use server-issued AdMob SSV nonces and immediate local provisional lives. Lives.Refill never charges gold. Explicit confirmed gold refills use Lives.RefillWithGold. See GAME_WIRING.md for exact Game integration, and backend/docs/admob-ssv.md for required console/credential steps.

Restore always submits all current entitlements; server idempotency protects consumables and permits Remove Ads reclaiming. Optional pre-consent events are held, while bounded operational errors and necessary first-party level proofs are sent without advertising identifiers. Cached consent survives account resets. Firebase collection remains gated.

DELETE is idempotent using hashed-subject tombstones. Configured Firebase admin credentials enable server-side identity removal; otherwise matching-UID client deletion remains the fallback. Deferred retries do not wipe a different current guest's progress. Google revoke markers bind to UID. Corrupt caches are quarantined and reset. Build patches fail on source drift; native cleanup plugin is iOS-only. Foreground refresh is spaced five minutes and linked UIDs are not repeatedly relinked. Ad policy is deferred during attempts; Game must call EndLevel on all terminal paths.

Editor and development builds retain the production API by default and label installs editor/ios-dev. Admin event analytics exclude them. Offline clocks remain untrusted; the server bounds consumption event times. D22 requires no frame cache at current queue sizes.
