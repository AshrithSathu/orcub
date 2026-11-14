using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orcub.Core
{
    /// <summary>
    /// Procedural board generator.
    ///
    ///  1. Layout: rings sit on a honeycomb lattice (portrait, up to 3 columns), grown as one connected blob,
    ///     optionally mirror-symmetric so boards look designed. Radii come from the three rendered sizes.
    ///  2. Links: a random spanning tree over lattice neighbours (every ring is attached) plus a few extra links.
    ///  3. Order: a random rank per ring; every link points from the higher rank (owner, locked) to the lower
    ///     (target). That makes the "who holds whom" graph acyclic, so the board is always solvable.
    ///  4. Geometry checks: no teeth collisions, stems in a comfortable range, every ring keeps a free arc for its
    ///     gap and clamps don't crowd each other. The gap starts at a random spot inside its free arc.
    ///  5. Grading: depth (rounds of unlocking), extra links and minimum rotation give a difficulty score.
    /// Deterministic for a given seed.
    /// </summary>
    public static class LevelGenerator
    {
        public sealed class Settings
        {
            public int Rings = 8;
            public float ExtraLinkChance = 0.25f;   // chance per remaining neighbour pair to add a link
            public bool Symmetric = true;
            public float DeepOrderBias = 0.5f;      // 0 = random order, 1 = order follows growth (long chains)
            public int MaxFeaturesPerRing = 4;
        }

        const float Spacing = 3.8f;                 // lattice pitch (centre to centre)
        const float TubeAndTeeth = 0.45f;           // tube half width + tooth height past the radius
        const float MinStem = 0.9f, MaxStem = 1.75f;
        static readonly float[] Radii = { 1.0f, 1.2f, 1.4f };
        const int Rows = 7;

        struct Node
        {
            public int Row, Col;
            public Vector2 Pos;
        }

        /// <summary>Lattice: even rows have columns -1,0,1; odd rows have -0.5,+0.5 (in units of Spacing).</summary>
        static List<Node> Lattice()
        {
            var nodes = new List<Node>();
            float rowH = Spacing * 0.8660254f;
            for (int row = 0; row < Rows; row++)
            {
                bool even = row % 2 == 0;
                int count = even ? 3 : 2;
                for (int c = 0; c < count; c++)
                {
                    float x = even ? (c - 1) * Spacing : (c - 0.5f) * Spacing;
                    nodes.Add(new Node { Row = row, Col = c, Pos = new Vector2(x, -row * rowH) });
                }
            }
            return nodes;
        }

        static bool Adjacent(Node a, Node b) => Mathf.Abs((a.Pos - b.Pos).magnitude - Spacing) < 0.05f;

        static int MirrorOf(List<Node> nodes, int i)
        {
            var p = nodes[i].Pos;
            for (int j = 0; j < nodes.Count; j++)
                if ((nodes[j].Pos - new Vector2(-p.x, p.y)).sqrMagnitude < 0.01f) return j;
            return i;
        }

        /// <summary>Try to build one board. Returns null if this seed produced something invalid.</summary>
        public static LevelData TryGenerate(int seed, Settings s)
        {
            var rng = new System.Random(seed);
            var lattice = Lattice();
            int n = Mathf.Clamp(s.Rings, 2, lattice.Count);

            // ---- 1. grow a connected blob (mirror pairs added together when symmetric)
            var chosen = new List<int>();
            var inSet = new HashSet<int>();
            var growthOrder = new List<int>();
            int startRow = rng.Next(0, 2) * 1;
            int start = lattice.FindIndex(nd => nd.Row == startRow && Mathf.Abs(nd.Pos.x) < 0.01f);
            if (start < 0) start = lattice.FindIndex(nd => nd.Row == 1);
            void AddNode(int i)
            {
                if (inSet.Add(i)) { chosen.Add(i); growthOrder.Add(i); }
            }
            AddNode(start);
            if (s.Symmetric) AddNode(MirrorOf(lattice, start));
            int guard = 0;
            while (chosen.Count < n && guard++ < 500)
            {
                var frontier = new List<int>();
                for (int i = 0; i < lattice.Count; i++)
                {
                    if (inSet.Contains(i)) continue;
                    foreach (int c in chosen)
                        if (Adjacent(lattice[i], lattice[c])) { frontier.Add(i); break; }
                }
                if (frontier.Count == 0) return null;
                int pick = frontier[rng.Next(frontier.Count)];
                if (s.Symmetric)
                {
                    int mirror = MirrorOf(lattice, pick);
                    int add = mirror == pick ? 1 : 2;
                    if (chosen.Count + add > n)
                    {
                        // need exactly one more: only a centre-column node keeps the symmetry
                        var centres = frontier.FindAll(f => MirrorOf(lattice, f) == f);
                        if (centres.Count == 0) return null;
                        pick = centres[rng.Next(centres.Count)];
                        mirror = pick;
                    }
                    AddNode(pick);
                    AddNode(mirror);
                }
                else AddNode(pick);
            }
            if (chosen.Count != n) return null;

            // ---- radii (mirror pairs share a radius); centres + small symmetric jitter
            var radius = new float[n];
            var pos = new Vector2[n];
            var indexOf = new Dictionary<int, int>();
            for (int k = 0; k < n; k++) indexOf[chosen[k]] = k;
            for (int k = 0; k < n; k++)
            {
                int li = chosen[k];
                int mirror = MirrorOf(lattice, li);
                if (s.Symmetric && indexOf.TryGetValue(mirror, out int mk) && mk < k)
                {
                    radius[k] = radius[mk];
                    pos[k] = new Vector2(-pos[mk].x, pos[mk].y);
                    continue;
                }
                radius[k] = Radii[rng.Next(Radii.Length)];
                var jitter = new Vector2((float)(rng.NextDouble() - 0.5) * 0.3f, (float)(rng.NextDouble() - 0.5) * 0.3f);
                if (s.Symmetric && mirror == li) jitter.x = 0f;
                pos[k] = lattice[li].Pos + jitter;
            }

            // ---- teeth must never touch, linked or not
            for (int a = 0; a < n; a++)
            for (int b = a + 1; b < n; b++)
            {
                float d = (pos[a] - pos[b]).magnitude;
                if (d < radius[a] + radius[b] + 2f * TubeAndTeeth + 0.05f) return null;
            }

            // ---- 2. candidate links = lattice neighbours with a comfortable stem
            var candidates = new List<(int a, int b)>();
            for (int a = 0; a < n; a++)
            for (int b = a + 1; b < n; b++)
            {
                if (!Adjacent(lattice[chosen[a]], lattice[chosen[b]])) continue;
                float stem = (pos[a] - pos[b]).magnitude - radius[a] - radius[b];
                if (stem >= MinStem && stem <= MaxStem) candidates.Add((a, b));
            }

            // random spanning tree (randomised Kruskal)
            Shuffle(candidates, rng);
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            var edges = new List<(int a, int b)>();
            var rest = new List<(int a, int b)>();
            foreach (var e in candidates)
            {
                int ra = Find(e.a), rb = Find(e.b);
                if (ra != rb) { parent[ra] = rb; edges.Add(e); }
                else rest.Add(e);
            }
            if (edges.Count != n - 1) return null; // not connected with valid stems
            int extra = 0;
            foreach (var e in rest)
                if (rng.NextDouble() < s.ExtraLinkChance) { edges.Add(e); extra++; }

            // ---- 3. solve order -> link direction (owner has the higher rank)
            var rank = new float[n];
            for (int k = 0; k < n; k++)
            {
                float growth = growthOrder.IndexOf(chosen[k]) / (float)Mathf.Max(1, n - 1);
                rank[k] = Mathf.Lerp((float)rng.NextDouble(), 1f - growth, s.DeepOrderBias) + (float)rng.NextDouble() * 0.05f;
            }
            var def = new LevelDef();
            var colors = PickColors(n, edges, rng);
            for (int k = 0; k < n; k++) def.Rings.Add(new RingDef(pos[k].x, pos[k].y, radius[k], colors[k]));
            foreach (var e in edges)
                def.Links.Add(rank[e.a] > rank[e.b] ? new LinkDef(e.a, e.b) : new LinkDef(e.b, e.a));

            // ---- 4. geometry: crowding + room for each gap, then place gaps randomly inside their free arc
            var probe = new PuzzleModel(def);
            for (int k = 0; k < n; k++)
            {
                var ring = probe.Rings[k];
                if (ring.Owned.Count + ring.Incoming.Count > s.MaxFeaturesPerRing) return null;
                probe.FreeArc(k, out float arcStart, out float arcLen, out float tightest);
                float need = PuzzleModel.GapHalfAngle * 2f + 16f;
                if (arcLen < need) return null;
                if (tightest < 14f) return null;
                float slack = arcLen - need;
                float gap = arcStart + need * 0.5f + (float)rng.NextDouble() * slack;
                var r = def.Rings[k];
                def.Rings[k] = new RingDef(r.Center.x, r.Center.y, r.Radius, r.Color, PuzzleModel.Normalize(gap));
            }

            // ---- 5. grade
            if (!LevelMetrics.Evaluate(def, out int depth, out float rotation, out int taps)) return null;

            // centre the board on the origin
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            foreach (var r in def.Rings)
            {
                min = Vector2.Min(min, r.Center - Vector2.one * r.Radius);
                max = Vector2.Max(max, r.Center + Vector2.one * r.Radius);
            }
            var centre = (min + max) * 0.5f;

            var data = new LevelData
            {
                seed = seed,
                depth = depth,
                extraLinks = extra,
                parRotation = rotation,
                parTaps = taps,
                symmetric = s.Symmetric,
                difficulty = LevelMetrics.Difficulty(n, depth, extra, rotation),
                rings = new RingData[n],
                links = new LinkData[def.Links.Count],
            };
            for (int k = 0; k < n; k++)
            {
                var r = def.Rings[k];
                data.rings[k] = new RingData
                {
                    x = Round(r.Center.x - centre.x), y = Round(r.Center.y - centre.y), r = r.Radius,
                    color = r.Color, gap = Round(r.GapAngle ?? 0f),
                };
            }
            for (int i = 0; i < def.Links.Count; i++)
                data.links[i] = new LinkData { owner = def.Links[i].Owner, target = def.Links[i].Target };
            return data;
        }

        /// <summary>Hand-shaped tiny boards for the first levels (teach: turn a ring, then order matters).</summary>
        public static LevelData Tutorial(int step)
        {
            var def = new LevelDef();
            switch (step)
            {
                case 0: // one clamp: turn the free ring
                    def.Rings.Add(new RingDef(0f, 1.9f, 1.2f, 0));
                    def.Rings.Add(new RingDef(0f, -1.9f, 1.2f, 3));
                    def.Links.Add(new LinkDef(0, 1));
                    break;
                case 1: // a chain of three: order matters
                    def.Rings.Add(new RingDef(0f, 3.8f, 1.2f, 1));
                    def.Rings.Add(new RingDef(0f, 0f, 1.2f, 2));
                    def.Rings.Add(new RingDef(0f, -3.8f, 1.2f, 4));
                    def.Links.Add(new LinkDef(0, 1));
                    def.Links.Add(new LinkDef(1, 2));
                    break;
                default: // a ring holding two others
                    def.Rings.Add(new RingDef(0f, 2.2f, 1.4f, 0));
                    def.Rings.Add(new RingDef(-1.9f, -1.6f, 1.0f, 6));
                    def.Rings.Add(new RingDef(1.9f, -1.6f, 1.0f, 8));
                    def.Links.Add(new LinkDef(0, 1));
                    def.Links.Add(new LinkDef(0, 2));
                    break;
            }
            LevelMetrics.Evaluate(def, out int depth, out float rotation, out int taps);
            var model = new PuzzleModel(def);
            var data = new LevelData
            {
                seed = -1 - step, depth = depth, parRotation = rotation, parTaps = taps, symmetric = true,
                difficulty = LevelMetrics.Difficulty(def.Rings.Count, depth, 0, rotation),
                rings = new RingData[def.Rings.Count], links = new LinkData[def.Links.Count],
            };
            for (int k = 0; k < def.Rings.Count; k++)
            {
                var r = def.Rings[k];
                data.rings[k] = new RingData { x = r.Center.x, y = r.Center.y, r = r.Radius, color = r.Color, gap = Round(model.Rings[k].GapAngle) };
            }
            for (int i = 0; i < def.Links.Count; i++)
                data.links[i] = new LinkData { owner = def.Links[i].Owner, target = def.Links[i].Target };
            return data;
        }

        static float Round(float v) => Mathf.Round(v * 1000f) / 1000f;

        static void Shuffle<T>(List<T> list, System.Random rng)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        /// <summary>Palette colours with no two linked rings sharing a colour.</summary>
        static int[] PickColors(int n, List<(int a, int b)> edges, System.Random rng)
        {
            const int paletteSize = 10;
            var colors = new int[n];
            var order = new List<int>();
            for (int i = 0; i < paletteSize; i++) order.Add(i);
            Shuffle(order, rng);
            for (int k = 0; k < n; k++)
            {
                var banned = new HashSet<int>();
                foreach (var e in edges)
                {
                    if (e.a == k && e.b < k) banned.Add(colors[e.b]);
                    if (e.b == k && e.a < k) banned.Add(colors[e.a]);
                }
                int c = order[k % paletteSize];
                for (int t = 0; t < paletteSize && banned.Contains(c); t++) c = order[(k + t + 1) % paletteSize];
                colors[k] = c;
            }
            return colors;
        }
    }
}
