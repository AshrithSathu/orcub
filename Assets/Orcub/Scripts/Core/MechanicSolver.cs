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

        public static string MemoKey(PuzzleModel m)
        {
            var key = new StringBuilder().Append(m.Moves).Append(':');
            foreach (var r in m.Rings) key.Append(r.Removed ? 'R' : r.Frozen ? 'F' : '.');
            foreach (var l in m.Links)
            {
                key.Append(l.Active ? 'A' : '.');
                if (l.SpringMoves > 0) key.Append('s').Append(l.ReleasedAt - m.Moves).Append(';');
                if (l.Clay) key.Append('c').Append(l.Passes).Append(';');
            }
            return key.ToString();
        }

        public static bool HasMechanics(LevelDef def)
        {
            foreach (var r in def.Rings)
                if (r.Ratchet != 0 || r.GearGroup != 0 || r.KeyId != 0 || r.PadlockId != 0 || r.RustAfter > 0 || r.OilId != 0) return true;
            foreach (var l in def.Links) if (l.Tinted || l.Clay || l.SpringMoves > 0) return true;
            return false;
        }

        // ------------------------------------------------------------ per-board cache (review e, M4)
        // Level start used to run this exact search 5+ times on the main thread (LevelRules + LevelRating, twice
        // each, plus the sheet). Results are cached per board geometry so a level start does at most one search,
        // and Warm() runs it on a worker thread ahead of time (next level, level-start sheet).
        static readonly Dictionary<string, (bool solved, Result result)> Cache = new Dictionary<string, (bool, Result)>();
        static readonly object CacheLock = new object();
        const int CacheCapacity = 256;
        // Review i, L9: boards solved ahead of time by Warm() live in their own small LRU, so a generation burst
        // that fills (and clears) the main cache never evicts the next level's board before it is played.
        static readonly Dictionary<string, (bool solved, Result result)> Warmed = new Dictionary<string, (bool, Result)>();
        static readonly LinkedList<string> WarmedOrder = new LinkedList<string>();
        const int WarmedCapacity = 32;

        /// <summary>Stable key for a board: everything the search reads.</summary>
        public static string Fingerprint(LevelDef def)
        {
            var k = new StringBuilder(def.Rings.Count * 48 + def.Links.Count * 16);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var r in def.Rings)
                k.Append(r.Center.x.ToString("R", inv)).Append(',').Append(r.Center.y.ToString("R", inv)).Append(',')
                 .Append(r.Radius.ToString("R", inv)).Append(',').Append(r.Color).Append(',')
                 .Append(r.GapAngle.HasValue ? r.GapAngle.Value.ToString("R", inv) : "-").Append(',')
                 .Append(r.Ratchet).Append(',').Append(r.GearGroup).Append(',').Append(r.KeyId).Append(',')
                 .Append(r.PadlockId).Append(',').Append(r.OilId).Append(',').Append(r.RustOilId).Append(',')
                 .Append(r.RustAfter).Append(';');
            k.Append('|');
            foreach (var l in def.Links)
                k.Append(l.Owner).Append('>').Append(l.Target).Append(l.Tinted ? "t" : "").Append(l.Clay ? "c" : "")
                 .Append(',').Append(l.Color).Append(',').Append(l.SpringMoves).Append(';');
            return k.ToString();
        }

        /// <summary>Cached exact search (no removal order). Thread-safe; results are shared, treat them as read-only.</summary>
        public static bool SolveCached(LevelDef def, out Result result) => SolveCached(def, out result, warm: false);

        static bool SolveCached(LevelDef def, out Result result, bool warm)
        {
            string key = Fingerprint(def);
            lock (CacheLock)
            {
                if (Warmed.TryGetValue(key, out var w)) { result = w.result; return w.solved; }
                if (Cache.TryGetValue(key, out var hit)) { result = hit.result; return hit.solved; }
            }
            bool solved = Solve(def, out result, out _);
            lock (CacheLock)
            {
                if (warm)
                {
                    if (!Warmed.ContainsKey(key)) WarmedOrder.AddLast(key);
                    Warmed[key] = (solved, result);
                    while (WarmedOrder.Count > WarmedCapacity) { Warmed.Remove(WarmedOrder.First.Value); WarmedOrder.RemoveFirst(); }
                }
                else
                {
                    if (Cache.Count >= CacheCapacity) Cache.Clear();
                    Cache[key] = (solved, result);
                }
            }
            return solved;
        }

        /// <summary>True once <see cref="SolveCached"/> has a result for this board (tests / tools).</summary>
        public static bool IsCached(LevelDef def)
        {
            string key = Fingerprint(def);
            lock (CacheLock) return Cache.ContainsKey(key) || Warmed.ContainsKey(key);
        }

        /// <summary>Solves mechanic boards on a worker thread so a later level start finds them cached.</summary>
        public static System.Threading.Tasks.Task Warm(IEnumerable<LevelDef> defs)
        {
            var list = new List<LevelDef>();
            foreach (var d in defs) if (d != null && HasMechanics(d) && !IsCached(d)) list.Add(d);
            if (list.Count == 0) return System.Threading.Tasks.Task.CompletedTask;
            return System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var d in list)
                    try { SolveCached(d, out _, warm: true); }
                    catch (Exception) { /* the main thread reports it when the board is used */ }
            });
        }

        public static bool Solve(LevelDef def, out Result result, out List<int> order, int budgetMilliseconds = int.MaxValue)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            if (def.Rings.Count > 20) throw new ArgumentException("Search supports at most 20 rings");
            var m = new PuzzleModel(def);
            var stats = new Result();
            var memo = new Dictionary<string, int>();
            var path = new List<int>();
            List<int> bestOrder = null;
            int branches = 0;
            int Visit()
            {
                if (clock.ElapsedMilliseconds >= budgetMilliseconds) throw new TimeoutException("Solver budget exceeded");
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
                string state = MemoKey(m);
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
