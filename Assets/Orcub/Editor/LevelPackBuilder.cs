using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Orcub.Core;
using UnityEditor;
using UnityEngine;

namespace Orcub.EditorTools
{
    /// <summary>
    /// Builds the shipped level pack: Assets/Orcub/Resources/Orcub/levels.json (+ a CSV report next to it).
    ///
    /// Curve: levels 1-3 are hand-made tutorials, then a target difficulty that rises over the pack with a
    /// 5-level sawtooth (easy, easy+, medium, hard, breather). Ring count unlocks gradually so early boards
    /// stay small. Each slot takes the unused candidate closest to its target.
    /// </summary>
    public static class LevelPackBuilder
    {
        public const string PackPath = "Assets/Orcub/Resources/Orcub/levels.json";
        const int LevelCount = 200;
        const int CandidatesPerSize = 700;

        static readonly float[] Saw = { 0.88f, 0.96f, 1.0f, 1.14f, 0.78f };

        [MenuItem("Orcub/Levels/Build Level Pack")]
        public static void Build() => Build(LevelCount, 1234);

        public static string Build(int count, int baseSeed)
        {
            // ---- candidate pool across sizes
            var pool = new List<LevelData>();
            int seed = baseSeed;
            for (int rings = 4; rings <= 13; rings++)
            {
                int made = 0, tries = 0;
                while (made < CandidatesPerSize && tries < CandidatesPerSize * 30)
                {
                    tries++;
                    seed++;
                    var rng = new System.Random(seed * 7919);
                    var settings = new LevelGenerator.Settings
                    {
                        Rings = rings,
                        Symmetric = rng.NextDouble() < 0.6,
                        ExtraLinkChance = (float)(0.05 + rng.NextDouble() * 0.45),
                        DeepOrderBias = (float)rng.NextDouble(),
                    };
                    var lvl = LevelGenerator.TryGenerate(seed, settings);
                    if (lvl == null) continue;
                    pool.Add(lvl);
                    made++;
                }
            }

            // ---- sequence
            var used = new HashSet<LevelData>();
            var levels = new List<LevelData>();
            for (int t = 0; t < 3; t++) levels.Add(LevelGenerator.Tutorial(t));
            float minD = pool.Min(l => l.difficulty), maxD = pool.Max(l => l.difficulty);
            string lastSignature = "";
            for (int i = levels.Count; i < count; i++)
            {
                float progress = (i - 3f) / (count - 4f);
                // Gentle start, steeper middle, plateau-ish end.
                float curve = Mathf.SmoothStep(0f, 1f, Mathf.Pow(progress, 0.85f));
                float target = Mathf.Lerp(minD + 1.5f, maxD * 0.92f, curve) * Saw[(i - 3) % Saw.Length];
                int maxRings = Mathf.Clamp(5 + (i - 3) / 12, 5, 13);
                int minRings = Mathf.Clamp(4 + (i - 3) / 30, 4, 9);
                LevelData best = null;
                float bestScore = float.MaxValue;
                foreach (var c in pool)
                {
                    if (used.Contains(c) || c.rings.Length > maxRings || c.rings.Length < minRings) continue;
                    float score = Mathf.Abs(c.difficulty - target);
                    if (Signature(c) == lastSignature) score += 2f; // avoid near-identical back-to-back boards
                    if (score < bestScore) { bestScore = score; best = c; }
                }
                if (best == null) break;
                used.Add(best);
                lastSignature = Signature(best);
                levels.Add(best);
            }
            for (int i = 0; i < levels.Count; i++) levels[i].id = i + 1;

            // Preserve shipped canonical geometry across v2 upgrades.
            if (File.Exists(PackPath))
            {
                var previous = LevelPack.FromJson(File.ReadAllText(PackPath));
                if (previous.levels?.Length == count) levels = previous.levels.ToList();
            }
            foreach (var candidate in pool) candidate.rating = LevelRating.For(candidate);
            var variants = new LevelVariants[levels.Count];
            for (int i = 0; i < levels.Count; i++)
            {
                var canonical = levels[i];
                canonical.rating = LevelRating.For(canonical);
                canonical.solver = SolverMeasure.Search(canonical.ToDef());
                var choices = new List<LevelData> { canonical };
                foreach (float offset in new[] { -140f, -70f, 70f, 140f })
                {
                    if (i < 3)
                    {
                        var small = JsonUtility.FromJson<LevelData>(JsonUtility.ToJson(canonical));
                        small.seed = -100 - i * 5 - choices.Count;
                        for (int ring = 0; ring < small.rings.Length; ring++)
                            small.rings[ring].gap += choices.Count * 12f;
                        small.rating = LevelRating.For(small);
                        small.solver = SolverMeasure.Search(small.ToDef());
                        choices.Add(small);
                        continue;
                    }
                    var candidate = pool.Where(c => !choices.Any(x => x.seed == c.seed))
                        .OrderBy(c => Mathf.Abs(c.rating - canonical.rating - offset)).First();
                    // Clone: candidates can be reused by different saga slots.
                    var copy = JsonUtility.FromJson<LevelData>(JsonUtility.ToJson(candidate));
                    copy.id = i + 1;
                    copy.solver = SolverMeasure.Search(copy.ToDef());
                    choices.Add(copy);
                }
                variants[i] = new LevelVariants { choices = choices.ToArray() };
            }
            var pack = new LevelPack { levels = levels.ToArray(), variants = variants };
            Directory.CreateDirectory(Path.GetDirectoryName(PackPath));
            File.WriteAllText(PackPath, pack.ToJson());

            var csv = new StringBuilder("id,rings,links,extra,depth,parTaps,parRotation,difficulty,symmetric,seed\n");
            foreach (var l in levels)
                csv.AppendLine($"{l.id},{l.rings.Length},{l.links.Length},{l.extraLinks},{l.depth},{l.parTaps},{l.parRotation:F0},{l.difficulty:F1},{l.symmetric},{l.seed}");
            File.WriteAllText("ArtSource/levels_report.csv", csv.ToString()); // outside Resources: not shipped
            AssetDatabase.ImportAsset(PackPath);
            LevelLibrary.Reload();

            string summary = $"level pack: {levels.Count} levels from {pool.Count} candidates, difficulty {minD:F1}-{maxD:F1}";
            Debug.Log("[Orcub] " + summary);
            return summary;
        }

        static string Signature(LevelData l) => $"{l.rings.Length}-{l.links.Length}-{l.depth}-{l.symmetric}";
    }
}
