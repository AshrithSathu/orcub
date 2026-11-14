using System.Collections.Generic;
using Orcub.Core;

namespace Orcub.Game
{
    /// <summary>
    /// Dev autosolve planner: the same exact gesture search as <see cref="MechanicSolver"/> (full sweeps in an
    /// allowed direction, clay double sweeps, free pulls after every gesture, counted moves with spring and rust
    /// timers), run from the live board state so it also recovers after the player has made moves.
    /// </summary>
    public static class AutoPlan
    {
        const int Unsolvable = 1000;

        /// <summary>Best next gesture (ring, signed degrees) from the current state, or (-1, 0) if none solves it.</summary>
        public static (int ring, float angle) Next(LevelDef def, PuzzleModel live, Dictionary<string, int> memo)
        {
            var m = new PuzzleModel(def);
            m.Restore(live.Capture());
            var snap = m.Capture();
            int bestCost = Unsolvable, bestRing = -1;
            float bestAngle = 0f;
            foreach (var (ring, angle) in Candidates(m))
            {
                if (m.Rotate(ring, angle) != RotateOutcome.Rotated) { m.Restore(snap); continue; }
                m.CompleteMove();
                int cost = 1 + Cost(m, memo, 0);
                m.Restore(snap);
                if (cost < bestCost) { bestCost = cost; bestRing = ring; bestAngle = angle; }
            }
            return bestCost >= Unsolvable ? (-1, 0f) : (bestRing, bestAngle);
        }

        static int Cost(PuzzleModel m, Dictionary<string, int> memo, int depth)
        {
            for (int i = 0; i < m.Rings.Count; i++) if (m.IsFree(i)) m.TryRemove(i);
            for (int i = 0; i < m.Rings.Count; i++) if (m.IsFree(i)) m.TryRemove(i);   // keys/oil can free more
            if (m.IsSolved) return 0;
            string key = MechanicSolver.MemoKey(m);
            if (memo.TryGetValue(key, out int cached)) return cached;
            if (depth > 40) return Unsolvable;
            int best = Unsolvable;
            var snap = m.Capture();
            foreach (var (ring, angle) in Candidates(m))
            {
                if (m.Rotate(ring, angle) == RotateOutcome.Rotated)
                {
                    m.CompleteMove();
                    int c = 1 + Cost(m, memo, depth + 1);
                    if (c < best) best = c;
                }
                m.Restore(snap);
            }
            memo[key] = best;
            return best;
        }

        static List<(int, float)> Candidates(PuzzleModel m)
        {
            var list = new List<(int, float)>();
            for (int i = 0; i < m.Rings.Count; i++)
            {
                if (m.Rings[i].Removed || !m.CanRotate(i)) continue;
                int mate = m.LinkedPartner(i);
                bool hasClamp = false, clay = false;
                foreach (var l in m.Links)
                    if (l.Active && (l.Target == i || l.Target == mate) && (!l.Tinted || l.Color == m.Rings[l.Target].Color))
                    { hasClamp = true; clay |= l.Clay && l.Passes == 0; }
                if (!hasClamp) continue;
                float angle = clay ? 720f : 360f;
                if (!m.DirectionAllowed(i, angle) || (mate >= 0 && !m.DirectionAllowed(mate, -angle))) angle = -angle;
                list.Add((i, angle));
            }
            return list;
        }
    }
}
