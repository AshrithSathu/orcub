using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orcub.Core
{
    /// <summary>Serializable form of a level (JSON via JsonUtility) plus the metrics it was graded with.</summary>
    [Serializable]
    public sealed class LevelData
    {
        public int id;
        public string gen_version; // null for curated; algorithm plus content-curve fingerprint otherwise
        public string mechanic_intro;
        public bool is_boss;
        public string[] mechanics;
        public int seed;
        public float rating;
        public SolverMeasure solver;
        public float difficulty;
        public int depth;            // longest forced chain: how many "rounds" of unlocking
        public int extraLinks;       // links beyond a spanning tree (more crossing constraints)
        public float parRotation;    // minimum total degrees of turning to solve
        public int parTaps;          // same, in 60-degree tap spins (future star rating / turn limits)
        public bool symmetric;
        public RingData[] rings;
        public LinkData[] links;

        public LevelDef ToDef()
        {
            var def = new LevelDef { Name = $"Level {id}", IsBoss = is_boss, MechanicIntro = mechanic_intro, Mechanics = mechanics };
            foreach (var r in rings) def.Rings.Add(new RingDef(r.x, r.y, r.r, r.color, r.gap) {
                Ratchet = r.ratchet, GearGroup = r.gearGroup, KeyId = r.keyId, PadlockId = r.padlockId,
                OilId = r.oilId, RustOilId = r.rustOilId, RustAfter = r.rustAfter });
            foreach (var l in links) def.Links.Add(new LinkDef(l.owner, l.target) { Tinted = l.tinted, Color = l.color, Clay = l.clay, SpringMoves = l.springMoves });
            return def;
        }
    }

    [Serializable]
    public struct RingData
    {
        public float x, y, r;
        public int color;
        public int ratchet, gearGroup, keyId, padlockId, oilId, rustOilId, rustAfter;
        public float gap;            // world angle of the gap centre at the start
    }

    [Serializable]
    public struct LinkData
    {
        public int owner, target;
        public bool tinted, clay;
        public int color, springMoves;
    }

    [Serializable]
    public sealed class LevelPack
    {
        public int version = 2;
        public LevelVariants[] variants;
        public LevelData[] levels;

        public static LevelPack FromJson(string json) => JsonUtility.FromJson<LevelPack>(json);
        public string ToJson(bool pretty = false) => JsonUtility.ToJson(this, pretty);
    }

    [Serializable]
    public sealed class LevelVariants { public LevelData[] choices; }

    /// <summary>Solution metrics computed by simulation.</summary>
    public static class LevelMetrics
    {
        /// <summary>
        /// Plays the level optimally-ish (each ring turned once, by its minimum sweep, in dependency order) and
        /// returns depth (rounds of unlocking), total rotation, and tap count.
        /// </summary>
        public static bool Evaluate(LevelDef def, out int depth, out float rotation, out int taps)
        {
            if (MechanicSolver.HasMechanics(def))
            {
                bool solved = MechanicSolver.SolveCached(def, out var result);
                depth = result.MinimumMoves; rotation = result.Rotation; taps = result.Taps;
                return solved;
            }
            var m = new PuzzleModel(def);
            depth = 0;
            rotation = 0f;
            taps = 0;
            int guard = 0;
            while (!m.IsSolved && guard++ < 200)
            {
                // One "round": everything currently free comes out, then every ring that can turn clears its clamps.
                bool progress = false;
                for (int i = 0; i < m.Rings.Count; i++)
                    if (m.IsFree(i)) { m.TryRemove(i); progress = true; }
                if (m.IsSolved) break;

                var turnable = new List<int>();
                for (int i = 0; i < m.Rings.Count; i++)
                {
                    if (!m.CanRotate(i) || m.Rings[i].Removed) continue;
                    foreach (var id in m.Rings[i].Incoming)
                        if (m.Links[id].Active) { turnable.Add(i); break; }
                }
                if (turnable.Count > 0)
                {
                    depth++;
                    foreach (int i in turnable)
                    {
                        float need = m.MinRotationToClear(i);
                        rotation += need;
                        taps += Mathf.Max(1, Mathf.CeilToInt(need / 60f));
                        m.Rotate(i, 360f); // guarantee release regardless of direction choice
                    }
                    progress = true;
                }
                if (!progress) return false;
            }
            return m.IsSolved;
        }

        public static float Difficulty(int rings, int depth, int extraLinks, float rotation) =>
            rings * 1.0f + depth * 1.8f + extraLinks * 1.1f + rotation / 90f;
    }
}
