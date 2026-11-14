using System;
using UnityEngine;

namespace Orcub.Core
{
    /// <summary>Publicly immutable snapshot; serialized private fields allow exact telemetry round-trips.</summary>
    [Serializable]
    public sealed class AttemptPlan
    {
        [SerializeField] int extraMoves;
        [SerializeField] bool freeHintGlow;
        [SerializeField] string reason;
        [SerializeField] int moveLimit;
        public int ExtraMoves => extraMoves;
        public bool FreeHintGlow => freeHintGlow;
        public string Reason => reason;
        public int MoveLimit => moveLimit;
        internal AttemptPlan(int extra, bool hint, string why, int budget)
        { extraMoves = extra; freeHintGlow = hint; reason = why; moveLimit = budget; }
    }

    public static class DifficultyPolicy
    {
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
