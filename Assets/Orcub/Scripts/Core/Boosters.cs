using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orcub.Core
{
    // Store binds these delegates; no SDK dependency enters the board assembly.
    public static class ConsumableGateway
    {
        public static Func<string, int> Count;
        public static Func<string, bool> Spend;
        public static Action Refill;
        public static Func<string,System.Threading.Tasks.Task<string>> PrepareAdRefill;
        public static Action<string> AdRefillEarned;
        public static Func<System.Threading.Tasks.Task> FlushSpends;
        public static Action<string,string> MigrateSpends;
        public static Action LevelStarted, LevelEnded;
        public static Func<string, System.Threading.Tasks.Task<bool>> RefillFromAd;
        public static Func<System.Threading.Tasks.Task<bool>> RefillWithGold;
        public static Func<int> SecondsToNext;
        public static Action<string> BeginAttempt;
    }

    public enum BoosterKind { Hint, Undo, Unclamp }

    public static class BoosterInventory
    {
        public const int StartCount = 0; // backend wallets.inventory defaults to {}

        public static int Count(BoosterKind k) => ConsumableGateway.Count?.Invoke("booster_" + k.ToString().ToLowerInvariant()) ?? 0;

        public static bool TrySpend(BoosterKind k)
        {
            return ConsumableGateway.Spend?.Invoke("booster_" + k.ToString().ToLowerInvariant()) ?? false;
        }

        public static void Add(BoosterKind k, int n)
        {
            // Inventory grants come exclusively from wallet responses.
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
                    if (!link.Active || (link.Tinted && link.Color != m.Rings[i].Color)) continue;
                    float delta = Mathf.DeltaAngle(m.Rings[i].GapAngle, link.ClampAngle);
                    float need = Mathf.Abs(delta) -
                        (PuzzleModel.GapHalfAngle - link.ClampHalfAngle) + 3f;
                    float turn = Mathf.Sign(delta) * Mathf.Max(3f, need);
                    int mate = m.LinkedPartner(i);
                    if (link.Clay) turn = turn < 0 ? -720 : 720;
                    if (!m.DirectionAllowed(i, turn) || (mate >= 0 && !m.DirectionAllowed(mate, -turn)))
                        turn = turn < 0 ? turn + (link.Clay ? 1440 : 360) : turn - (link.Clay ? 1440 : 360);
                    if (!m.DirectionAllowed(i, turn) || (mate >= 0 && !m.DirectionAllowed(mate, -turn))) continue;
                    need = Mathf.Abs(turn);
                    if (need >= best) continue;
                    best = need;
                    hint = new Hint(false, i, turn);
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
