using System;
using UnityEngine;

namespace Orcub.Core
{
    /// <summary>Publicly immutable snapshot; serialized private fields allow exact telemetry round-trips.</summary>
    [Serializable]
    public sealed class AttemptPlan
    {
        [SerializeField] int variant;
        public int Variant => variant;
        [SerializeField] int extraMoves;
        [SerializeField] bool freeHintGlow;
        [SerializeField] string reason;
        [SerializeField] int moveLimit;
        public int ExtraMoves => extraMoves;
        public bool FreeHintGlow => freeHintGlow;
        public string Reason => reason;
        public int MoveLimit => moveLimit;
        internal AttemptPlan(int extra, bool hint, string why, int budget, int selectedVariant = 0)
        { variant = selectedVariant; extraMoves = extra; freeHintGlow = hint; reason = why; moveLimit = budget; }
    }

    public static class DifficultyPolicy
    {
        public static AttemptPlan Select(int levelIndex, LevelData[] choices, SkillEstimate skill, int failures,
            AttemptLog log, Tuning t, System.Random rng)
        {
            if (choices == null || choices.Length == 0) throw new ArgumentException("No variants");
            float advantage = 400f * (float)Math.Log10(t.TargetWinRate / (1 - t.TargetWinRate));
            float center = skill.Rating - advantage;
            double shape = .25 + .5 * Mathf.InverseLerp(t.PaceLowRating, t.PaceHighRating, skill.Rating);
            double sample = Beta(shape * t.BetaConcentration, (1 - shape) * t.BetaConcentration, rng);
            float target = center + ((float)sample - (float)shape) * t.TargetSpread;
            var order = new System.Collections.Generic.List<int>();
            for (int i = 0; i < choices.Length; i++) order.Add(i);
            order.Sort((a,b) => choices[a].rating.CompareTo(choices[b].rating));
            int selected = 0; float best = float.MaxValue;
            var recent = new System.Collections.Generic.HashSet<int>();
            int previous = -1;
            for (int i = log.Entries.Count - 1; i >= 0; i--)
                if (log.Entries[i].LevelIndex == levelIndex)
                { if (previous < 0) previous = log.Entries[i].Variant;
                    if (recent.Count < t.RecentVariants) recent.Add(log.Entries[i].Variant);
                    if (recent.Count >= t.RecentVariants) break; }
            for (int i = 0; i < choices.Length; i++)
            {
                float distance = Mathf.Abs(choices[i].rating - target);
                if (recent.Contains(i) && recent.Count < choices.Length) continue;
                if (distance < best) { best = distance; selected = i; }
            }
            // Retry relief takes precedence over repeat avoidance; never step above the previous failure.
            if (failures >= t.StepDownAfter)
            {
                int rank = order.IndexOf(previous >= 0 && previous < choices.Length ? previous : selected);
                selected = order[Math.Max(0, rank - 1)];
            }
            var rules = LevelRules.For(choices[selected].ToDef(), levelIndex < 3);
            int minimum = choices[selected].solver?.MinimumMoves ?? rules.MinMoves;
            int relief = failures < t.MovesAfter ? 0 : (int)Math.Min(t.MaxExtraMoves, 2L * (failures - t.MovesAfter + 1L));
            int extra = Math.Max(relief, minimum + t.SafetyMargin - rules.MoveLimit);
            return new AttemptPlan(extra, failures >= t.HintAfter,
                $"{t.Version};target={target:F1};confidence={skill.Confidence:F2};failures={failures};variant={selected}", rules.MoveLimit + extra, selected);
        }

        public static double Beta(double alpha, double beta, System.Random rng)
        {
            if (rng == null || double.IsNaN(alpha) || double.IsNaN(beta) || double.IsInfinity(alpha) || double.IsInfinity(beta) || alpha <= 0 || beta <= 0)
                throw new ArgumentException("Beta shapes must be finite and positive");
            double a = Gamma(alpha, rng), b = Gamma(beta, rng);
            return a / (a + b);
        }
        static double Gamma(double shape, System.Random rng)
        {
            if (shape < 1) return Gamma(shape + 1, rng) * Math.Pow(Math.Max(1e-12, rng.NextDouble()), 1 / shape);
            double d = shape - 1.0 / 3, c = 1 / Math.Sqrt(9 * d);
            while (true)
            {
                double x = Math.Sqrt(-2 * Math.Log(Math.Max(1e-12, rng.NextDouble()))) * Math.Cos(2 * Math.PI * rng.NextDouble());
                double v = 1 + c * x;
                if (v <= 0) continue;
                v = v * v * v;
                double u = Math.Max(1e-12, rng.NextDouble());
                if (u < 1 - .0331 * x * x * x * x || Math.Log(u) < .5 * x * x + d * (1 - v + Math.Log(v))) return d * v;
            }
        }

        public const int MaxAssistanceMoves = 10;
        public const int SafetyMoves = 2;
        public const float TargetWinRate = 0.75f;

        /// <summary>
        /// Fixed saga order, assistance only. Initial 75% target uses a rules-only rating proxy
        /// (700 + 18*links + 12*minMoves). Each extra move is provisionally worth 40 Elo points.
        /// These are balancing priors, not a measured completion-rate guarantee. At least six
        /// observations are required for skill aid; fluent/expert players receive only retry aid.
        /// Normal assistance caps at 10; repair of an invalid base budget takes precedence over the cap.
        /// No spending, ad value or monetisation signals are accepted or consulted.
        /// </summary>
        public static AttemptPlan Plan(int levelIndex, LevelRules baseRules, PlayerSkill skill, FailureStreak streak)
        {
            if (skill == null) throw new ArgumentNullException(nameof(skill));
            if (streak == null) throw new ArgumentNullException(nameof(streak));
            if (baseRules.MinMoves < 0 || baseRules.Links < 0 || baseRules.MoveLimit < 0)
                throw new ArgumentOutOfRangeException(nameof(baseRules));
            int failures = streak.Count(levelIndex);
            int retry = failures < 2 ? 0 : 2 * Mathf.Min(5, failures - 1);
            int ability = 0;
            if (skill.Confidence >= 0.25f && skill.Tier < 2)
            {
                float proxy = 700f + 18f * baseRules.Links + 12f * baseRules.MinMoves;
                // A player needs ~191 Elo advantage for a 75% expected score.
                float targetAdvantage = 400f * (float)Math.Log10(TargetWinRate / (1f - TargetWinRate));
                ability = Mathf.Clamp(Mathf.CeilToInt((proxy + targetAdvantage - skill.Rating) / 40f), 0, 6);
            }
            int assist = Mathf.Min(MaxAssistanceMoves, retry + ability);
            int floor = checked(Mathf.Max(baseRules.MinMoves, baseRules.Links) + SafetyMoves);
            int repair = Mathf.Max(0, floor - baseRules.MoveLimit);
            int extra = Mathf.Max(assist, repair);
            int budget = checked(baseRules.MoveLimit + extra);
            string why = $"v1;skill={ability};retry={retry};failures={failures};safety={repair}";
            return new AttemptPlan(extra, failures >= 3, why, budget);
        }
    }
}
