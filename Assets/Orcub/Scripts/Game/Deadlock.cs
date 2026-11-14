using Orcub.Core;

namespace Orcub.Game
{
    /// <summary>
    /// Review e, M5: "no legal progress possible". The exact solver works from a fresh level, not a live state,
    /// so the board uses two cheap, sound checks instead (each one only reports boards that can never be solved
    /// by any gesture sequence; Undo / Unclamp boosters are outside the rules and offered by the fail card):
    ///  1. a frozen ring that still has an active clamp on it: rust is permanent and only the ring's own turn
    ///     (impossible now) releases its incoming clamps, so it can never come off the board;
    ///  2. nothing can make progress: no ring is free to pull, and no ring that can turn (in a direction both its
    ///     own and its gear partner's ratchets allow) has a releasable clamp on itself or its partner. Further
    ///     moves only advance rust deadlines and re-close springs, never open anything, so the state is final.
    /// </summary>
    public static class Deadlock
    {
        public static bool IsDead(PuzzleModel m)
        {
            if (m == null || m.IsSolved) return false;
            var rings = m.Rings;
            for (int i = 0; i < rings.Count; i++)
            {
                var r = rings[i];
                if (r.Removed || !r.Frozen) continue;
                for (int k = 0; k < r.Incoming.Count; k++)
                    if (m.Links[r.Incoming[k]].Active) return true;   // rule 1
            }
            for (int i = 0; i < rings.Count; i++)
                if (!rings[i].Removed && m.IsFree(i)) return false;
            for (int i = 0; i < rings.Count; i++)
            {
                if (rings[i].Removed || !m.CanRotate(i)) continue;
                int mate = m.LinkedPartner(i);
                bool turns = Turns(m, i, mate, 1f) || Turns(m, i, mate, -1f);
                if (!turns) continue;
                if (HasReleasable(m, i) || (mate >= 0 && HasReleasable(m, mate))) return false;
            }
            return true;   // rule 2
        }

        static bool Turns(PuzzleModel m, int ring, int mate, float dir) =>
            m.DirectionAllowed(ring, dir) && (mate < 0 || m.DirectionAllowed(mate, -dir));

        static bool HasReleasable(PuzzleModel m, int ring)
        {
            var r = m.Rings[ring];
            for (int k = 0; k < r.Incoming.Count; k++)
            {
                var l = m.Links[r.Incoming[k]];
                if (l.Active && (!l.Tinted || l.Color == r.Color)) return true;
            }
            return false;
        }
    }
}
