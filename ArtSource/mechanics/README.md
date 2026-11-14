# Gear mechanics integration

Core implementation only. Artwork and Game/BoardView changes are owned separately. These rules are not live in the game until the gesture boundary and rendering integration below is wired.

## Rules and data

All new fields default to zero/false/null, so old JSON retains classic rules. Ring and link indices retain their old meaning. Colours are existing palette indices, including zero; `tinted` distinguishes a colourless clamp from palette colour zero.

| Mechanic | Definition | Sprite information |
|---|---|---|
| Ratchet | `RingData.ratchet`: +1 permits positive/anticlockwise degrees, -1 negative/clockwise, 0 either. The entire input delta is refused in the wrong direction. | Clear directional arrow and ratchet teeth; never rely on colour alone. |
| Linked pair | Two rings share a positive `gearGroup`; one input turns both by opposite equal angles. Either ring's owned active clamps, padlock or rust blocks both. Removing a free member disengages the pair. | Matching pair symbol and visible gear connection. |
| Colour clamp | `LinkData.tinted` plus `color`; gap entry releases only if target ring colour equals clamp colour. Classic clamps ignore colour. Ring colours never change. | Clamp tint plus matching palette symbol readable without colour. |
| Key/padlock | Positive `keyId` on a carrier and matching `padlockId` on another ring. Pulling the carrier unlocks rotation. A padlock restricts rotation, not pulling a ring with no links. | Key on carrier; closed/open lock on receiver; matching identifier. |
| Rust/oil | `rustAfter` is an absolute counted-move deadline. At that move a remaining ring freezes unless a carrier with matching positive `oilId` was pulled first (`rustOilId`). Late oil cannot thaw it; frozen rings can still be pulled if free. | Rust texture, remaining move count, frozen state; oil drop with matching identifier. |
| Spring | Positive `springMoves=K`: released clamp re-closes at the end of the Kth further counted gesture, unless either endpoint has already been pulled. Reclosure resets clay pass count. Release gesture does not consume a further move. | Spring coil, open/closed pose, remaining move count. |
| Clay coating | `clay=true`: two distinct gap entries release it. Staying inside the gap for many drag samples counts once; leave and return to count again. Two revolutions in one continuous drag are legal and cost one move. | Coating intact, chipped after one pass, bare after release. |
| Boss | `is_boss=true`, 14–18 rings and eight extra budget moves. | Intro/map badge; no additional piece artwork required. |

Author each positive gear group on exactly two rings. Pair directions must be compatible. Key/oil identifiers are positive; zero means absent. The generator only publishes colour clamps matching their target and verifies dependencies using the solver. Hand-authored mismatched colour clamps correctly make their board unsolvable.

## Game layer API

Construct `PuzzleModel(data.ToDef())` as before. Read metadata from `LevelData.mechanic_intro`, `is_boss`, and `mechanics` (also carried into `LevelDef`). Intro keys are `mechanic_intro.ratchet`, `.linked`, `.colour_clamp`, `.key_padlock`, `.rust_oil`, `.spring`, `.clay`, `.mix`; localisation remains a game-layer task.

- Per ring: `Rings[id]` exposes static mechanic identifiers, `GapAngle`, `Removed`, `Frozen`. `CanRotate(id)` includes pair locks, padlocks and rust. `LinkedPartner(id)` returns the active mate or -1. `IsPadlocked(id)` is the current lock state. `DirectionAllowed(id, delta)` checks that ring's ratchet; `Rotate` checks both members.
- Per clamp: `Links[id]` exposes `Tinted`, `Color`, `Clay`, `Passes`, `InGap`, `SpringMoves`, `ReleasedAt`, `Active` plus the existing geometry. Rust countdown is `max(0, RustAfter-model.Moves)`; spring countdown is `max(0, SpringMoves-(model.Moves-ReleasedAt))` after committing the release gesture.
- Input: capture before a gesture, send all drag samples to `Rotate`, and call **`CompleteMove()` exactly once after every counted gesture**, including failed/locked gestures. Do not call it per frame or per angle sample. Tap input must respect ratchet direction on both geared rings. `WrongDirection` means reject and shake; do not silently reverse the player's drag. Zero deltas do not pass a clamp.
- Pull through `TryRemove`; pulls do not consume moves. Oil is collected on pulling its carrier, not merely releasing one of its clamps.
- Events: existing `LinkReleased`, `RingRemoved`, `Solved`; new `KeyCollected(Ring)`, `OilCollected(Ring)`, `RingFrozen(Ring)`, `ClampReclosed(Link)`. On a linked turn, both gap angles change; redraw both even if only one was touched.
- Undo: `Capture/Restore` includes moves, angles, removal, frozen flags, pass entry latches, pass counts and spring release timestamps. Restore emits no forward gameplay events; rebuild displayed state from queries. Undo should restore the pre-gesture move boundary rather than immediately calling CompleteMove again.
- `ReleaseLink` is an explicit booster bypass of colour/coating requirements, and starts the normal spring timer. Ordinary play must use `Rotate`.

## Solver, grading and budgets

`SolverMeasure.Search` reports exact minimum counted rotation gestures, states, mean branching, blocked choices and dead ends. Free pulls cost zero. A continuous gesture has no revolution cap. Both paired rings must own no active clamps before rotating, so a full sweep clearing every eligible incoming clamp and immediately pulling free rings dominates partial rotations or waiting. Clay may require a second revolution in the same gesture. Search memoises remaining rings, frozen flags, active links and move count; key/oil collection follows removed carriers. Timed branches can be dead ends even when the initial board is solvable. Search supports at most 20 rings.

Classic grading and chapter 1 budgets remain unchanged. Mechanic rating adds directional, coupling, key, rust, spring urgency and coating weights. `MinRotationToClear` / `Adaptive.RequiredRotation` are angular workload estimates for telemetry (coating adds a revolution; paired workload may count both rings), not a minimum-gesture proof or a guaranteed monotone win-distance on timed boards. Solver minimum is the budget authority. `LevelRules.For` always covers that minimum plus at least two moves, with a larger boss allowance.

## Saga and reproduction

Chapter 1 (all 20 canonical boards and their five variants) is preserved, including the existing level 20; its legacy geometry takes precedence over introducing a new boss there. Progress/star storage is unchanged. Chapters 2–8 introduce ratchet, linked, colour, key/padlock, rust/oil, spring and coating respectively. The first slot teaches that chapter's mechanic on a small board. Normal chapter slots focus on its new mechanic; bosses combine mechanics unlocked so far. Chapters 9–10 mix all seven. Every slot has five deterministic solver-validated variants; bosses have 14 through 18 rings. Rust deadlines in shipped boards allow a clean solution with spare moves; wasted gestures can still freeze the ring.

Run `Orcub/Levels/Build Level Pack`, then `Orcub/Simulate Difficulty`, then EditMode tests. Pack schema 3 retains the classic `levels` array and five-choice `variants` structure. The first chapter is copied from the existing pack on rebuild; retain a source pack when rebuilding. Simulation is synthetic calibration evidence only, and now counts timed moves and treats player-created rust dead ends as losses.

## Validation evidence for this run

Unity's bundled C# compiler compiled the runtime sources. A standalone Mono harness also ran the generator and the same LevelPackBuilder source with temporary adapters for JSON, Resources, PlayerPrefs and editor logging/import. It produced 200 slots / 1,000 solver-validated choices; all original chapter 1 fields round-trip identically at Unity float precision. Raw pack: 3,021,657 bytes; gzip comparison: 277,115 bytes. This is not a player build size.

The standalone difficulty simulator ran the same DifficultySimulator source: 471/600 (78.5%) at ability 800, 558/600 (93.0%) at 1100, 597/600 (99.5%) at 1450. It also refreshed the independent 10,000-attempt corpus. These are synthetic results; the 75% calibration target remains unmet for stronger bots. Temporary harness adapters and executables are outside the repository and are not shipped.

Unity MCP at localhost:8080 accepted an EditMode run (job `4ac6cfef7cb945198068babf9c0f76bf`) but subsequent status/menu/scene calls repeatedly failed with `Unity plugin session ... disconnected while awaiting command_result`, `ping not answered`, or `TimeoutError`. A complete Unity EditMode verdict and confirmation of the final active scene are still required. No play-mode start was requested. Do not present the standalone harness as Unity test proof.

Standalone test result after the hint fix: **64 passed, 2 failed** (Ads/Net suites excluded from that harness). All new mechanic cases, saga samples, shipped-pack solvability/geometry/budget checks, five-variant solver checks and hint playthroughs passed. The remaining failures are the existing weld test's Unity-native Quaternion call and the existing attempt-log round-trip test, whose private `[SerializeField]` AttemptPlan fields are not supported by the temporary Newtonsoft adapter. Those two paths require the Unity runner; they are not recorded as passes.
