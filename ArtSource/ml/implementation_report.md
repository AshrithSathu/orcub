# Adaptive difficulty v2

Original implementation; only architecture notes were read. No third-party source was opened or ported.

## Architecture

The v2 pack preserves the 200 existing canonical boards as variant 0. Four alternatives per slot are graded with LevelRating and an exact monotone full-sweep solver search. A gesture can clear every clamp on one target; every initially clamped target requires a gesture, so the target count is both a lower bound and an achievable minimum. Search explores release-order subsets, records mean legal branching, blocked choices and true dead ends. Pulls cost no rotation move. These metrics describe the game's unrestricted drag rules, not an optimal tap-only solution.

Variant 0 preserves original geometry and saga identity. Tutorial alternatives retain tiny layouts with different gap starts; their achievable band is deliberately narrow. Later boards target canonical rating offsets -140, -70, +70, +140, bounded by available validated generator content. Variant IDs are stable pack positions, not sorted ranks. Selection sorts by measured rating.

PlayerSkill persists Elo and eight performance samples. SkillModel combines Elo, capped recent solve pace and excess moves, and early-model evidence into a rating with evidence-coverage confidence. The model is a four-feature logistic regression: useful-move fraction, mean think time / 30, locked touches per move / 10, undo fraction. EarlyFeatures also records each raw move. JSON schema 1 fixes feature order/normalisation; changed feature semantics require a new schema. Outcomes/boosters/continues never enter early features. Unsupported/incomplete legacy records do not influence the model estimate.

DifficultyPolicy samples a skill-shaped Beta distribution around rating minus the Elo advantage for a 75% win target, then chooses the nearest available variant, excluding two recent variants for that saga slot. At two failures it steps below the previous variant, with repeat avoidance subordinate to relief. Extra moves start at four failures and hint glow at five. All budgets cover solver minimum plus at least two moves. A frozen plan never changes after BeginAttempt. No advertising, monetisation or spending inputs exist in selection or skill inference.

Tuning.FromJson overlays validated defaults; Adaptive.Configure(tuningJson, modelJson) applies between attempts and rejects changes during play. PlayerModel.Load(localOverridePath) supports a downloaded JSON file, falling back to Resources/Orcub/skill_model on invalid files. The game/network layer owns fetching Firebase config and writing overrides. Configuration/model versions and prediction are logged per attempt.

The legacy BeginAttempt(index, def) and DifficultyPolicy.Plan remain canonical-only v1 assistance APIs for existing callers/tests. Use BeginAttempt(index) for the complete v2 policy. The legacy path intentionally preserves existing behaviour.

## Public API and BoardView integration

```csharp
BoardView integration (one Adaptive for the BoardView lifetime):
    readonly Adaptive adaptive = new Adaptive();
    AttemptPlan attemptPlan;

    // LoadLevel, before enabling input (end/cancel the previous attempt first):
    attemptPlan = adaptive.BeginAttempt(levelIndex);
    var data = LevelLibrary.Get(levelIndex, attemptPlan.Variant);
    var def = data.ToDef();
    model = new PuzzleModel(def);
    rules = LevelRules.For(def, tutorial: levelIndex < 3, extraMoves: attemptPlan.ExtraMoves);
    // Reset moves/elapsed; offer Hints.TryFind glow when attemptPlan.FreeHintGlow.

    // Once per counted gesture, including failed/undo gestures:
    // Capture before the gesture: requiredBefore = Adaptive.RequiredRotation(model);
    // Accumulate locked touches since the last gesture and think time using unscaled time.
    adaptive.RecordMove(Adaptive.RequiredRotation(model) < requiredBefore - 0.01f,
        thinkSeconds, lockedTouches, wasUndo);

    // Exactly once at final win/loss/quit; do not end on a continue prompt:
    adaptive.EndAttempt(new AttemptResult(outcome, movesUsed, elapsedSeconds, boostersUsed, continues));
    // If leaving before input: adaptive.CancelAttempt();

Never reselect within an attempt. Use plan.MoveLimit if applying custom rules.
BeginAttempt(index, def) is the legacy canonical-only API; use BeginAttempt(index)
for variants. Progress/stars stay keyed by saga index; variant 0 retains original geometry.
RequiredRotation measures remaining angular sweep, so useful partial drags are recognised.
No policy or model accepts advertising, purchase or spend information.
```

The caller owns elapsed time, move accounting and final outcome. RecordMove(useful, thinkSeconds, lockedTouches, undo) records at most the first Tuning.EarlyMoves gestures. RequiredRotation(model) sums minimum angular sweep remaining. Accumulate rejected locked touches until the next gesture; report undo separately. Call EndAttempt once on the final result, including abandonment/restart; CancelAttempt drops untouched attempts. Duplicate EndAttempt calls are ignored. Saga stars/progress remain the game layer's responsibility; tune cross-variant star fairness with human playtesting.

## Training and reproduction

Runtime telemetry is the JSON object `Attempts` in `Path.Combine(Application.persistentDataPath, "orcub-attempts.json")`, retaining 500 recent records. Unity's Application.persistentDataPath determines the platform-specific directory. Schema 2 adds Variant, Features (including Moves), ModelPrediction, ModelVersion, TuningVersion, SkillEstimate and SkillConfidence. Schema 1 reads with absent optional fields and is excluded from training.

1. Run editor menu `Orcub/Levels/Build Level Pack`.
2. Run `Orcub/Simulate Difficulty` to write sim_report.md and ignored sim_attempts.json.
3. Run `python3 ArtSource/ml/train_skill_model.py ArtSource/ml/sim_attempts.json`.
4. Reimport Resources, rerun the simulation with trained weights, then run all EditMode tests.

Training uses independent synthetic players and random boards, fixed seed 1729. Four-fifths train, every fifth eligible sample is held out. Only complete early windows, terminal non-quit attempts without boosters/continues are eligible. Do not interpret synthetic holdout performance as human validation. Real telemetry retraining must use player-separated, time-separated holdouts and check calibration and subgroup performance before remote rollout. No telemetry upload is performed here.

The bot selects actual rings, applies noisy short/full rotations, sometimes undoes and records think time. Win means PuzzleModel.IsSolved before the frozen gesture budget expires. Simulation reports first-try rates after 120 separate warmup games, 600 measured games per band, and generates 10,000 independent training attempts. The 75% target can saturate where a slot's finite variant band cannot match a player; the report retains those deviations rather than forcing outcomes. This is an initial calibrated system, not evidence of commercial retention or production human win rates.

## Results and remaining calibration work

Unity editor rebuilt the pack through LevelPackBuilder. All 200 canonical ring/link/seed arrays match the previous committed pack. There are 1,000 solver-validated choices. Pack size: 1,753,501 bytes raw (1.67 MiB), 288,481 bytes gzip (281.7 KiB); previous raw pack 267,263 bytes. Gzip is a transfer comparison, not a measured player build size. Canonicals are duplicated in the backwards-compatible pack structure.


| Skill band | First try wins / attempts | Win rate |
|---|---:|---:|
| 800 | 516 / 600 | 86.0% |
| 1100 | 587 / 600 | 97.8% |
| 1450 | 600 / 600 | 100.0% |

**The 75% first-try calibration acceptance target is not met.** With current content bands, these bots exceed the target, particularly the strong band. Safe minimum budgets and available hardest variants bound the challenge. Do not claim this is calibrated for human production rollout. A broader harder-but-solvable content band, human action traces and variant-level empirical rating updates are needed before that claim. No forced-loss or economy mechanisms were added to conceal this limitation. Tutorial bands also cannot span a broad difficulty range while retaining their tiny teaching layouts.

Model training: 6,133 training / 1,534 held-out complete-window samples; accuracy 91.79%, Brier 0.05994 (prevalence baseline 0.07865), log loss 0.20378. Holdout observed win rate 91.395%, mean predicted 91.372%. Accuracy alone is inflated by class imbalance; the Brier comparison provides additional evidence. These are synthetic metrics, not human performance. Weights in Resources/Orcub/skill_model.json are trained, version sim-logistic-v1. Source corpus checksum and exact metrics are in model_metrics.json. The ignored corpus is reproducible through the editor menu.

Unity MCP run_tests(EditMode), job 7720f77af05347e5a4cd8d6295ee59b3: **67 passed, 0 failed, 0 skipped**, 27.63 seconds. This includes the concurrently added ad-policy tests as well as existing tests and nine new v2 tests. No scene edits or play mode changes were made by this implementation. Canonical preservation was also independently compared against git. Game, ad integration, packages, assembly definition changes and ProjectSettings are owned by the other agent and are excluded from this commit.
