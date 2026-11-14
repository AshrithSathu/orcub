using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Orcub.Core
{
    /// <summary>
    /// Exact gesture search: an unrestricted gesture may sweep twice without lifting.
    /// Both geared rings must own no active clamps before turning, so clearing all matching
    /// incoming clamps and immediately pulling free rings dominates a partial sweep or waiting.
    /// Pulling is free and only helps (collects keys/oil and prevents spring reclosure).
    /// Thus each useful successor permanently removes at least one ring; depth is bounded by N.
    /// </summary>
    public static class MechanicSolver
    {
        public sealed class Result
        {
            public int MinimumMoves = -1, States, DeadEnds, BlockedChoices, Taps;
            public float Branching, Rotation;
        }

        public static bool HasMechanics(LevelDef def)
        {
            foreach (var r in def.Rings)
                if (r.Ratchet != 0 || r.GearGroup != 0 || r.KeyId != 0 || r.PadlockId != 0 || r.RustAfter > 0 || r.OilId != 0) return true;
            foreach (var l in def.Links) if (l.Tinted || l.Clay || l.SpringMoves > 0) return true;
            return false;
        }

        public static bool Solve(LevelDef def, out Result result, out List<int> order)
        {
            if (def.Rings.Count > 20) throw new ArgumentException("Search supports at most 20 rings");
            var m = new PuzzleModel(def);
            var stats = new Result();
            var memo = new Dictionary<string, int>();
            var path = new List<int>();
            List<int> bestOrder = null;
            int branches = 0;
            int Visit()
            {
                int pathStart = path.Count;
                // Repeat because key/oil removal can affect queries on other rings.
                for (int i = 0; i < m.Rings.Count; i++)
                    if (m.IsFree(i)) { m.TryRemove(i); path.Add(i); }
                if (m.IsSolved)
                {
                    if (bestOrder == null) bestOrder = new List<int>(path);
                    path.RemoveRange(pathStart, path.Count - pathStart);
                    return 0;
                }
                var key = new StringBuilder().Append(m.Moves).Append(':');
                foreach (var r in m.Rings) key.Append(r.Removed ? 'R' : r.Frozen ? 'F' : '.');
                foreach (var l in m.Links) key.Append(l.Active ? 'A' : '.');
                string state = key.ToString();
                if (memo.TryGetValue(state, out int cached))
                { path.RemoveRange(pathStart, path.Count - pathStart); return cached; }
                stats.States++;
                int best = 1000, choices = 0;
                var snapshot = m.Capture();
                for (int i = 0; i < m.Rings.Count; i++)
                {
                    if (m.Rings[i].Removed) continue;
                    if (!m.CanRotate(i)) { stats.BlockedChoices++; continue; }
                    int mate = m.LinkedPartner(i);
                    bool hasClamp = false, clay = false;
                    foreach (var l in m.Links)
                        if (l.Active && (l.Target == i || l.Target == mate) && (!l.Tinted || l.Color == m.Rings[l.Target].Color))
                        { hasClamp = true; clay |= l.Clay && l.Passes == 0; }
                    if (!hasClamp) continue;
                    float angle = clay ? 720 : 360;
                    if (!m.DirectionAllowed(i, angle) || (mate >= 0 && !m.DirectionAllowed(mate, -angle))) angle = -angle;
                    if (m.Rotate(i, angle) != RotateOutcome.Rotated) continue;
                    choices++;
                    m.CompleteMove();
                    int cost = Visit();
                    if (cost + 1 < best) best = cost + 1;
                    m.Restore(snapshot);
                }
                branches += choices;
                if (choices == 0) stats.DeadEnds++;
                memo[state] = best;
                path.RemoveRange(pathStart, path.Count - pathStart);
                return best;
            }
            int minimum = Visit();
            stats.MinimumMoves = minimum >= 1000 ? -1 : minimum;
            stats.Branching = stats.States == 0 ? 0 : branches / (float)stats.States;
            // Angular workload is a heuristic sum; gesture minimum above is exact.
            var initial = new PuzzleModel(def);
            foreach (var r in initial.Rings) stats.Rotation += initial.MinRotationToClear(r.Id);
            stats.Taps = Mathf.CeilToInt(stats.Rotation / 60);
            result = stats; order = bestOrder ?? new List<int>();
            return minimum < 1000;
        }
    }
}
