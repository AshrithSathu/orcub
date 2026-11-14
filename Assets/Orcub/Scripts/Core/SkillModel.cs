using System;
using UnityEngine;
namespace Orcub.Core
{
    public readonly struct SkillEstimate
    {
        public readonly float Rating, Confidence;
        public readonly int PaceTier;
        public SkillEstimate(float rating, float confidence, int paceTier) { Rating = rating; Confidence = confidence; PaceTier = paceTier; }
    }
    public static class SkillModel
    {
        public static SkillEstimate Estimate(PlayerSkill skill, AttemptLog log, Tuning t)
        {
            float seconds = 0, excess = 0; int n = 0;
            for (int i = skill.Recent.Count - 1; i >= 0 && n < t.PaceWindow; i--)
            {
                var sample = skill.Recent[i];
                if (!sample.Won || sample.Efficiency <= 0 || sample.SecondsPerMove <= 0) continue;
                seconds += Mathf.Min(t.SecondsCap, sample.SecondsPerMove);
                excess += Mathf.Min(t.ExcessCap, 1 / sample.Efficiency - 1); n++;
            }
            float pace = n == 0 ? .5f : .5f * (1 - Mathf.InverseLerp(t.FastSeconds, t.SlowSeconds, seconds / n)) +
                .5f * (1 - Mathf.InverseLerp(t.FastExcess, t.SlowExcess, excess / n));
            float modelRating = 0; int models = 0;
            for (int i = log.Entries.Count - 1; i >= 0 && models < t.PaceWindow; i--)
            {
                var r = log.Entries[i];
                if (r.Version < 2 || r.Features == null || r.Features.Count < t.EarlyMoves) continue;
                float p = Mathf.Clamp(r.ModelPrediction, .05f, .95f);
                modelRating += r.LevelRating + t.ModelRatingScale * (float)Math.Log10(p / (1 - p)); models++;
            }
            float pw = n == 0 ? 0 : t.PaceWeight, mw = models == 0 ? 0 : t.ModelWeight;
            float denominator = t.EloWeight + pw + mw;
            float rating = denominator <= 0 ? skill.Rating : (t.EloWeight * skill.Rating + pw * Mathf.Lerp(t.PaceLowRating, t.PaceHighRating, pace) +
                mw * modelRating / Math.Max(1, models)) / denominator;
            return new SkillEstimate(rating, Mathf.Clamp01(skill.Games / t.ConfidenceGames), pace < .33f ? 0 : pace < .67f ? 1 : 2);
        }
    }
}
