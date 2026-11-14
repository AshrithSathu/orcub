using System;
using System.Collections.Generic;
namespace Orcub.Core
{
    [Serializable]
    public sealed class SolverMeasure
    {
        public int MinimumMoves, States, DeadEnds, BlockedChoices;
        public float Branching;
        // Exact over full-sweep gestures. Removing a free ring costs no rotation move.
        // Constraints are monotone: each initially clamped target needs exactly one sweep.
        public static SolverMeasure Search(LevelDef def)
        {
            if (MechanicSolver.HasMechanics(def))
            {
                if (!MechanicSolver.Solve(def, out var measured, out _)) throw new ArgumentException("Unsolvable board");
                return new SolverMeasure { MinimumMoves = measured.MinimumMoves, States = measured.States,
                    DeadEnds = measured.DeadEnds, BlockedChoices = measured.BlockedChoices, Branching = measured.Branching };
            }
            if (!PuzzleModel.IsSolvable(def, out _)) throw new ArgumentException("Unsolvable board");
            if (def.Rings.Count > 20) throw new ArgumentException("Search supports at most 20 rings");
            var m = new PuzzleModel(def);
            var seen = new HashSet<int>();
            var result = new SolverMeasure { MinimumMoves = LevelRules.For(def).MinMoves };
            int branches = 0;
            void Visit(int mask)
            {
                if (!seen.Add(mask)) return;
                result.States++;
                int choices = 0;
                var snapshot = m.Capture();
                for (int i = 0; i < m.Rings.Count; i++)
                {
                    if ((mask & (1 << i)) != 0) continue;
                    bool incoming = false;
                    foreach (int id in m.Rings[i].Incoming) incoming |= m.Links[id].Active;
                    if (!incoming) continue;
                    if (!m.CanRotate(i)) { result.BlockedChoices++; continue; }
                    choices++;
                    m.Rotate(i, 360);
                    Visit(mask | (1 << i));
                    m.Restore(snapshot);
                }
                branches += choices;
                if (choices == 0)
                {
                    bool active = false;
                    foreach (var link in m.Links) active |= link.Active;
                    if (active) result.DeadEnds++;
                }
            }
            Visit(0);
            result.Branching = branches / (float)result.States;
            return result;
        }
    }
}
