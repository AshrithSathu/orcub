using Orcub.Core;
using UnityEngine;

namespace Orcub.Game
{
    /// <summary>
    /// A holder: mount welded on the owner's tube, a stem, and a clamp gripping the target's tube.
    /// Lives in the owner's visual space so it moves with the owner. On release it retracts into the mount.
    /// </summary>
    public sealed class HolderView : MonoBehaviour
    {
        public const int StemOrder = 10;
        public const int MountOrder = 11;
        public const int ClampOrder = 12;
        const float ReleaseDuration = 0.32f;
        const float NubLength = 0.16f;

        public int LinkId { get; private set; }

        SpriteRenderer _mount, _stem, _clamp, _clampAccent;
        Vector2 _weld, _clampPos;
        float _stemWidth;
        bool _released;
        float _releaseT;
        float _flash;
        Color _tint;

        public void Init(Link link, PuzzleModel model, RingView owner, OrcubArt art)
        {
            LinkId = link.Id;
            name = $"Holder_{link.Owner}->{link.Target}";
            transform.SetParent(owner.HolderParent, false);

            var space = owner.HolderParent;
            _weld = space.InverseTransformPoint(model.WeldPoint(link));
            _clampPos = space.InverseTransformPoint(model.ClampPoint(link));
            _stemWidth = art.stemWidth;

            // Holders are their own clay "hardware" colour; the dome on the clamp carries the owner's colour
            // so you can read who holds whom.
            _tint = art.hardwareTint;
            _tint.a = 1f;

            _stem = Create("Stem", art.stem, _tint, StemOrder, art.spriteMaterial);
            _stem.drawMode = SpriteDrawMode.Sliced;
            _mount = Create("Mount", art.mount, _tint, MountOrder, art.spriteMaterial);
            _clamp = Create("Clamp", art.clamp, _tint, ClampOrder, art.spriteMaterial);
            if (art.clampAccent != null)
                _clampAccent = Create("ClampAccent", art.clampAccent, owner.Color, ClampOrder + 1, art.spriteMaterial);

            Layout(0f);
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

        /// <summary>Show the holder already retracted (board rebuilt after Undo).</summary>
        public void ReleaseInstant()
        {
            _released = true;
            _releaseT = 1f;
            Layout(1f);
        }

        public void Release()
        {
            if (_released) return;
            _released = true;
            _releaseT = 0f;
        }

        /// <summary>Brief highlight used when the player touches a locked owner ring.</summary>
        public void Flash() => _flash = 0.4f;

        void Update()
        {
            if (_released && _releaseT < 1f)
            {
                _releaseT = Mathf.Min(1f, _releaseT + Time.deltaTime / ReleaseDuration);
                Layout(_releaseT);
            }

            if (_flash > 0f)
            {
                _flash -= Time.deltaTime;
                float k = Mathf.Clamp01(_flash / 0.4f);
                var c = Color.Lerp(_tint, Color.white, 0.55f * Mathf.Sin(k * Mathf.PI));
                SetTint(c);
                if (_flash <= 0f) SetTint(_tint);
            }
        }

        void SetTint(Color c)
        {
            c.a = _mount.color.a;
            _mount.color = c;
            _stem.color = c;
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
            var rot = Quaternion.Euler(0f, 0f, ang - 90f);

            // Ease-out with a little overshoot: the clamp snaps back toward the mount.
            float e = t <= 0f ? 0f : 1f + 2.2f * Mathf.Pow(t - 1f, 3f) + 1.2f * Mathf.Pow(t - 1f, 2f);
            float curLen = Mathf.LerpUnclamped(len, NubLength, e);
            Vector2 clampAt = _weld + n * curLen;

            _mount.transform.localPosition = _weld;
            _mount.transform.localRotation = rot;
            _mount.transform.localScale = Vector3.one * 0.82f; // keep the clamp the dominant shape

            _stem.transform.localPosition = _weld;
            _stem.transform.localRotation = Quaternion.Euler(0f, 0f, ang);
            _stem.size = new Vector2(Mathf.Max(curLen, 0.01f), _stemWidth);

            float pop = t > 0f ? 1f + 0.25f * Mathf.Sin(Mathf.Clamp01(t * 2.5f) * Mathf.PI) : 1f;
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
            _stem.enabled = curLen > 0.02f;
        }
    }
}
