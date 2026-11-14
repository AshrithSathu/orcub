using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orcub.Core
{
    public static class SagaGenerator
    {
        static readonly string[] Names = { "ratchet", "linked", "colour_clamp", "key_padlock", "rust_oil", "spring", "clay" };

        public static LevelData Generate(int slot, int variant, int baseSeed = 1234)
        {
            int chapter = slot / 20 + 1;
            if (chapter < 2 || chapter > 10) throw new ArgumentOutOfRangeException(nameof(slot));
            bool intro = slot % 20 == 0, boss = slot % 20 == 19;
            int count = boss ? 14 + variant : intro ? 4 + variant / 2 : Mathf.Clamp(5 + chapter / 2 + variant + slot % 4, 5, 13);
            for (int attempt = 0; attempt < 20000; attempt++)
            {
                int seed = baseSeed + slot * 100000 + variant * 20000 + attempt;
                var data = LevelGenerator.TryGenerate(seed, new LevelGenerator.Settings {
                    Rings = count, Symmetric = false, ExtraLinkChance = variant * .07f, DeepOrderBias = .2f + variant * .15f });
                if (data == null) continue;
                var leaves = new List<int>();
                for (int i = 0; i < data.rings.Length; i++)
                {
                    bool owned = false;
                    foreach (var link in data.links) if (link.owner == i) owned = true;
                    if (!owned) leaves.Add(i);
                }
                // Two accessible leaves make a gear pair that cannot introduce a dependency cycle.
                bool Wants(int mechanic) => chapter >= 9 || (boss && chapter >= mechanic + 2) || chapter == mechanic + 2;
                if ((Wants(1) || Wants(3) || Wants(4)) && leaves.Count < 2) continue;
                var present = new List<string>();
                for (int mechanic = 0; mechanic < Names.Length; mechanic++)
                {
                    if (!Wants(mechanic)) continue;
                    present.Add(Names[mechanic]);
                    int leaf = leaves[0];
                    switch (mechanic)
                    {
                        case 0: data.rings[leaf].ratchet = variant % 2 == 0 ? 1 : -1; break;
                        case 1: data.rings[leaves[0]].gearGroup = data.rings[leaves[1]].gearGroup = 1; break;
                        case 2: data.links[0].tinted = true; data.links[0].color = data.rings[data.links[0].target].color; break;
                        case 3:
                            data.rings[leaves[0]].keyId = 1;
                            data.rings[leaves[1]].padlockId = 1; break;
                        case 4:
                            data.rings[leaves[0]].oilId = 1;
                            data.rings[leaves[1]].rustOilId = 1;
                            data.rings[leaves[1]].rustAfter = count + 2; break;
                        case 5: data.links[0].springMoves = intro ? 4 : 2; break;
                        case 6: data.links[0].clay = true; break;
                    }
                }
                // A key must remain accessible independently of the ring it unlocks.
                if (Wants(1) && Wants(3))
                {
                    if (leaves.Count < 3) continue;
                    data.rings[leaves[0]].keyId = 0; data.rings[leaves[2]].keyId = 1;
                }
                data.id = slot + 1; data.is_boss = boss;
                data.mechanic_intro = intro ? "mechanic_intro." + (chapter >= 9 ? "mix" : Names[chapter - 2]) : null;
                data.mechanics = present.ToArray();
                var def = data.ToDef();
                if (!MechanicSolver.Solve(def, out var solved, out _)) continue;
                data.solver = SolverMeasure.Search(def);
                data.depth = solved.MinimumMoves; data.parRotation = solved.Rotation; data.parTaps = solved.Taps;
                data.difficulty = LevelMetrics.Difficulty(count, data.depth, data.extraLinks, data.parRotation) + present.Count * 3;
                data.rating = LevelRating.For(def);
                return data;
            }
            throw new InvalidOperationException($"No valid saga board for slot {slot}, variant {variant}");
        }
    }
}
