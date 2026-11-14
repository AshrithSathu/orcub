# Review f implementation

The source review has F1–F19 and three Infos; the table treats Infos I1–I3 as
items 20–22. Production SSV configuration and Firebase admin setup were already
completed by the owner before this work.

| Finding | Resolution |
|---|---|
| F1 | Tombstone authorizes retries only; every request deletes current matching rows. Re-link/delete regression test. |
| F2 | Carry only remaining reward value after chronological guest debits; never full spent receipts. |
| F3 | Owned pending nonces block only life spends, for ten minutes. Unknown/older nonces debit real balances. Editor simulation never tags. |
| F4 | 4096 consume acknowledgements; 40 recent confirmed nonces. Concurrent player indexes in separate non-transaction migrations. 30-day retention. Changed-only writes, two-second prefs flush, per-frame life counts and hash-based pruning. |
| F5 | Wallet GET does not change consume_floor; conflict refresh deferred until replay pass ends. |
| F6 | Committed launch authorization and remote flags are false. Build validation is callable directly and tested without an export. |
| F7 | Save and spend flush are best effort and bounded; unaccepted intent never triggers boot reauth or disables save/link. Cancel API and Settings confirmation property exposed. |
| F8 | Admin identity failure returns identity_deleted=false; client fallback proceeds after backend deletion. |
| F9 | Daily completion is essential consent-independent gameplay telemetry. 401 renews installation then retries once; only 400/410/422 permanent. |
| F10 | Link drains spends with a timeout and refuses to merge while any remain. |
| F11 | Key fetch cooldown 60 seconds; network fetch outside global lock; stale keys retained. |
| F12 | Signed permanent mismatches acknowledged; confirmation/reservation dates use explicit UTC. |
| F13 | Capture consent stamp stays local; denied optional capture is skipped and denial purges held optional events. |
| F14 | Chronological remaining-grant accounting preserves later purchased boosters and expired unlimited gifts. |
| F15 | README rollback floor/snapshot warning; startup logs unknown applied migration versions. |
| F16 | Tombstones expire after 30 days; nonce/window tables included in export. |
| F17 | SignOut(true) offers explicit unsynced-progress discard; default remains protective. Game owns presenting the option. |
| F18 | 400/422 and all 409 consume failures drop with one error event; transient statuses retained. |
| F19 | Keep one quarantine file; local reset deletes all matching corrupt files. |
| I1 / 20 | Historical refill exemption requires the consume's nonce; guest historical unlimited windows are removed rather than ported. New canonical benefit windows come from accepted wallet updates. |
| I2 / 21 | Request actual placement; allow supported rewarded placement forms. Failed nonce reservation no longer consumes a client slot; duplicate close-time slot recording removed. |
| I3 / 22 | Public APIs and required Game wiring documented in GAME_WIRING.md; Game changes remain with the concurrent owner. |

Boss rewards use the exact InfiniteChapter rule: index % 20 == 19, including
infinite chapters. Grants are once per event and once per level, with 5 normal /
50 boss gold. Bosses have a separate cap of 100 per UTC day. Sparse stars support
indices through 100000, matching the existing backend validator. Telemetry adds
gen_version and curated without changing existing PascalCase fields.

Validation: cargo fmt, clippy --locked -- -D warnings, 42 backend unit/database
tests and one API integration test passed against port 55432 only; cluster stopped.
Unity passed 191/191 on the previous compiled assembly, including the notification
validation test. The latest source validation is pending: concurrent Game edits
prevent compilation (BoardView missing MechSprites.Warm, ClientGateway.OpenApp,
LivesClock, and booster wiring). The tool can run the old assembly while these
errors exist, so that result is not proof of the final consent/caching changes.

Deployment 6e4e79c7-fd88-4502-a368-8a197657481b succeeded on Railway api.
Production healthz/readyz returned 200. Test guest level-19 first-clear reward was
50 gold, replay granted 0 and balance stayed 50. The test guest was deleted.
