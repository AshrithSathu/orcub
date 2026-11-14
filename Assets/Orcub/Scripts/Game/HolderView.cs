using Orcub.Core;
using UnityEngine;

namespace Orcub.Game
{
    /// <summary>
    /// A holder: mount welded on the owner's tube, a stem, and a clamp gripping the target's tube.
    /// Lives in the owner's visual space so it moves with the owner. On release it retracts into the mount.
    /// Mechanic variants (approved features_v1):
    ///   colour clamp  clamp tinted with its colour + a large shape stamp (matches the stamp on its ring)
    ///   spring        coiled clamp; on release the jaws spring open and wait with a moves-left badge, then
    ///                 snap shut again (unless either ring was pulled out first)
    ///   clay coat     clay shell over the clamp: intact, cracked after the first pass, gone on release
    /// </summary>
    public sealed class HolderView : MonoBehaviour
    {
        public const int StemOrder = 10;
        public const int MountOrder = 11;
        public const int ClampOrder = 12;
        const float ReleaseDuration = 0.32f;
        const float NubLength = 0.16f;
        const float BadgeScale = 1.6f;          // countdown badges must read at board zoom
        const float StampScale = 2.2f;          // owner correction: the kit's stamp is too small on a phone
        static readonly Vector2 StampAt = new Vector2(0f, -0.325f);   // the stamp's spot on the clamp base

        public int LinkId { get; private set; }

        SpriteRenderer _mount, _stem, _clamp, _clampAccent, _stamp, _coat;
        Vector2 _weld, _clampPos;
        float _stemWidth;
        bool _released;
        float _releaseT;
        float _flash;
        Color _tint, _clampTint;

        Link _link;
        PuzzleModel _model;
        OrcubArt _art;
        bool _spring, _springOpen;
        float _snap, _refuse, _crack;
        int _shownPasses;
        Transform _badge;
        TextMesh _badgeText;
        MeshRenderer _badgeTextRenderer;
        SpriteRenderer _badgeSprite;
        int _lastCount = -1;
        float _badgeBump;

        public void Init(Link link, PuzzleModel model, RingView owner, OrcubArt art)
        {
            LinkId = link.Id;
            _link = link;
            _model = model;
            _art = art;
            name = $"Holder_{link.Owner}->{link.Target}";
            transform.SetParent(owner.HolderParent, false);
            Measure();
            _stemWidth = art.stemWidth;

            // Holders are their own clay "hardware" colour; the dome on the clamp carries the owner's colour
            // so you can read who holds whom.
            _tint = art.hardwareTint;
            _tint.a = 1f;
            _spring = link.SpringMoves > 0 && art.springClosed != null;
            int shape = OrcubArt.ShapeFor(link.Color, model);   // L3: unique per colour on this board
            bool tinted = link.Tinted && art.clampStamps != null && art.clampStamps.Length > shape;
            _clampTint = tinted ? Color.Lerp(art.RingColor(link.Color), Color.white, 0.12f) : _tint;

            _stem = Create("Stem", art.stem, _tint, StemOrder, art.spriteMaterial);
            _stem.drawMode = SpriteDrawMode.Sliced;
            _mount = Create("Mount", art.mount, _tint, MountOrder, art.spriteMaterial);
            var clampSprite = _spring ? art.springClosed : tinted ? art.clampStamps[shape] : art.clamp;
            _clamp = Create("Clamp", clampSprite, _clampTint, ClampOrder, art.spriteMaterial);
            if (art.clampAccent != null && !tinted)
                _clampAccent = Create("ClampAccent", art.clampAccent, owner.Color, ClampOrder + 1, art.spriteMaterial);
            if (link.Clay && art.clayIntact != null)
            {
                _coat = Create("ClayCoat", Coat(link.Passes > 0), CoatTint, ClampOrder + 2, art.spriteMaterial);
                _shownPasses = link.Passes;
            }
            if (tinted)
            {
                var stamp = art.RingStamp(shape, 1f);
                if (stamp != null)
                    _stamp = Create("Stamp", stamp, RingMechanics.StampInk(art.RingColor(link.Color)), ClampOrder + 3, art.spriteMaterial);
            }
            if (_spring && art.springBadge != null)
            {
                _badge = new GameObject("SpringBadge").transform;
                _badge.SetParent(transform, false);
                _badgeSprite = _badge.gameObject.AddComponent<SpriteRenderer>();
                _badgeSprite.sprite = art.springBadge;
                _badgeSprite.color = new Color(1f, 0.95f, 0.86f);
                _badgeSprite.sortingOrder = 20;
                if (art.spriteMaterial != null) _badgeSprite.sharedMaterial = art.spriteMaterial;
                _badgeText = MechText.Create(_badge, "", art, 21, 1f);
                _badgeTextRenderer = _badgeText.GetComponent<MeshRenderer>();
                _badge.localScale = Vector3.one * BadgeScale;
                _badge.gameObject.SetActive(false);
            }

            Layout(0f);
        }

        /// <summary>
        /// Weld and clamp points in the owner's (rotating) frame. The clamp point comes straight from the model.
        /// Review e, L5 (re-closed spring geometry), decided here: an active holder is always drawn welded on the
        /// owner's rim at the point facing the target ring. Owners can only turn while their spring waits open (an
        /// owner with an active clamp is locked), so for every never-released holder this is exactly the model's
        /// weld point; after a re-close it moves the mount back to the side facing the target instead of drawing the
        /// stem from the turned weld across the owner's own interior. The weld has no rule effect (only the clamp
        /// point on the target matters), so gameplay, the solver and Undo are unchanged.
        /// </summary>
        void Measure()
        {
            var owner = _model.Rings[_link.Owner];
            var target = _model.Rings[_link.Target];
            var inv = Quaternion.Euler(0f, 0f, -owner.GapAngle);
            Vector2 weld = _link.Active
                ? owner.Center + PuzzleModel.Dir(PuzzleModel.AngleOf(target.Center - owner.Center)) * owner.Radius
                : _model.WeldPoint(_link);
            _weld = inv * (weld - owner.Center);
            _clampPos = inv * (_model.ClampPoint(_link) - owner.Center);
        }

        SpriteRenderer Create(string childName, Sprite sprite, Color color, int order, Material material)
        {
            var go = new GameObject(childName);
            go.transform.SetParent(transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.color = color;
            sr.sortingOrder = order;
            if (material != null) sr.sharedMaterial = material;
            return sr;
        }

        /// <summary>Cream clay shell (approved clay_coat_A) over the tan clamp; cracked after the first pass.</summary>
        static readonly Color CoatTint = new Color(1f, 0.94f, 0.84f);

        Sprite Coat(bool cracked) => cracked
            ? (_art.clayCrackedLight != null ? _art.clayCrackedLight : _art.clayCracked)
            : (_art.clayIntactLight != null ? _art.clayIntactLight : _art.clayIntact);

        bool EndpointsPresent => !_model.Rings[_link.Target].Removed && !_model.Rings[_link.Owner].Removed;

        /// <summary>Show the holder already released (board rebuilt after Undo).</summary>
        public void ReleaseInstant()
        {
            if (_spring && EndpointsPresent)
            {
                SetSpringOpen(true);
                Layout(0f);
                return;
            }
            _released = true;
            _releaseT = 1f;
            Layout(1f);
        }

        public void Release()
        {
            if (_spring && EndpointsPresent)
            {   // the jaws spring open and stay by the tube, counting down
                SetSpringOpen(true);
                _snap = 0.3f;
                return;
            }
            if (_released) return;
            _released = true;
            _releaseT = 0f;
        }

        /// <summary>Spring clamp re-closed on the target: re-measure (the owner may have turned) and snap shut.</summary>
        public void Reclose()
        {
            _released = false;
            _releaseT = 0f;
            Measure();
            SetSpringOpen(false);
            if (_coat != null) { _coat.sprite = Coat(false); _coat.enabled = true; _shownPasses = 0; }
            _snap = 0.35f;
            Layout(0f);
        }

        /// <summary>Gentle "no" from a colour clamp the ring's gap passed but cannot open.</summary>
        public void Refuse() => _refuse = 0.45f;

        void SetSpringOpen(bool open)
        {
            _springOpen = open;
            _clamp.sprite = open && _art.springOpen != null ? _art.springOpen : _art.springClosed;
        }

        /// <summary>Brief highlight used when the player touches a locked owner ring.</summary>
        public void Flash() => _flash = 0.4f;

        void Update()
        {
            float dt = Time.deltaTime;
            if (_released && _releaseT < 1f)
            {
                _releaseT = Mathf.Min(1f, _releaseT + dt / ReleaseDuration);
                Layout(_releaseT);
            }

            // a waiting spring whose ring was pulled out has nothing left to grip: retract it
            if (_springOpen && !EndpointsPresent && !_released)
            {
                _springOpen = false;
                _released = true;
                _releaseT = 0f;
            }

            if (_coat != null && _link.Active && _link.Passes != _shownPasses)
            {
                _shownPasses = _link.Passes;
                _coat.sprite = Coat(_shownPasses > 0);
                if (_shownPasses > 0) _crack = 0.3f;
            }
            if (_coat != null && !_link.Active) _coat.enabled = false;   // second pass: the shell falls away

            if (_snap > 0f || _refuse > 0f || _crack > 0f)
            {
                _snap = Mathf.Max(0f, _snap - dt);
                _refuse = Mathf.Max(0f, _refuse - dt);
                _crack = Mathf.Max(0f, _crack - dt);
                if (!_released) Layout(0f);
            }

            if (_flash > 0f)
            {
                _flash -= dt;
                float k = Mathf.Clamp01(_flash / 0.4f);
                var c = Color.Lerp(_tint, Color.white, 0.55f * Mathf.Sin(k * Mathf.PI));
                SetTint(c);
                if (_flash <= 0f) SetTint(_tint);
            }
            UpdateBadge(dt);
        }

        void UpdateBadge(float dt)
        {
            if (_badge == null) return;
            bool show = _springOpen && !_released && !_link.Active && EndpointsPresent;
            if (_badge.gameObject.activeSelf != show) _badge.gameObject.SetActive(show);
            if (!show) { _lastCount = -1; return; }
            // release gesture is move zero; never show more than K while that gesture is still in progress
            int k = _link.SpringMoves;
            int left = Mathf.Clamp(k - Mathf.Max(0, _model.Moves - _link.ReleasedAt), 0, k);
            if (left != _lastCount)
            {   // L1: set (and re-mesh) the number only when it changes
                if (_lastCount >= 0) _badgeBump = 1f;
                _lastCount = left;
                _badgeText.text = left.ToString();
                _badgeText.color = left <= 1 ? new Color(0.86f, 0.32f, 0.27f) : new Color(0.25f, 0.08f, 0.16f);
            }
            _badgeBump = Mathf.MoveTowards(_badgeBump, 0f, dt * 3f);

            // upright, beside the clamp on the side away from the target ring's centre line
            Vector3 clampWorld = _clamp.transform.position;
            Vector2 along = (Vector2)(clampWorld - transform.TransformPoint(_weld));
            Vector2 perp = along.sqrMagnitude > 1e-4f ? new Vector2(-along.y, along.x).normalized : Vector2.up;
            if (perp.y < 0f) perp = -perp;
            _badge.position = clampWorld + (Vector3)(perp * 0.78f);
            _badge.rotation = Quaternion.identity;
            _badge.localScale = Vector3.one * BadgeScale * (1f + 0.25f * Mathf.Sin(_badgeBump * Mathf.PI));
            MechText.Sort(_badgeTextRenderer, _badgeSprite.sortingOrder + 1);
        }

        void SetTint(Color c)
        {
            c.a = _mount.color.a;
            _mount.color = c;
            _stem.color = c;
            if (_clampTint != _tint) return;   // a colour clamp keeps its colour
            var cc = c;
            cc.a = _clamp.color.a;
            _clamp.color = cc;
        }

        /// <param name="t">0 = clamped on the target, 1 = fully retracted into a nub on the mount.</param>
        void Layout(float t)
        {
            Vector2 dir = _clampPos - _weld;
            float len = dir.magnitude;
            Vector2 n = len > 1e-4f ? dir / len : Vector2.up;
            float ang = Mathf.Atan2(n.y, n.x) * Mathf.Rad2Deg;
            // refusal: a short side-to-side wiggle of the jaws; snap: a quick squeeze-pop
            float wiggle = _refuse > 0f ? Mathf.Sin(_refuse * 48f) * 9f * (_refuse / 0.45f) : 0f;
            var rot = Quaternion.Euler(0f, 0f, ang - 90f + wiggle);

            // Ease-out with a little overshoot: the clamp snaps back toward the mount.
            float e = t <= 0f ? 0f : 1f + 2.2f * Mathf.Pow(t - 1f, 3f) + 1.2f * Mathf.Pow(t - 1f, 2f);
            float curLen = Mathf.LerpUnclamped(len, NubLength, e);
            Vector2 clampAt = _weld + n * curLen;

            _mount.transform.localPosition = _weld;
            _mount.transform.localRotation = Quaternion.Euler(0f, 0f, ang - 90f);
            _mount.transform.localScale = Vector3.one * 0.82f; // keep the clamp the dominant shape

            _stem.transform.localPosition = _weld;
            _stem.transform.localRotation = Quaternion.Euler(0f, 0f, ang);
            _stem.size = new Vector2(Mathf.Max(curLen, 0.01f), _stemWidth);

            float pop = t > 0f ? 1f + 0.25f * Mathf.Sin(Mathf.Clamp01(t * 2.5f) * Mathf.PI) : 1f;
            if (_snap > 0f) pop *= 1f + 0.18f * Mathf.Sin((1f - _snap / 0.35f) * Mathf.PI);
            float shrink = Mathf.Lerp(1f, 0f, Mathf.Clamp01((t - 0.35f) / 0.65f));
            _clamp.transform.localPosition = clampAt;
            _clamp.transform.localRotation = rot;
            _clamp.transform.localScale = Vector3.one * pop * shrink;
            var cc = _clamp.color;
            cc.a = shrink;
            _clamp.color = cc;
            _clamp.enabled = shrink > 0.01f;
            if (_clampAccent != null)
            {
                _clampAccent.transform.localPosition = clampAt;
                _clampAccent.transform.localRotation = rot;
                _clampAccent.transform.localScale = _clamp.transform.localScale;
                _clampAccent.enabled = _clamp.enabled;
            }
            if (_coat != null)
            {
                float crack = _crack > 0f ? 1f + 0.12f * Mathf.Sin((1f - _crack / 0.3f) * Mathf.PI) : 1f;
                _coat.transform.localPosition = clampAt;
                _coat.transform.localRotation = rot;
                _coat.transform.localScale = _clamp.transform.localScale * crack * 1.1f;   // a thick shell over the jaws
                _coat.enabled = _clamp.enabled && _link.Active && _coat.enabled;
            }
            if (_stamp != null)
            {
                _stamp.transform.localPosition = clampAt + (Vector2)(rot * StampAt) * pop * shrink;
                _stamp.transform.localRotation = rot;
                _stamp.transform.localScale = Vector3.one * StampScale * pop * shrink;
                _stamp.enabled = _clamp.enabled;
            }
            _stem.enabled = curLen > 0.02f;
        }
    }
}
