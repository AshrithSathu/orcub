using System;
using System.Collections.Generic;

namespace Orcub.Core
{
    [Serializable]
    public sealed class InfiniteCurve
    {
        public int BaseRings = 6, MaxRings = 10, ChaptersPerRing = 12, SpotlightEvery = 5;
        public int GenerationBudgetMs = 80, CacheEntries = 100, PrefetchCount = 5;
        public void Validate()
        {
            if (BaseRings < 4 || MaxRings < BaseRings || MaxRings > 10 || ChaptersPerRing < 1 || SpotlightEvery < 2 ||
                GenerationBudgetMs < 10 || GenerationBudgetMs > 500 || CacheEntries < 5 || CacheEntries > 1000 || PrefetchCount != 5)
                throw new ArgumentException("Invalid infinite curve");
        }
    }

    /// <summary>PCG-XSH-RR 64/32. Overflow and seed mixing are explicitly defined.</summary>
    public struct LevelPcg
    {
        ulong state;
        public LevelPcg(ulong seed) { state = 0; Next(); state = unchecked(state + seed); Next(); }
        public uint Next()
        {
            ulong old = state;
            state = unchecked(old * 6364136223846793005UL + 1442695040888963407UL);
            uint value = (uint)(((old >> 18) ^ old) >> 27);
            int rotation = (int)(old >> 59);
            return (value >> rotation) | (value << ((-rotation) & 31));
        }
        public int Range(int count) => (int)(Next() % (uint)count);
    }

    public static class InfiniteGenerator
    {
        public const int Version = 1;
        public static string ContentVersion(InfiniteCurve curve) => $"{Version}-{curve.BaseRings}-{curve.MaxRings}-{curve.ChaptersPerRing}-{curve.SpotlightEvery}";
        public static readonly string[] MechanicNames = { "ratchet", "linked", "colour_clamp", "key_padlock", "rust_oil", "spring", "clay" };
        public static bool IsBreather(int index) => index % 20 == 0 || index % 5 == 0;
        public static int RingCount(int index, int variant, InfiniteCurve curve)
        {
            int growth = Math.Min(curve.MaxRings - curve.BaseRings, (index / 20 - 10) / curve.ChaptersPerRing);
            int saw = IsBreather(index) ? -2 : index % 5 == 4 ? 1 : 0;
            return Math.Max(4, Math.Min(curve.MaxRings, curve.BaseRings + growth + variant / 2 + saw + (index % 20 == 19 ? 2 : 0)));
        }

        /// <summary>
        /// Bounded spine/fork family: only two frontier leaves can branch, so exact search is linear.
        /// Integer geometry and discrete angles determine content; no floating point acceptance decisions.
        /// </summary>
        public static LevelData Generate(int index, int variant, InfiniteCurve curve = null)
        {
            curve = curve ?? new InfiniteCurve(); curve.Validate();
            if (index < 200 || index == int.MaxValue || variant < 0 || variant > 4) throw new ArgumentOutOfRangeException();
            var data = Construct(index, variant, curve);
            try
            {
                if (!MechanicSolver.Solve(data.ToDef(), out var result, out _, curve.GenerationBudgetMs)) throw new InvalidOperationException("Invalid infinite template");
                if (result.MinimumMoves != data.solver.MinimumMoves) throw new InvalidOperationException("Template certificate mismatch");
            }
            catch (TimeoutException)
            {
                // The safe family has a linear witness. Use it instead of changing identity on slow devices.
                VerifyWitness(data);
            }
            return data;
        }

        public static void VerifyWitness(LevelData data)
        {
            var model = new PuzzleModel(data.ToDef());
            for (int step = 0; step <= data.rings.Length; step++)
            {
                bool removed;
                do { removed = false; for (int i = 0; i < data.rings.Length; i++) removed |= model.TryRemove(i); } while (removed);
                if (model.IsSolved) return;
                int pick = -1;
                for (int i = data.rings.Length - 2; i >= 0; i--) if (model.CanRotate(i)) { pick = i; break; }
                if (pick < 0 && model.CanRotate(data.rings.Length - 1)) pick = data.rings.Length - 1;
                if (pick < 0) break;
                float sweep = model.DirectionAllowed(pick, 720) ? 720 : -720;
                model.Rotate(pick, sweep); model.CompleteMove();
            }
            throw new InvalidOperationException("Invalid infinite witness");
        }

        internal static LevelData Construct(int index, int variant, InfiniteCurve curve)
        {
            int n = RingCount(index, variant, curve);
            var rng = new LevelPcg(unchecked(((ulong)(uint)index << 32) ^ ((uint)variant * 747796405u) ^ Version));
            var data = new LevelData { id = index + 1, gen_version = ContentVersion(curve), seed = unchecked((int)rng.Next()), is_boss = index % 20 == 19,
                rings = new RingData[n], links = new LinkData[n - 1], symmetric = true };
            int a = n - 2, b = n - 1;
            int mirror = rng.Range(2) == 0 ? 1 : -1;
            for (int i = 0; i < n; i++)
            {
                // A narrow spine ends in two accessible fork leaves; gaps start clear of every weld.
                data.rings[i] = new RingData { x = i < a ? 0 : (i == a ? -1.9f : 1.9f) * mirror,
                    y = i < a ? (a - i) * 3.8f : .5f, r = 1.2f,
                    color = (i + rng.Range(3) * 3) % 10, gap = i < a ? new[] { 0, 15, 345, 180, 195, 165 }[rng.Range(6)] : 270 };
                if (i > 0) data.links[i - 1] = new LinkData { owner = i < a ? i - 1 : a - 1, target = i };
            }
            // Use millimetre quantisation for centred geometry, including at large saga indices.
            float centre = (data.rings[0].y + .5f) / 2;
            for (int i = 0; i < n; i++) data.rings[i].y = (float)Math.Round((data.rings[i].y - centre) * 1000) / 1000;
            int emphasis = (index / 20 - 10) % 7;
            var selected = new List<int> { emphasis };
            int other = (emphasis + 1 + index % 6) % 7;
            bool incompatible = (emphasis == 1 && other == 3) || (emphasis == 3 && other == 1);
            if (!IsBreather(index) && !incompatible && other != emphasis) selected.Add(other);
            if ((index / 20 - 9) % curve.SpotlightEvery == 0) selected = new List<int> { emphasis };
            var names = new List<string>();
            foreach (int mechanic in selected)
            {
                names.Add(MechanicNames[mechanic]);
                switch (mechanic)
                {
                    case 0: data.rings[a].ratchet = variant % 2 == 0 ? 1 : -1; break;
                    case 1: data.rings[a].gearGroup = data.rings[b].gearGroup = 1; break;
                    case 2: data.links[n - 2].tinted = true; data.links[n - 2].color = data.rings[b].color; break;
                    case 3: data.rings[a].keyId = 1; data.rings[b].padlockId = 1; break;
                    case 4: data.rings[a].oilId = 1; data.rings[b].rustOilId = 1; data.rings[b].rustAfter = n + 2; break;
                    case 5: data.links[n - 2].springMoves = 2; break;
                    case 6: data.links[n - 2].clay = true; break;
                }
            }
            data.mechanics = names.ToArray();
            int minimum = n - (selected.Contains(1) ? 2 : 1);
            data.solver = new SolverMeasure { MinimumMoves = minimum };
            data.depth = minimum;
            var model = new PuzzleModel(data.ToDef());
            foreach (var r in model.Rings) data.parRotation += model.MinRotationToClear(r.Id);
            data.parRotation = (int)Math.Round(data.parRotation);
            data.parTaps = (int)Math.Ceiling(data.parRotation / 60);
            data.difficulty = (n * 90 + minimum * 162 + (int)data.parRotation) / 90f;
            float mechanics = 0;
            foreach (int m in selected) mechanics += m == 0 ? 1.5f : m == 1 ? 2.5f : m == 2 ? 1.5f : m == 3 ? 3 : m == 4 ? 4 : m == 5 ? 2 : 3;
            data.rating = Math.Min(1800, 650 + 12 * mechanics + 12 * data.difficulty + 8 * minimum);
            return data;
        }
    }
}
