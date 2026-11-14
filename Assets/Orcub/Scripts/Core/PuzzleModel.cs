using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orcub.Core
{
    /// <summary>One open gear-ring as authored in a level.</summary>
    public struct RingDef
    {
        public Vector2 Center;
        public float Radius;
        public int Color;
        /// <summary>World angle (degrees) of the gap centre. Null = pick the widest free arc automatically.</summary>
        public float? GapAngle;

        public RingDef(float x, float y, float radius, int color, float? gapAngle = null)
        {
            Center = new Vector2(x, y);
            Radius = radius;
            Color = color;
            GapAngle = gapAngle;
        }
    }

    /// <summary>A holder welded to <see cref="Owner"/> whose clamp grips <see cref="Target"/>'s tube.</summary>
    public struct LinkDef
    {
        public int Owner;
        public int Target;

        public LinkDef(int owner, int target)
        {
            Owner = owner;
            Target = target;
        }
    }

    public sealed class LevelDef
    {
        public string Name;
        public readonly List<RingDef> Rings = new List<RingDef>();
        public readonly List<LinkDef> Links = new List<LinkDef>();
    }

    public sealed class Ring
    {
        public int Id;
        public Vector2 Center;
        public float Radius;
        public int Color;
        /// <summary>Current world angle (degrees) of the gap centre. This is the ring's rotation.</summary>
        public float GapAngle;
        public bool Removed;
        public readonly List<int> Owned = new List<int>();
        public readonly List<int> Incoming = new List<int>();
    }

    public sealed class Link
    {
        public int Id;
        public int Owner;
        public int Target;
        /// <summary>Weld position on the owner, relative to the owner's gap angle (rotates with the owner).</summary>
        public float WeldOffset;
        /// <summary>World angle of the clamp around the target's centre. Fixed while active (owner is locked).</summary>
        public float ClampAngle;
        /// <summary>Angular half-width the clamp occupies on the target tube.</summary>
        public float ClampHalfAngle;
        public bool Active = true;
    }

    public enum RotateOutcome
    {
        Rotated,
        Locked,
        Removed,
    }

    /// <summary>
    /// Pure puzzle rules, no rendering:
    ///  - A ring that still owns an active holder is locked and cannot rotate.
    ///  - Any other ring rotates freely; when its gap sweeps fully over a clamp, that clamp slips off.
    ///  - A ring with no active links at all is free and can be pulled out.
    ///  - The board is solved when every ring is pulled out.
    /// </summary>
    public sealed class PuzzleModel
    {
        public const float GapHalfAngle = 32f;      // the gap is 64 degrees wide
        public const float ClampHalfWidth = 0.24f;  // world units, half the clamp's width along the tube
        public const float WeldHalfWidth = 0.20f;
        public const float MaxStep = 3f;            // rotation sub-step so a fast drag never skips a clamp

        readonly List<Ring> _rings = new List<Ring>();
        readonly List<Link> _links = new List<Link>();

        public IReadOnlyList<Ring> Rings => _rings;
        public IReadOnlyList<Link> Links => _links;
        public string Name { get; }

        public event Action<Link> LinkReleased;
        public event Action<Ring> RingRemoved;
        public event Action Solved;

        public PuzzleModel(LevelDef def)
        {
            Name = def.Name;
            for (int i = 0; i < def.Rings.Count; i++)
            {
                var d = def.Rings[i];
                _rings.Add(new Ring { Id = i, Center = d.Center, Radius = d.Radius, Color = d.Color });
            }

            for (int i = 0; i < def.Links.Count; i++)
            {
                var d = def.Links[i];
                if (d.Owner == d.Target) throw new ArgumentException($"Link {i} links ring {d.Owner} to itself.");
                var owner = _rings[d.Owner];
                var target = _rings[d.Target];
                var link = new Link
                {
                    Id = i,
                    Owner = d.Owner,
                    Target = d.Target,
                    ClampAngle = AngleOf(owner.Center - target.Center),
                    ClampHalfAngle = Mathf.Rad2Deg * ClampHalfWidth / target.Radius,
                };
                _links.Add(link);
                owner.Owned.Add(i);
                target.Incoming.Add(i);
            }

            for (int i = 0; i < _rings.Count; i++)
            {
                var ring = _rings[i];
                var authored = def.Rings[i].GapAngle;
                ring.GapAngle = Normalize(authored ?? PickGapAngle(ring));
                foreach (var id in ring.Owned)
                {
                    var l = _links[id];
                    l.WeldOffset = Mathf.DeltaAngle(ring.GapAngle, AngleOf(_rings[l.Target].Center - ring.Center));
                }
            }
        }

        // ---------------------------------------------------------------- queries

        public bool CanRotate(int ring)
        {
            var r = _rings[ring];
            if (r.Removed) return false;
            foreach (var id in r.Owned)
                if (_links[id].Active) return false;
            return true;
        }

        public bool IsFree(int ring)
        {
            var r = _rings[ring];
            if (r.Removed) return false;
            foreach (var id in r.Owned)
                if (_links[id].Active) return false;
            foreach (var id in r.Incoming)
                if (_links[id].Active) return false;
            return true;
        }

        public bool IsSolved
        {
            get
            {
                foreach (var r in _rings)
                    if (!r.Removed) return false;
                return true;
            }
        }

        public IEnumerable<Link> ActiveOwned(int ring)
        {
            foreach (var id in _rings[ring].Owned)
                if (_links[id].Active) yield return _links[id];
        }

        /// <summary>True when the clamp fits entirely inside the target's gap.</summary>
        public bool ClampInGap(Link l)
        {
            var t = _rings[l.Target];
            return Mathf.Abs(Mathf.DeltaAngle(t.GapAngle, l.ClampAngle)) <= GapHalfAngle - l.ClampHalfAngle;
        }

        public Vector2 WeldPoint(Link l)
        {
            var o = _rings[l.Owner];
            return o.Center + Dir(o.GapAngle + l.WeldOffset) * o.Radius;
        }

        public Vector2 ClampPoint(Link l)
        {
            var t = _rings[l.Target];
            return t.Center + Dir(l.ClampAngle) * t.Radius;
        }

        // ---------------------------------------------------------------- actions

        public RotateOutcome Rotate(int ring, float deltaDegrees, List<Link> released = null)
        {
            var r = _rings[ring];
            if (r.Removed) return RotateOutcome.Removed;
            if (!CanRotate(ring)) return RotateOutcome.Locked;

            int steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(deltaDegrees) / MaxStep));
            float step = deltaDegrees / steps;
            for (int s = 0; s < steps; s++)
            {
                r.GapAngle = Normalize(r.GapAngle + step);
                foreach (var id in r.Incoming)
                {
                    var l = _links[id];
                    if (!l.Active || !ClampInGap(l)) continue;
                    l.Active = false;
                    released?.Add(l);
                    LinkReleased?.Invoke(l);
                }
            }
            return RotateOutcome.Rotated;
        }

        public bool TryRemove(int ring)
        {
            if (!IsFree(ring)) return false;
            var r = _rings[ring];
            r.Removed = true;
            RingRemoved?.Invoke(r);
            if (IsSolved) Solved?.Invoke();
            return true;
        }

        /// <summary>
        /// Checks solvability by simulation. Releasing a link only ever removes constraints, so greedily
        /// spinning every rotatable ring a full turn and pulling out every free ring is exact.
        /// Works on a fresh copy; the model itself is untouched.
        /// </summary>
        public static bool IsSolvable(LevelDef def, out List<int> removalOrder)
        {
            var m = new PuzzleModel(def);
            removalOrder = new List<int>();
            bool progress = true;
            while (progress && !m.IsSolved)
            {
                progress = false;
                for (int i = 0; i < m._rings.Count; i++)
                {
                    if (m._rings[i].Removed) continue;
                    if (m.IsFree(i))
                    {
                        m.TryRemove(i);
                        removalOrder.Add(i);
                        progress = true;
                    }
                    else if (m.CanRotate(i))
                    {
                        m.Rotate(i, 360f);
                        progress = true;
                    }
                }
            }
            return m.IsSolved;
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>Puts the gap in the middle of the widest arc not occupied by welds or clamps.</summary>
        float PickGapAngle(Ring ring)
        {
            FreeArc(ring.Id, out float start, out float length, out _);
            return start + length * 0.5f;
        }

        /// <summary>
        /// The widest arc on a ring not covered by its welds (owned holders) or clamps (incoming holders),
        /// computed from geometry only. <paramref name="tightestGap"/> is the smallest clear arc between two
        /// neighbouring features (small = clamps crowd each other).
        /// </summary>
        public void FreeArc(int ringId, out float start, out float length, out float tightestGap)
        {
            var ring = _rings[ringId];
            var features = new List<(float angle, float half)>();
            foreach (var id in ring.Owned)
            {
                var l = _links[id];
                features.Add((AngleOf(_rings[l.Target].Center - ring.Center), Mathf.Rad2Deg * WeldHalfWidth / ring.Radius));
            }
            foreach (var id in ring.Incoming)
            {
                var l = _links[id];
                features.Add((l.ClampAngle, l.ClampHalfAngle));
            }
            start = 0f;
            length = 360f;
            tightestGap = 360f;
            if (features.Count == 0) return;

            features.Sort((a, b) => Normalize(a.angle).CompareTo(Normalize(b.angle)));
            float bestStart = 0f, bestLen = -1f;
            for (int i = 0; i < features.Count; i++)
            {
                var a = features[i];
                var b = features[(i + 1) % features.Count];
                float s = Normalize(a.angle) + a.half;
                float e = Normalize(b.angle) - b.half;
                if (i == features.Count - 1) e += 360f;
                float len = e - s;
                tightestGap = Mathf.Min(tightestGap, len);
                if (len > bestLen)
                {
                    bestLen = len;
                    bestStart = s;
                }
            }
            start = bestStart;
            length = bestLen;
        }

        /// <summary>
        /// Smallest total rotation (degrees) that sweeps this ring's gap over every clamp currently on it.
        /// Explores going one way, the other way, or out-and-back. Used for par / difficulty.
        /// </summary>
        public float MinRotationToClear(int ringId)
        {
            var r = _rings[ringId];
            var offsets = new List<float>();
            foreach (var id in r.Incoming)
            {
                var l = _links[id];
                if (!l.Active) continue;
                float reach = GapHalfAngle - l.ClampHalfAngle;           // gap centre must come this close
                float d = Normalize(l.ClampAngle - r.GapAngle);          // counter-clockwise distance
                float ccw = Mathf.Max(0f, d - reach);
                float cw = Mathf.Max(0f, 360f - d - reach);
                offsets.Add(ccw);
                offsets.Add(-cw);                                         // tagged by sign below
            }
            if (offsets.Count == 0) return 0f;
            // Each clamp is satisfied by going ccw by a_i OR cw by b_i. Try every split point.
            var ccwNeed = new List<float>();
            var cwNeed = new List<float>();
            for (int i = 0; i < offsets.Count; i += 2)
            {
                ccwNeed.Add(offsets[i]);
                cwNeed.Add(-offsets[i + 1]);
            }
            int n = ccwNeed.Count;
            var idx = new List<int>();
            for (int i = 0; i < n; i++) idx.Add(i);
            idx.Sort((a, b) => ccwNeed[a].CompareTo(ccwNeed[b]));
            float best = float.MaxValue;
            // clamps idx[0..k-1] reached ccw, the rest cw
            for (int k = 0; k <= n; k++)
            {
                float a = k == 0 ? 0f : ccwNeed[idx[k - 1]];
                float b = 0f;
                for (int j = k; j < n; j++) b = Mathf.Max(b, cwNeed[idx[j]]);
                float cost = Mathf.Min(2f * a + b, a + 2f * b);
                if (a == 0f || b == 0f) cost = a + b;
                best = Mathf.Min(best, cost);
            }
            return best;
        }

        public static float AngleOf(Vector2 v) => Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg;

        public static Vector2 Dir(float degrees)
        {
            float r = degrees * Mathf.Deg2Rad;
            return new Vector2(Mathf.Cos(r), Mathf.Sin(r));
        }

        public static float Normalize(float degrees)
        {
            degrees %= 360f;
            return degrees < 0f ? degrees + 360f : degrees;
        }
    }
}
