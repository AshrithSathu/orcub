/*
BoardView integration (game layer owns one Adaptive instance for its lifetime):

    readonly Adaptive adaptive = new Adaptive();
    AttemptPlan attemptPlan;
    // LoadLevel: after choosing the fixed saga LevelDef, before enabling input:
    attemptPlan = adaptive.BeginAttempt(levelIndex, def);
    rules = LevelRules.For(def, tutorial: levelIndex < 3, extraMoves: attemptPlan.ExtraMoves);
    // Reset per-attempt elapsed seconds, movesUsed, boostersUsed and continues here.
    // Offer a glow via Hints.TryFind(model, out hint) when attemptPlan.FreeHintGlow.
    // Never recompute the plan after spending a booster or taking a continue.

    // OnSolved, once:
    adaptive.EndAttempt(new AttemptResult(AttemptOutcome.Win, movesUsed, elapsedSeconds, boostersUsed, continues));
    // Out-of-moves: only when the player finally declines/exhausts continues:
    adaptive.EndAttempt(new AttemptResult(AttemptOutcome.Lose, movesUsed, elapsedSeconds, boostersUsed, continues));
    // Quit-to-home or restart before a terminal result:
    adaptive.EndAttempt(new AttemptResult(AttemptOutcome.Quit, movesUsed, elapsedSeconds, boostersUsed, continues));

End the current attempt before LoadLevel starts a retry or another level. Duplicate
terminal callbacks are ignored. BeginAttempt rejects overlapping attempts. The plan
is immutable, and ratings are evaluated against the geometry captured at BeginAttempt.
This facade does not spend lives, grant inventory, award stars, or advance saga progress.
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

        public Adaptive(PlayerSkill skill = null, FailureStreak streak = null, AttemptLog log = null)
        {
            Skill = skill ?? new PlayerSkill();
            Streak = streak ?? new FailureStreak();
            Log = log ?? new AttemptLog();
        }

        public AttemptPlan BeginAttempt(int levelIndex, LevelDef def)
        {
            if (active != null) throw new InvalidOperationException("End the active attempt before beginning another.");
            if (levelIndex < 0) throw new ArgumentOutOfRangeException(nameof(levelIndex));
            float rating = LevelRating.For(def); // validates solvability before any attempt can start
            var rules = LevelRules.For(def, tutorial: levelIndex < 3);
            var plan = DifficultyPolicy.Plan(levelIndex, rules, Skill, Streak);
            index = levelIndex;
            minMoves = rules.MinMoves;
            levelRating = rating;
            ratingBefore = Skill.Rating;
            startedUtc = DateTime.UtcNow.ToString("o");
            active = plan;
            return plan;
        }

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
                LevelIndex = index, StartedUtc = startedUtc, Plan = plan, LevelRating = levelRating,
                RatingBefore = ratingBefore, RatingAfter = Skill.Rating, Result = r
            });
        }
    }
}
