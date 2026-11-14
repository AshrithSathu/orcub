using System;
using UnityEngine;

namespace Orcub.Core
{
    public static class LevelRating
    {
        /// <summary>
        /// Version 1 prior: 650 + 12 * generator difficulty + 8 * minimum gesture moves,
        /// clamped to [650,1800]. Generator difficulty includes rings, dependency depth,
        /// excess links and minimum sweep angle. Recompute from geometry, not cached pack grades,
        /// so LevelData and LevelDef agree. This prior needs calibration from attempt telemetry.
        /// </summary>
        public static float For(LevelDef def)
        {
            if (def == null) throw new ArgumentNullException(nameof(def));
            if (!LevelMetrics.Evaluate(def, out int depth, out float rotation, out _))
                throw new ArgumentException("Adaptive attempts require a solvable level.", nameof(def));
            int extra = Mathf.Max(0, def.Links.Count - Mathf.Max(0, def.Rings.Count - 1));
            float difficulty = LevelMetrics.Difficulty(def.Rings.Count, depth, extra, rotation);
            return Mathf.Clamp(650f + 12f * difficulty + 8f * LevelRules.For(def).MinMoves, 650f, 1800f);
        }

        public static float For(LevelData data) => For((data ?? throw new ArgumentNullException(nameof(data))).ToDef());
    }
}
