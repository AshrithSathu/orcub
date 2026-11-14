using System.Collections.Generic;
using Orcub.Core;
using UnityEngine;

namespace Orcub.Game
{
    /// <summary>
    /// Gear-mechanic pieces drawn on one ring (approved features_v1 kit), rebuilt from model queries so Undo
    /// (which rebuilds every view) always shows the restored state:
    ///   ratchet   three arrow inlays along the arc, enlarged so they read on a phone; they flash on a refused turn
    ///   linked    chain-link mark + two meshing pinions in the ring's centre (the pinions counter-rotate with the
    ///             pair), and opposed turn arrows in both rings while the pair is moving
    ///   colour    shape stamp on the ring matching the stamp on its colour clamp (never colour alone)
    ///   key       key on the carrier's tube; padlock on the locked ring that pops open when the key leaves
    ///   rust/oil  rust patches that spread as the deadline nears, an upright moves-left badge, seized when frozen;
    ///             the oil can on its carrier; patches wash off once the oil is collected
    /// Board pieces use the lit, normal-mapped sprite material like rings and holders.
    /// </summary>
    public sealed class RingMechanics : MonoBehaviour
    {
        const float PadlockScale = 1.7f;
        const int DecalOrder = 3, PieceOrder = 6, HubOrder = 7, BadgeOrder = 20;
        // Owner correction: the kit's stamps and arrows read too small on a phone.
        const float ArrowScale = 1.6f, StampScale = 2.5f, RustScale = 2.1f, BadgeScale = 1.7f;
        // Owner correction (rust): must read on ANY ring colour. Patches are inked dark and ringed with a pale
        // halo (contrast on dark AND light clay), and the clay itself dulls toward a desaturated rust brown.
        const int RustPatches = 6;
        static readonly Color RustInk = new Color(0.58f, 0.36f, 0.27f);       // multiplies the kit's brown patch: dark rust
        static readonly Color RustHalo = new Color(1f, 0.93f, 0.82f, 0.75f);  // pale rim around each patch
        static readonly Color RustBrown = new Color(0.56f, 0.24f, 0.12f);
        // Owner correction (linked): the mark and pinions read ~1.6x larger, tinted per pair.
        const float LinkedScale = 1.6f;
        /// <summary>Per-pair tint for the chain mark (pair 1, 2, ...): pastels that read on plum felt and differ from each other.</summary>
        public static readonly Color[] PairTints =
        {
            new Color(1f, 0.82f, 0.36f), new Color(0.55f, 0.92f, 0.98f), new Color(1f, 0.6f, 0.78f),
            new Color(0.66f, 0.95f, 0.6f), new Color(0.8f, 0.7f, 1f),
        };
        public static Color PairTint(int gearGroup) => PairTints[Mathf.Abs(gearGroup - 1) % PairTints.Length];

        PuzzleModel _model;
        Ring _ring;
        RingView _view;
        OrcubArt _art;

        readonly List<SpriteRenderer> _arrows = new List<SpriteRenderer>();
        readonly List<SpriteRenderer> _rust = new List<SpriteRenderer>();
        readonly List<SpriteRenderer> _rustHalo = new List<SpriteRenderer>();
        SpriteRenderer _padlock, _key, _oil, _stamp;
        Transform _hub, _pinionA, _pinionB;
        SpriteRenderer _pinionBRenderer, _mark;
        TextMesh _pairText; MeshRenderer _pairTextRenderer;   // L6: hidden / faded / sorted with the ring
        SpriteRenderer _turnArrow, _turnShadow;
        Transform _badge; TextMesh _badgeText; SpriteRenderer _badgeSprite; MeshRenderer _badgeTextRenderer;
        Vector2 _badgeOffset;
        readonly List<(Transform t, TextMesh text, SpriteRenderer sr, Vector2 local, MeshRenderer mr)> _ids = new List<(Transform, TextMesh, SpriteRenderer, Vector2, MeshRenderer)>();

        bool _padlockShownLocked;
        float _padlockPop = -1f;
        int _rustStage = -1;
        bool _oiled, _frozen;
        float _rustFade = 1f, _refuseFlash, _turnShow, _turnSign = 1f, _lastAngle;
        float _badgeBump;
        int _lastRemaining = -1;

        readonly List<(float angle, float half)> _taken = new List<(float, float)>();

        public static RingMechanics Attach(RingView view, Ring ring, PuzzleModel model, OrcubArt art)
        {
            if (!Has(ring, model)) return null;
            var m = view.gameObject.AddComponent<RingMechanics>();
            m.Init(view, ring, model, art);
            return m;
        }

        static bool Has(Ring r, PuzzleModel m)
        {
            if (r.Ratchet != 0 || r.GearGroup != 0 || r.KeyId != 0 || r.PadlockId != 0 || r.OilId != 0 || r.RustAfter > 0) return true;
            foreach (int id in r.Incoming) if (m.Links[id].Tinted) return true;
            return false;
        }

        void Init(RingView view, Ring ring, PuzzleModel model, OrcubArt art)
        {
            _view = view; _ring = ring; _model = model; _art = art;
            _lastAngle = ring.GapAngle;
            var parent = view.HolderParent;
            var mat = art.spriteMaterial;

            // Reserve the gap and every weld so pieces never sit on them.
            _taken.Add((0f, 44f));
            foreach (int id in ring.Owned) _taken.Add((model.Links[id].WeldOffset, Deg(0.32f)));
            // and where the clamps on this ring sit at the start, so pieces aren't hidden under them
            foreach (int id in ring.Incoming) _taken.Add((Mathf.DeltaAngle(ring.GapAngle, model.Links[id].ClampAngle), Deg(0.26f)));

            bool multiKeys = Count(r => r.KeyId > 0) > 1, multiOil = Count(r => r.OilId > 0) > 1;

            if (ring.PadlockId > 0 && art.padlockLocked != null)
            {
                float a = Take(Deg(0.34f), 180f);
                _padlock = OnTube("Padlock", art.padlockLocked, a, PadlockScale, new Color(0.96f, 0.72f, 0.36f), PieceOrder);
                _padlockShownLocked = true;
                if (multiKeys) IdBadge(ring.PadlockId, a);
            }
            if (ring.KeyId > 0 && art.key != null)
            {
                float a = Take(Deg(0.34f), 135f);
                _key = OnTube("Key", art.key, a, 1.8f, new Color(1f, 0.94f, 0.82f), PieceOrder);
                _key.transform.localRotation = Quaternion.Euler(0f, 0f, a + 90f);   // points out from the rim, as approved
                if (multiKeys) IdBadge(ring.KeyId, a);
            }
            if (ring.OilId > 0 && art.oilCan != null)
            {
                float a = Take(Deg(0.48f), 150f);
                _oil = OnTube("OilCan", art.oilCan, a, 1.5f, Color.white, PieceOrder);
                _oil.transform.localPosition = PuzzleModel.Dir(a) * (_ring.Radius + 0.22f);   // sits on the rim, readable
                if (multiOil) IdBadge(ring.OilId, a);
            }
            bool stamped = false;
            foreach (int id in ring.Incoming) stamped |= model.Links[id].Tinted;
            if (stamped)
            {
                int shape = OrcubArt.ShapeFor(ring.Color, model);   // L3: unique per colour on this board
                var sprite = art.RingStamp(shape, ring.Radius);
                if (sprite != null)
                {
                    float a = Take(Deg(0.24f), 180f);
                    _stamp = OnTube("Stamp", sprite, a, StampScale, StampInk(art.RingColor(ring.Color)), DecalOrder + 1);
                }
            }
            if (ring.Ratchet != 0)
            {
                var set = ring.Ratchet > 0 ? art.ratchetCcw : art.ratchetCw;
                var sprite = art.PerRadius(set, ring.Radius);
                if (sprite != null)
                    for (int i = 0; i < 3; i++)
                        _arrows.Add(OnTube("Ratchet", sprite, Take(Deg(0.34f), 120f + i * 70f), ArrowScale,
                            new Color(1f, 0.96f, 0.88f), DecalOrder + 1));
            }
            if (ring.RustAfter > 0)
            {
                for (int i = 0; i < RustPatches; i++)
                {
                    float a = Take(Deg(0.3f), 200f, allowOverlap: true);
                    var sp = art.PerRadius(i % 2 == 0 ? art.rustPatchA : art.rustPatchB, ring.Radius);
                    if (sp == null) continue;
                    var halo = OnTube("RustHalo", sp, a, RustScale * 1.16f, RustHalo, DecalOrder - 1);
                    _rustHalo.Add(halo);
                    _rust.Add(OnTube("Rust", sp, a, RustScale, RustInk, DecalOrder));
                }
                if (art.movesBadge != null) BuildRustBadge();
                // L2: with several oil cans, the rusting ring shows which can saves it
                if (multiOil && ring.RustOilId > 0) IdBadge(ring.RustOilId, Take(Deg(0.3f), 250f));
            }
            if (ring.GearGroup > 0) BuildHub();
            Refresh(animate: false);
        }

        int Count(System.Predicate<Ring> p)
        {
            int n = 0;
            foreach (var r in _model.Rings) if (p(r)) n++;
            return n;
        }

        float Deg(float worldUnits) => Mathf.Rad2Deg * worldUnits / Mathf.Max(0.5f, _ring.Radius);

        /// <summary>Free spot on the tube (ring-local degrees, gap = 0) farthest from everything already placed.</summary>
        float Take(float half, float prefer, bool allowOverlap = false)
        {
            float best = prefer, bestScore = float.MinValue;
            for (float a = 50f; a <= 310f; a += 5f)
            {
                float clear = float.MaxValue;
                foreach (var (angle, h) in _taken)
                    clear = Mathf.Min(clear, Mathf.Abs(Mathf.DeltaAngle(a, angle)) - h - half);
                float score = clear - Mathf.Abs(Mathf.DeltaAngle(a, prefer)) * 0.02f;
                if (score > bestScore) { bestScore = score; best = a; }
            }
            if (!allowOverlap || bestScore > -half) _taken.Add((best, half));
            return best;
        }

        SpriteRenderer OnTube(string name, Sprite sprite, float angle, float scale, Color tint, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_view.HolderParent, false);
            go.transform.localPosition = PuzzleModel.Dir(angle) * _ring.Radius;
            go.transform.localRotation = Quaternion.Euler(0f, 0f, angle - 90f);
            go.transform.localScale = Vector3.one * scale;
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.color = tint;
            sr.sortingOrder = order;
            if (_art.spriteMaterial != null) sr.sharedMaterial = _art.spriteMaterial;
            return sr;
        }

        /// <summary>Cream stamps on darker clay, plum stamps on the light pastels (yellows), for contrast.</summary>
        public static Color StampInk(Color clay)
        {
            float lum = 0.2126f * clay.r + 0.7152f * clay.g + 0.0722f * clay.b;
            return lum > 0.72f ? new Color(0.36f, 0.14f, 0.24f) : new Color(1f, 0.96f, 0.88f);
        }

        void IdBadge(int id, float angle)
        {
            if (_art.idBadge == null) return;
            var t = new GameObject("Id").transform;
            t.SetParent(transform, false);
            var sr = t.gameObject.AddComponent<SpriteRenderer>();
            sr.sprite = _art.idBadge;
            sr.color = new Color(1f, 0.95f, 0.86f);
            sr.sortingOrder = BadgeOrder;
            if (_art.spriteMaterial != null) sr.sharedMaterial = _art.spriteMaterial;
            t.localScale = Vector3.one * 1.1f;
            var text = MechText.Create(t, id.ToString(), _art, BadgeOrder + 1, 1f);
            _ids.Add((t, text, sr, PuzzleModel.Dir(angle) * (_ring.Radius + 0.62f), text.GetComponent<MeshRenderer>()));
        }

        void BuildRustBadge()
        {
            _badge = new GameObject("RustBadge").transform;
            _badge.SetParent(transform, false);
            _badgeSprite = _badge.gameObject.AddComponent<SpriteRenderer>();
            _badgeSprite.sprite = _art.movesBadge;
            _badgeSprite.color = new Color(1f, 0.95f, 0.86f);
            _badgeSprite.sortingOrder = BadgeOrder;
            if (_art.spriteMaterial != null) _badgeSprite.sharedMaterial = _art.spriteMaterial;
            _badge.localScale = Vector3.one * BadgeScale;
            _badgeText = MechText.Create(_badge, "", _art, BadgeOrder + 1, 1f);
            _badgeTextRenderer = _badgeText.GetComponent<MeshRenderer>();
            _badgeOffset = BadgeSpot();
        }

        /// <summary>World-fixed badge spot beside the ring, picked where it overlaps the fewest neighbours.</summary>
        Vector2 BadgeSpot()
        {
            float[] prefs = { 45f, 135f, 0f, 180f, 90f, -45f, -135f, -90f };
            Vector2 best = PuzzleModel.Dir(45f) * (_ring.Radius + 0.5f);
            float bestScore = float.MinValue;
            for (int i = 0; i < prefs.Length; i++)
            {
                Vector2 off = PuzzleModel.Dir(prefs[i]) * (_ring.Radius + 0.62f);
                Vector2 p = _ring.Center + off;
                float clear = 3f;
                foreach (var r in _model.Rings)
                {
                    if (r.Id == _ring.Id) continue;
                    float d = (p - r.Center).magnitude;
                    clear = Mathf.Min(clear, Mathf.Abs(d - r.Radius) - 0.25f, d < r.Radius ? 0.2f : 3f);
                }
                foreach (var l in _model.Links)
                    clear = Mathf.Min(clear, (p - _model.ClampPoint(l)).magnitude - 0.35f);
                float score = Mathf.Min(clear, 0.6f) - i * 0.01f;
                if (score > bestScore) { bestScore = score; best = off; }
            }
            return best;
        }

        void BuildHub()
        {
            int mate = -1;
            foreach (var r in _model.Rings) if (r.Id != _ring.Id && r.GearGroup == _ring.GearGroup) mate = r.Id;
            _hub = new GameObject("LinkedHub").transform;
            _hub.SetParent(_view.HolderParent, false);
            // 1.6x the kit size, capped so the hub stays inside the tube on small rings
            float s = Mathf.Clamp(_ring.Radius, 1f, 1.4f) * Mathf.Min(LinkedScale, Mathf.Max(1f, (_ring.Radius - 0.18f) / 0.62f));
            int groups = 0;
            var seen = new HashSet<int>();
            foreach (var r in _model.Rings) if (r.GearGroup > 0 && seen.Add(r.GearGroup)) groups++;
            if (_art.linkedMark != null)
            {
                // tinted per pair so the two partners read as a pair at a glance (both rings share the tint)
                _mark = MakeChild(_hub, "ChainMark", _art.linkedMark, PairTint(_ring.GearGroup), HubOrder + 1, _art.spriteMaterial);
                _mark.transform.localPosition = new Vector3(0f, 0.2f * s, 0f);
                _mark.transform.localScale = Vector3.one * 1.45f * s;
                if (groups > 1)   // several pairs on one board: the pair's number too, never colour alone
                {
                    _pairText = MechText.Create(_mark.transform, _ring.GearGroup.ToString(), _art, HubOrder + 2, 0.55f);
                    _pairText.color = PairTint(_ring.GearGroup);   // reads on the felt beside the mark, matches the pair
                    _pairText.transform.localPosition = new Vector3(0.27f, 0.12f, 0f);
                    _pairTextRenderer = _pairText.GetComponent<MeshRenderer>();
                }
            }
            // Two meshing pinions: this ring's colour and its partner's, turning against each other.
            var cog = _art.homeGearRing;
            if (cog != null)
            {
                float d = 0.5f * s;    // pinion diameter in world units: big enough to read as meshing teeth
                float scale = d / Mathf.Max(0.01f, cog.bounds.size.x);
                var mateColor = mate >= 0 ? _art.RingColor(_model.Rings[mate].Color) : Color.white;
                _pinionA = MakeChild(_hub, "PinionA", cog, Shade(_art.RingColor(_ring.Color)), HubOrder, _art.unlitMaterial).transform;
                _pinionB = MakeChild(_hub, "PinionB", cog, Shade(mateColor), HubOrder, _art.unlitMaterial).transform;
                _pinionA.localScale = _pinionB.localScale = Vector3.one * scale;
                _pinionA.localPosition = new Vector3(-d * 0.45f, -0.27f * s, 0f);
                _pinionB.localPosition = new Vector3(d * 0.45f, -0.27f * s, 0f);
                _pinionB.localRotation = Quaternion.Euler(0f, 0f, 18f);   // offset half a tooth so the teeth interleave
                _pinionBRenderer = _pinionB.GetComponent<SpriteRenderer>();
            }
            var arc = MechSprites.ArcArrow;
            _turnShadow = MakeChild(_hub, "TurnShadow", arc, new Color(0.12f, 0.03f, 0.08f, 0f), HubOrder + 2, _art.unlitMaterial);
            _turnArrow = MakeChild(_hub, "TurnArrow", arc, new Color(1f, 0.95f, 0.86f, 0f), HubOrder + 3, _art.unlitMaterial);
            // arc radius 0.6 world units at kit scale: clears the chain mark and stays inside the tube
            float arcScale = Mathf.Clamp(_ring.Radius, 1f, 1.4f);
            _turnArrow.transform.localScale = _turnShadow.transform.localScale = Vector3.one * arcScale;
            _turnShadow.transform.localPosition = new Vector3(0.03f, -0.05f, 0f);
        }

        static Color Shade(Color c) => new Color(c.r * 0.92f, c.g * 0.92f, c.b * 0.92f, 1f);

        static SpriteRenderer MakeChild(Transform parent, string name, Sprite sprite, Color color, int order, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.color = color;
            sr.sortingOrder = order;
            if (mat != null) sr.sharedMaterial = mat;
            return sr;
        }

        // ------------------------------------------------------------------ feedback hooks

        /// <summary>Wrong-direction turn on a ratchet: the arrows flash coral and bump.</summary>
        public void Refuse() => _refuseFlash = 0.5f;

        /// <summary>Touch on a padlocked / frozen ring: the lock or the rust gives a nudge.</summary>
        public void Nudge()
        {
            _refuseFlash = 0.5f;
            _badgeBump = 1f;
        }

        public bool HasRatchet => _arrows.Count > 0;

        // ------------------------------------------------------------------ live state

        void LateUpdate()
        {
            if (_model == null || _view == null) return;
            Refresh(animate: true);
        }

        void Refresh(bool animate)
        {
            float dt = animate ? Time.deltaTime : 0f;
            bool hidden = _view.IsIntroPending || _view.IsFlying || _ring.Removed;

            // ratchet arrows: coral flash + bump when a turn is refused
            if (_refuseFlash > 0f) _refuseFlash -= dt;
            float flash = Mathf.Clamp01(_refuseFlash / 0.5f);
            float bump = Mathf.Sin(flash * Mathf.PI);
            foreach (var a in _arrows)
            {
                if (_view.IsIntroPending) break;
                a.color = Color.Lerp(new Color(1f, 0.96f, 0.88f, a.color.a), new Color(0.95f, 0.42f, 0.36f, a.color.a), bump);
                a.transform.localScale = Vector3.one * ArrowScale * (1f + 0.2f * bump);
            }

            UpdatePadlock(dt, hidden);
            UpdateRust(dt, hidden, animate);
            UpdateHub(dt, hidden);

            for (int i = 0; i < _ids.Count; i++)
            {
                var (t, text, sr, local, mr) = _ids[i];
                t.position = transform.TransformPoint(local);
                t.rotation = Quaternion.identity;
                if (text.gameObject.activeSelf == hidden) text.gameObject.SetActive(!hidden);
                MechText.Sort(mr, sr.sortingOrder + 1);
            }
            if (_pairText != null)
            {   // L6: the pair number hides with the intro / fly-away and follows the mark's (lifted) order
                bool show = !hidden && _mark != null && _mark.enabled;
                if (_pairText.gameObject.activeSelf != show) _pairText.gameObject.SetActive(show);
                if (show) MechText.Sort(_pairTextRenderer, _mark.sortingOrder + 1);
            }
        }

        void UpdatePadlock(float dt, bool hidden)
        {
            if (_padlock == null) return;
            bool locked = _model.IsPadlocked(_ring.Id);
            if (locked && !_padlockShownLocked)
            {   // restored by Undo: snap back, no animation
                _padlock.sprite = _art.padlockLocked;
                _padlock.enabled = true;
                _padlock.transform.localScale = Vector3.one * PadlockScale;
                _padlockPop = -1f;
            }
            else if (!locked && _padlockShownLocked)
            {
                _padlock.sprite = _art.padlockUnlocked != null ? _art.padlockUnlocked : _padlock.sprite;
                _padlockPop = dt > 0f ? 0f : 2f;
            }
            _padlockShownLocked = locked;
            if (_padlockPop >= 0f && !hidden)
            {
                // a small pop: swell, hold open, then shrink away
                _padlockPop += dt;
                float t = _padlockPop;
                float s = t < 0.18f ? Mathf.Lerp(1f, 1.4f, t / 0.18f)
                    : t < 0.7f ? Mathf.Lerp(1.4f, 1.15f, (t - 0.18f) / 0.52f)
                    : Mathf.Lerp(1.15f, 0f, (t - 0.7f) / 0.3f);
                _padlock.transform.localScale = Vector3.one * PadlockScale * Mathf.Max(0f, s);
                _padlock.enabled = t < 1f;
            }
            else if (locked && _refuseFlash > 0f)
                _padlock.transform.localRotation = Quaternion.Euler(0f, 0f, _padlockBase + Mathf.Sin(_refuseFlash * 40f) * 12f * (_refuseFlash / 0.5f));
            if (locked && _refuseFlash <= 0f) _padlock.transform.localRotation = Quaternion.Euler(0f, 0f, _padlockBase);
        }

        float _padlockBase => _padlock != null ? PuzzleModel.AngleOf(_padlock.transform.localPosition) - 90f : 0f;

        void UpdateRust(float dt, bool hidden, bool animate)
        {
            if (_rust.Count == 0) return;
            int remaining = Mathf.Max(0, _ring.RustAfter - _model.Moves);
            bool oiled = false;
            if (_ring.RustOilId > 0)
            {
                var rings = _model.Rings;   // L1: indexed, no boxed enumerator per frame
                for (int i = 0; i < rings.Count; i++) if (rings[i].OilId == _ring.RustOilId && rings[i].Removed) oiled = true;
            }
            oiled &= !_ring.Frozen;
            bool frozen = _ring.Frozen;

            // spread: 2 patches with plenty of time, all of them on the last move; seized once frozen
            int stage = frozen ? 99 : oiled ? -2 : remaining >= 4 ? 2 : remaining == 3 ? 3 : remaining == 2 ? 4 : RustPatches;
            if (stage != _rustStage)
            {
                bool grew = animate && stage > _rustStage && _rustStage >= 0;
                _rustStage = stage;
                for (int i = 0; i < _rust.Count; i++)
                {
                    var sr = _rust[i];
                    sr.sprite = frozen ? _art.PerRadius(_art.rustSeized, _ring.Radius) ?? sr.sprite
                        : _art.PerRadius(i % 2 == 0 ? _art.rustPatchA : _art.rustPatchB, _ring.Radius);
                    if (oiled) { if (!animate) sr.enabled = false; }   // oiled: the existing patches wash off (fade)
                    else sr.enabled = frozen || i < stage;
                    if (i < _rustHalo.Count) { _rustHalo[i].sprite = sr.sprite; _rustHalo[i].enabled = sr.enabled; }
                }
                if (grew) _view.Pulse();
            }
            float fadeTarget = oiled ? 0f : 1f;
            _rustFade = animate ? Mathf.MoveTowards(_rustFade, fadeTarget, dt * 1.6f) : fadeTarget;
            if (!_view.IsIntroPending && !_view.IsFlying)
            {
                for (int i = 0; i < _rust.Count; i++) { var c = _rust[i].color; if (c.a != _rustFade) { c.a = _rustFade; _rust[i].color = c; } }
                for (int i = 0; i < _rustHalo.Count; i++) { var c = _rustHalo[i].color; float a = RustHalo.a * _rustFade; if (c.a != a) { c.a = a; _rustHalo[i].color = c; } }
            }
            // the clay itself dulls to rust brown as the deadline nears: obvious on orange / yellow / red too
            float amount = frozen ? 0.75f : oiled ? 0f : Mathf.Lerp(0.25f, 0.55f, Mathf.InverseLerp(4f, 1f, remaining));
            _view.SetBodyBlend(RustedClay(_view.Color), amount * _rustFade);
            _oiled = oiled; _frozen = frozen;

            if (_badge == null) return;
            bool showBadge = !hidden && !oiled;
            _badge.gameObject.SetActive(showBadge);
            if (!showBadge) return;
            if (remaining != _lastRemaining) { if (_lastRemaining >= 0 && animate) _badgeBump = 1f; _lastRemaining = remaining; }
            _badgeBump = Mathf.MoveTowards(_badgeBump, 0f, dt * 3f);
            _badge.position = transform.position + (Vector3)_badgeOffset;
            _badge.rotation = Quaternion.identity;
            _badge.localScale = Vector3.one * BadgeScale * (1f + 0.25f * Mathf.Sin(_badgeBump * Mathf.PI));
            if (remaining != _shownRemaining || frozen != _shownFrozen)
            {   // L1: re-mesh the number only when it changes (frozen shows a coral 0 on a rusty badge)
                _shownRemaining = remaining; _shownFrozen = frozen;
                _badgeText.text = remaining.ToString();
                _badgeText.color = frozen ? new Color(0.62f, 0.16f, 0.12f) : remaining <= 1 ? new Color(0.86f, 0.32f, 0.27f) : new Color(0.25f, 0.08f, 0.16f);
                _badgeSprite.color = frozen ? new Color(0.95f, 0.82f, 0.72f) : new Color(1f, 0.95f, 0.86f);
            }
            MechText.Sort(_badgeTextRenderer, _badgeSprite.sortingOrder + 1);
        }

        int _shownRemaining = int.MinValue;
        bool _shownFrozen;

        /// <summary>Rusted clay for a palette colour: desaturated, darkened and pulled toward rust brown.</summary>
        public static Color RustedClay(Color clay)
        {
            float lum = 0.2126f * clay.r + 0.7152f * clay.g + 0.0722f * clay.b;
            var grey = new Color(lum * 0.62f, lum * 0.58f, lum * 0.55f, 1f);
            return Color.Lerp(grey, RustBrown, 0.68f);
        }

        void UpdateHub(float dt, bool hidden)
        {
            if (_hub == null) return;
            _hub.rotation = Quaternion.identity;   // the label and pinions stay upright while the ring turns
            float delta = Mathf.DeltaAngle(_lastAngle, _ring.GapAngle);
            _lastAngle = _ring.GapAngle;
            int mate = _model.LinkedPartner(_ring.Id);
            if (Mathf.Abs(delta) > 0.01f && mate >= 0)
            {
                _turnShow = 0.7f;
                _turnSign = Mathf.Sign(delta);
            }
            else _turnShow = Mathf.Max(0f, _turnShow - dt);
            if (_pinionA != null)
            {
                // ~3.5x gearing so a small turn of the ring visibly spins the pinions
                _pinionA.Rotate(0f, 0f, delta * 3.5f);
                _pinionB.Rotate(0f, 0f, -delta * 3.5f);
                bool coupled = mate >= 0;
                if (!_view.IsIntroPending && _pinionBRenderer.enabled != coupled) _pinionBRenderer.enabled = coupled;   // partner pulled: disengaged
            }
            if (_view.IsIntroPending) return;
            float a = Mathf.Clamp01(_turnShow / 0.25f) * (hidden ? 0f : 1f);
            var ca = _turnArrow.color; ca.a = a; _turnArrow.color = ca;
            var cs = _turnShadow.color; cs.a = a * 0.3f; _turnShadow.color = cs;
            float flip = _turnSign >= 0f ? 1f : -1f;   // sprite is drawn counter-clockwise
            float k = _turnArrow.transform.localScale.y;
            _turnArrow.transform.localScale = new Vector3(k * flip, k, 1f);
            _turnShadow.transform.localScale = new Vector3(k * flip, k, 1f);
        }
    }

    /// <summary>Upright numbers on board badges (Fredoka TextMesh, crisp at any zoom).</summary>
    public static class MechText
    {
        public static TextMesh Create(Transform parent, string text, OrcubArt art, int order, float size)
        {
            var go = new GameObject("Text");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0.012f, 0f);
            var tm = go.AddComponent<TextMesh>();
            tm.font = art.uiFont;
            tm.fontSize = 96;
            tm.characterSize = 0.05f * size;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = new Color(0.25f, 0.08f, 0.16f);
            tm.text = text;
            var mr = go.GetComponent<MeshRenderer>();
            if (art.uiFont != null) mr.sharedMaterial = art.uiFont.material;
            mr.sortingOrder = order;
            return tm;
        }

        public static void Sort(TextMesh t, int order) => Sort(t.GetComponent<MeshRenderer>(), order);

        /// <summary>L1: callers cache the renderer; no GetComponent per frame.</summary>
        public static void Sort(MeshRenderer mr, int order)
        {
            if (mr != null && mr.sortingOrder != order) mr.sortingOrder = order;
        }
    }

    /// <summary>Procedural sprites that have no kit counterpart (a clean cream curved arrow).</summary>
    public static class MechSprites
    {
        static Sprite _arc;

        /// <summary>Builds the arrow at boot (behind the splash) instead of on the first linked board / intro card.</summary>
        public static void Warm() { _ = ArcArrow; }

        /// <summary>
        /// Counter-clockwise arc arrow over the top of a circle (from 30° to 150°, head at 150°), white so it can be
        /// tinted; 256 px at 160 PPU, so at scale 1 the arc radius is 0.6 world units. Mirror X for clockwise.
        /// Review e, L9: one analytic anti-aliased sample per pixel (signed distance), not 3×3 supersampling.
        /// </summary>
        public static Sprite ArcArrow
        {
            get
            {
                if (_arc != null) return _arc;
                const int n = 256;
                const float cx = 128f, cy = 100f, r = 96f, half = 13f, a0 = 32f, a1 = 138f;
                var px = new Color32[n * n];
                float h = a1 * Mathf.Deg2Rad;
                var end = new Vector2(cx + Mathf.Cos(h) * r, cy + Mathf.Sin(h) * r);
                var tan = new Vector2(-Mathf.Sin(h), Mathf.Cos(h));
                var rad = new Vector2(Mathf.Cos(h), Mathf.Sin(h));
                Vector2 tip = end + tan * 40f, b1 = end + rad * 30f - tan * 2f, b2 = end - rad * 30f - tan * 2f;
                var start = new Vector2(cx + Mathf.Cos(a0 * Mathf.Deg2Rad) * r, cy + Mathf.Sin(a0 * Mathf.Deg2Rad) * r);
                var centre = new Vector2(cx, cy);
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        var p = new Vector2(x + 0.5f, y + 0.5f);
                        var d = p - centre;
                        float ang = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
                        float dist = (p - start).magnitude - half;   // round tail cap
                        if (ang >= a0 && ang <= a1) dist = Mathf.Min(dist, Mathf.Abs(d.magnitude - r) - half);
                        dist = Mathf.Min(dist, TriDistance(p, tip, b1, b2));
                        byte a = (byte)(255f * Mathf.Clamp01(0.5f - dist));
                        px[y * n + x] = new Color32(255, 255, 255, a);
                    }
                var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "ArcArrow" };
                tex.SetPixels32(px);
                tex.Apply();
                _arc = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, cy / n), 160f);
                _arc.name = "ArcArrow";
                return _arc;
            }
        }

        /// <summary>Signed distance to a triangle (negative inside).</summary>
        static float TriDistance(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float dist = Mathf.Min(Segment(p, a, b), Mathf.Min(Segment(p, b, c), Segment(p, c, a)));
            return InTri(p, a, b, c) ? -dist : dist;
        }

        static float Segment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-6f, ab.sqrMagnitude));
            return (p - (a + ab * t)).magnitude;
        }

        static bool InTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross(p, a, b), d2 = Cross(p, b, c), d3 = Cross(p, c, a);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        static float Cross(Vector2 p, Vector2 a, Vector2 b) => (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
    }
}
