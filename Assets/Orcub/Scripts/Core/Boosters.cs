using System.Collections.Generic;
using UnityEngine;

namespace Orcub.Core
{
    public enum BoosterKind { Hint, Undo, Unclamp }

    public static class BoosterInventory
    {
        public const int StartCount = 3;
        static string Key(BoosterKind k) => "orcub.booster." + k;

        public static int Count(BoosterKind k) => PlayerPrefs.GetInt(Key(k), StartCount);

        public static bool TrySpend(BoosterKind k)
        {
            int count = Count(k);
            if (count <= 0) return false;
            PlayerPrefs.SetInt(Key(k), count - 1);
            PlayerPrefs.Save();
            return true;
        }

        public static void Add(BoosterKind k, int n)
        {
            if (n <= 0) return;
            PlayerPrefs.SetInt(Key(k), Count(k) + n);
            PlayerPrefs.Save();
        }
    }

    public readonly struct Hint
    {
        public readonly bool PullOut;
        public readonly int Ring;
        public readonly float Delta;

        public Hint(bool pullOut, int ring, float delta)
        {
            PullOut = pullOut;
            Ring = ring;
            Delta = delta;
        }
    }

    public static class Hints
    {
        public static bool TryFind(PuzzleModel m, out Hint hint)
        {
            hint = default;
            for (int i = 0; i < m.Rings.Count; i++)
            {
                if (!m.IsFree(i)) continue;
                hint = new Hint(true, i, 0f);
                return true;
            }

            float best = float.MaxValue;
            bool found = false;
            for (int i = 0; i < m.Rings.Count; i++)
            {
                if (!m.CanRotate(i)) continue;
                foreach (int id in m.Rings[i].Incoming)
                {
                    var link = m.Links[id];
                    if (!link.Active) continue;
                    float delta = Mathf.DeltaAngle(m.Rings[i].GapAngle, link.ClampAngle);
                    float need = Mathf.Abs(delta) -
                        (PuzzleModel.GapHalfAngle - link.ClampHalfAngle) + 3f;
                    if (need >= best) continue;
                    best = need;
                    hint = new Hint(false, i, Mathf.Sign(delta) * need);
                    found = true;
                }
            }
            return found;
        }
    }

    public static class Unclamp
    {
        public static int PickLink(PuzzleModel m)
        {
            int best = -1, bestUpstream = -1;
            bool bestUnlocks = false;
            foreach (var link in m.Links)
            {
                if (!link.Active) continue;
                int owned = 0;
                foreach (int id in m.Rings[link.Owner].Owned)
                    if (m.Links[id].Active) owned++;
                bool unlocks = owned == 1;
                int upstream = CountUpstream(m, link.Owner);
                if (best >= 0 && (bestUnlocks && !unlocks ||
                    bestUnlocks == unlocks && (upstream < bestUpstream ||
                    upstream == bestUpstream && link.Id > best))) continue;
                best = link.Id;
                bestUnlocks = unlocks;
                bestUpstream = upstream;
            }
            return best;
        }

        static int CountUpstream(PuzzleModel m, int owner)
        {
            var visited = new HashSet<int> { owner };
            var pending = new Stack<int>();
            pending.Push(owner);
            int count = 0;
            while (pending.Count > 0)
            {
                foreach (int id in m.Rings[pending.Pop()].Incoming)
                {
                    var link = m.Links[id];
                    if (!link.Active) continue;
                    count++;
                    if (visited.Add(link.Owner)) pending.Push(link.Owner);
                }
            }
            return count;
        }
    }
}
