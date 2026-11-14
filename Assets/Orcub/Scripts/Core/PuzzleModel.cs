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
        public int Ratchet; // -1 clockwise, +1 counter-clockwise, 0 unrestricted
        public int GearGroup; // positive id shared by exactly two rings
        public int KeyId, PadlockId, OilId, RustOilId, RustAfter;

        public RingDef(float x, float y, float radius, int color, float? gapAngle = null)
        {
            Center = new Vector2(x, y);
            Radius = radius;
            Color = color;
            GapAngle = gapAngle;
            Ratchet = GearGroup = KeyId = PadlockId = OilId = RustOilId = RustAfter = 0;
        }
    }

    /// <summary>A holder welded to <see cref="Owner"/> whose clamp grips <see cref="Target"/>'s tube.</summary>
    public struct LinkDef
    {
        public int Owner;
        public int Target;
        public bool Tinted, Clay;
        public int Color, SpringMoves;

        public LinkDef(int owner, int target)
        {
            Owner = owner;
            Target = target;
            Tinted = Clay = false; Color = SpringMoves = 0;
        }
    }

    public sealed class LevelDef
    {
        public string Name;
        public bool IsBoss;
        public string MechanicIntro;
        public string[] Mechanics;
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
        public int Ratchet, GearGroup, KeyId, PadlockId, OilId, RustOilId, RustAfter;
        public bool Frozen;
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
        public bool Tinted, Clay, InGap;
        public int Color, SpringMoves, Passes, ReleasedAt;
    }

    public sealed class ModelSnapshot
    {
        public float[] Gaps;
        public bool[] LinkActive;
        public bool[] Removed;
        public bool[] Frozen, InGap;
        public int[] Passes, ReleasedAt;
        public int Moves;
    }

    public enum RotateOutcome
    {
        Rotated,
        Locked,
        Removed,
        WrongDirection,
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
        public int Moves { get; private set; }
        public event Action<Ring> KeyCollected, OilCollected, RingFrozen;
        public event Action<Link> ClampReclosed;

        public event Action<Link> LinkReleased;
        public event Action<Ring> RingRemoved;
        public event Action Solved;

        public PuzzleModel(LevelDef def)
        {
            Name = def.Name;
            for (int i = 0; i < def.Rings.Count; i++)
            {
                var d = def.Rings[i];
                _rings.Add(new Ring { Id = i, Center = d.Center, Radius = d.Radius, Color = d.Color,
                    Ratchet = d.Ratchet, GearGroup = d.GearGroup, KeyId = d.KeyId,
                    PadlockId = d.PadlockId, OilId = d.OilId, RustOilId = d.RustOilId, RustAfter = d.RustAfter });
            }

            var groups = new Dictionary<int, int>();
            foreach (var r in def.Rings)
            {
                if (r.Ratchet < -1 || r.Ratchet > 1 || r.GearGroup < 0 || r.RustAfter < 0)
                    throw new ArgumentException("Invalid ring mechanic configuration.");
                if (r.GearGroup > 0) groups[r.GearGroup] = groups.TryGetValue(r.GearGroup, out int n) ? n + 1 : 1;
            }
            foreach (int n in groups.Values) if (n != 2) throw new ArgumentException("Each gear group must contain exactly two rings.");
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
                    Tinted = d.Tinted, Color = d.Color, Clay = d.Clay, SpringMoves = d.SpringMoves,
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
            if (!LocallyRotatable(ring)) return false;
            int mate = LinkedPartner(ring);
            return mate < 0 || LocallyRotatable(mate);
        }

        public int LinkedPartner(int ring)
        {
            int group = _rings[ring].GearGroup;
            if (group == 0) return -1;
            foreach (var r in _rings)
                if (r.Id != ring && r.GearGroup == group && !r.Removed) return r.Id;
            return -1;
        }

        public bool IsPadlocked(int ring)
        {
            int key = _rings[ring].PadlockId;
            if (key == 0) return false;
            foreach (var r in _rings) if (r.KeyId == key && r.Removed) return false;
            return true;
        }

        bool LocallyRotatable(int ring)
        {
            var r = _rings[ring];
            if (r.Removed || r.Frozen || IsPadlocked(ring)) return false;
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

        public ModelSnapshot Capture()
        {
            var s = new ModelSnapshot
            {
                Gaps = new float[_rings.Count],
                LinkActive = new bool[_links.Count],
                Removed = new bool[_rings.Count],
                Frozen = new bool[_rings.Count], InGap = new bool[_links.Count],
                Passes = new int[_links.Count], ReleasedAt = new int[_links.Count], Moves = Moves,
            };
            for (int i = 0; i < _rings.Count; i++)
            {
                s.Gaps[i] = _rings[i].GapAngle;
                s.Removed[i] = _rings[i].Removed; s.Frozen[i] = _rings[i].Frozen;
            }
            for (int i = 0; i < _links.Count; i++)
            { var l = _links[i]; s.LinkActive[i] = l.Active; s.InGap[i] = l.InGap;
                s.Passes[i] = l.Passes; s.ReleasedAt[i] = l.ReleasedAt; }
            return s;
        }

        public void Restore(ModelSnapshot s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            if (s.Gaps == null || s.Gaps.Length != _rings.Count ||
                s.Removed == null || s.Removed.Length != _rings.Count ||
                s.LinkActive == null || s.LinkActive.Length != _links.Count ||
                s.Frozen == null || s.Frozen.Length != _rings.Count ||
                s.InGap == null || s.InGap.Length != _links.Count ||
                s.Passes == null || s.Passes.Length != _links.Count ||
                s.ReleasedAt == null || s.ReleasedAt.Length != _links.Count)
                throw new ArgumentException("Snapshot dimensions do not match this puzzle.", nameof(s));

            for (int i = 0; i < _rings.Count; i++)
            {
                _rings[i].GapAngle = s.Gaps[i];
                _rings[i].Removed = s.Removed[i];
                _rings[i].Frozen = s.Frozen[i];
            }
            Moves = s.Moves;
            for (int i = 0; i < _links.Count; i++)
            { var l = _links[i]; l.Active = s.LinkActive[i]; l.InGap = s.InGap[i];
                l.Passes = s.Passes[i]; l.ReleasedAt = s.ReleasedAt[i]; }
        }

        public bool ReleaseLink(int linkId)
        {
            var link = _links[linkId];
            if (!link.Active) return false;
            link.Active = false;
            link.ReleasedAt = Moves + 1;
            LinkReleased?.Invoke(link);
            return true;
        }

        public RotateOutcome Rotate(int ring, float deltaDegrees, List<Link> released = null)
        {
            var r = _rings[ring];
            if (r.Removed) return RotateOutcome.Removed;
            if (!CanRotate(ring)) return RotateOutcome.Locked;

            int mate = LinkedPartner(ring);
            if (!DirectionAllowed(ring, deltaDegrees) || (mate >= 0 && !DirectionAllowed(mate, -deltaDegrees)))
                return RotateOutcome.WrongDirection;
            if (Mathf.Abs(deltaDegrees) < .0001f) return RotateOutcome.Rotated;
            int steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(deltaDegrees) / MaxStep));
            float step = deltaDegrees / steps;
            for (int s = 0; s < steps; s++)
            {
                AdvanceRing(ring, step, released);
                if (mate >= 0) AdvanceRing(mate, -step, released);
            }
            return RotateOutcome.Rotated;
        }

        public bool DirectionAllowed(int ring, float delta) =>
            _rings[ring].Ratchet == 0 || delta == 0 || Mathf.Sign(delta) == _rings[ring].Ratchet;

        void AdvanceRing(int ring, float delta, List<Link> released)
        {
            var r = _rings[ring];
            r.GapAngle = Normalize(r.GapAngle + delta);
            foreach (int id in r.Incoming)
            {
                var l = _links[id];
                bool inside = ClampInGap(l);
                bool entered = inside && !l.InGap;
                l.InGap = inside;
                if (!l.Active || !entered || (l.Tinted && l.Color != r.Color)) continue;
                l.Passes++;
                if (l.Passes < (l.Clay ? 2 : 1)) continue;
                ReleaseLink(id); released?.Add(l);
            }
        }

        /// <summary>Call once after each counted gesture, including failed gestures; never per drag sample.
        /// Pulls are free. Release gesture is move zero of a spring's K further moves.</summary>
        public void CompleteMove()
        {
            Moves++;
            foreach (var l in _links)
                if (!l.Active && l.SpringMoves > 0 && Moves - l.ReleasedAt >= l.SpringMoves &&
                    !_rings[l.Target].Removed && !_rings[l.Owner].Removed)
                {
                    l.Active = true; l.Passes = 0; l.InGap = ClampInGap(l);
                    ClampReclosed?.Invoke(l);
                }
            foreach (var r in _rings)
            {
                if (r.Removed || r.Frozen || r.RustAfter <= 0 || Moves < r.RustAfter) continue;
                bool oiled = false;
                foreach (var oil in _rings)
                    if (r.RustOilId > 0 && oil.OilId == r.RustOilId && oil.Removed) oiled = true;
                if (!oiled) { r.Frozen = true; RingFrozen?.Invoke(r); }
            }
        }

        public bool TryRemove(int ring)
        {
            if (!IsFree(ring)) return false;
            var r = _rings[ring];
            r.Removed = true;
            if (r.KeyId > 0) KeyCollected?.Invoke(r);
            if (r.OilId > 0) OilCollected?.Invoke(r);
            RingRemoved?.Invoke(r);
            if (IsSolved) Solved?.Invoke();
            return true;
        }

        /// <summary>
        /// Checks a fresh copy. Classic boards use monotone simulation; mechanic boards search
        /// release orders with counted moves, key/oil collection and timed freezes.
        /// </summary>
        public static bool IsSolvable(LevelDef def, out List<int> removalOrder)
        {
            if (MechanicSolver.HasMechanics(def)) return MechanicSolver.Solve(def, out _, out removalOrder);
            var model = new PuzzleModel(def);
            removalOrder = new List<int>();
            for (int round = 0; round <= def.Rings.Count; round++)
            {
                bool progress = false;
                foreach (var ring in model.Rings)
                {
                    if (model.IsFree(ring.Id)) { model.TryRemove(ring.Id); removalOrder.Add(ring.Id); progress = true; }
                    else if (model.CanRotate(ring.Id))
                    {
                        int before = 0; foreach (var l in model.Links) if (l.Active) before++;
                        model.Rotate(ring.Id, 360);
                        int after = 0; foreach (var l in model.Links) if (l.Active) after++;
                        progress |= after < before;
                    }
                }
                if (model.IsSolved || !progress) break;
            }
            return model.IsSolved;
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
        /// Explores going one way, the other way, or out-and-back on classic rings.
        /// For coating this is an angular workload estimate including another revolution,
        /// rather than a proof of minimum angle. Used for par / difficulty and telemetry.
        /// </summary>
        public float MinRotationToClear(int ringId)
        {
            var r = _rings[ringId];
            var offsets = new List<float>();
            foreach (var id in r.Incoming)
            {
                var l = _links[id];
                if (!l.Active) continue;
                if (l.Tinted && l.Color != r.Color) return float.PositiveInfinity;
                float reach = GapHalfAngle - l.ClampHalfAngle;           // gap centre must come this close
                float d = Normalize(l.ClampAngle - r.GapAngle);          // counter-clockwise distance
                float ccw = Mathf.Max(0f, d - reach);
                float cw = Mathf.Max(0f, 360f - d - reach);
                if (l.Clay && (l.Passes == 0 || l.InGap)) { ccw += 360; cw += 360; }
                if (r.Ratchet > 0) cw = float.PositiveInfinity;
                if (r.Ratchet < 0) ccw = float.PositiveInfinity;
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
