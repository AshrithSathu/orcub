/*
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
*/
using System;

namespace Orcub.Core
{
    public sealed class Adaptive
    {
        public PlayerSkill Skill { get; }
        public FailureStreak Streak { get; }
        public AttemptLog Log { get; }
        AttemptPlan active;
        int index, minMoves;
        float levelRating, ratingBefore;
        string startedUtc;
        Tuning tuning;
        PlayerModel model;
        readonly Random random;
        Tuning activeTuning;
        EarlyFeatures features;
        SkillEstimate estimate;
        public float Prediction => features == null || features.Count == 0 ? .5f : model.Predict(features);
        public string TuningVersion => tuning.Version;

        public Adaptive(PlayerSkill skill = null, FailureStreak streak = null, AttemptLog log = null, Tuning tuning = null, PlayerModel model = null, Random random = null)
        {
            this.tuning = (tuning ?? new Tuning()).Snapshot();
            this.model = PlayerModel.Parse(UnityEngine.JsonUtility.ToJson(model ?? PlayerModel.Load()));
            this.random = random ?? new Random();
            Skill = skill ?? new PlayerSkill(tuning: this.tuning);
            Skill.UseTuning(this.tuning);
            Streak = streak ?? new FailureStreak();
            Log = log ?? new AttemptLog();
        }

        /// <summary>Remote config entry point. Apply between attempts; invalid input leaves current config intact.</summary>
        public void Configure(string tuningJson, string modelJson = null)
        {
            if (active != null) throw new InvalidOperationException("Apply remote configuration between attempts");
            var nextTuning = Tuning.FromJson(tuningJson);
            var nextModel = modelJson == null ? model : PlayerModel.Parse(modelJson);
            tuning = nextTuning; model = nextModel; Skill.UseTuning(tuning);
        }

        public static float RequiredRotation(PuzzleModel puzzle)
        {
            float remaining = 0;
            foreach (var ring in puzzle.Rings)
            {
                if (!ring.Removed) remaining += puzzle.MinRotationToClear(ring.Id);
            }
            return remaining;
        }

        public void RecordMove(bool useful, float thinkSeconds, int lockedTouches = 0, bool undo = false)
        {
            if (active == null) throw new InvalidOperationException("No active attempt");
            features.Add(new MoveFeature(useful, thinkSeconds, lockedTouches, undo), activeTuning);
        }

        public AttemptPlan BeginAttempt(int levelIndex)
        {
            if (active != null) throw new InvalidOperationException("End the active attempt first");
            if (levelIndex < 0) throw new ArgumentOutOfRangeException(nameof(levelIndex));
            var choices = LevelLibrary.GetVariants(levelIndex);
            activeTuning = tuning.Snapshot();
            estimate = SkillModel.Estimate(Skill, Log, activeTuning);
            var plan = DifficultyPolicy.Select(levelIndex, choices, estimate, Streak.Count(levelIndex), Log, activeTuning, random);
            Start(levelIndex, choices[plan.Variant].ToDef(), plan);
            return plan;
        }

        public AttemptPlan BeginAttempt(int levelIndex, LevelDef def)
        {
            if (active != null) throw new InvalidOperationException("End the active attempt before beginning another.");
            if (levelIndex < 0) throw new ArgumentOutOfRangeException(nameof(levelIndex));
            float rating = LevelRating.For(def); // validates solvability before any attempt can start
            var rules = LevelRules.For(def, tutorial: levelIndex < 3);
            var plan = DifficultyPolicy.Plan(levelIndex, rules, Skill, Streak);
            activeTuning = tuning.Snapshot();
            estimate = SkillModel.Estimate(Skill, Log, activeTuning);
            Start(levelIndex, def, plan);
            return plan;
        }

        void Start(int levelIndex, LevelDef def, AttemptPlan plan)
        {
            float rating = LevelRating.For(def);
            var rules = LevelRules.For(def, levelIndex < 3);
            features = new EarlyFeatures();
            index = levelIndex;
            minMoves = rules.MinMoves;
            levelRating = rating;
            ratingBefore = Skill.Rating;
            startedUtc = DateTime.UtcNow.ToString("o");
            active = plan;
        }

        /// <summary>Drops the active attempt without recording it (e.g. the player left before making a move).</summary>
        public void CancelAttempt() => active = null;

        public void EndAttempt(AttemptResult r)
        {
            if (active == null) return;
            r.Validate();
            var plan = active;
            Skill.Record(levelRating, minMoves, r);
            Streak.Record(index, r.Outcome);
            active = null;
            Log.Append(new AttemptRecord
            {
                Variant = plan.Variant, Features = features, ModelPrediction = Prediction,
                TuningVersion = activeTuning.Version, ModelVersion = model.Version,
                SkillEstimate = estimate.Rating, SkillConfidence = estimate.Confidence,
                LevelIndex = index, StartedUtc = startedUtc, Plan = plan, LevelRating = levelRating,
                RatingBefore = ratingBefore, RatingAfter = Skill.Rating, Result = r
            });
        }
    }
}
