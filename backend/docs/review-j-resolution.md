# Review j resolution

- J1 / review i H1: claims require a winning event for the claimed level. Earlier levels with retained evidence become deferred debt; missing evidence is skipped unpaid as the frontier advances. No missing event or dropped claim blocks later rewards. Skipped levels are not retroactively paid by late events (the simplest frontier policy).
- Economy decision: normal cap is 20 levels / 100 gold per UTC day, bosses remain 3 / 150 gold. Excess eligible wins remain deferred without expiry. Amounts and both caps come from economy.json. This bounds client-asserted gold while preserving over-cap earned debt.
- J2: canonical deferred rows already paid to the guest are deleted before carrying guest gold, preventing debt being paid twice.
- Level start/end schemas ignore additional fields, including nested attempt fields, while validating known types and bounds. Future level event timestamps and attempt start times are clamped to server now (including clocks 24h ahead). A rejected level_end stays queued for retry rather than being deleted by schema isolation.
- J-L2: only successful link responses consume the merge proof; 401/404 preserve it.
- J-L3/L4: SDK identity errors cannot prove backend deletion. Unconfirmed deletion retains its intent and local data. Boot with a missing/different identity refuses reset until the durable backend-success flag exists and otherwise requires recovery of the original account; it does not silently erase a deletion request. This deliberately favours retention over falsely reporting deletion. If original identity recovery is impossible, server/operator-assisted deletion is still needed.
- J-L5 decision: keep merge proof TTL and provider checks. A skipped merge leaves the guest backend record untouched, but the device switches to the canonical account and loses its recovery path. Its wallet, purchases and telemetry may remain orphaned until retention cleanup or operator-assisted recovery. No automatic carry on an invalid/expired proof.
- J-L6: Store tracks unlock confirmation by player ID and retries due unlocks independently of Game's legacy device flags. Switching players cannot suppress another player's grant; server receipts remain authoritative and idempotent.
- J-L7: transient daily reward failures back off exponentially (10, 20, 40…1280 seconds) instead of trying on every empty telemetry flush; successful queue completion resets backoff.

Game queue ordering, cap retries and first-clear evidence are owned by the separate UI agent. No Scripts/Game edits are part of this implementation.

Verification results are recorded in the implementation report. Unity was in Play Mode/transition and not ready; EditMode tests remain pending without stopping Play Mode or restarting the editor.
