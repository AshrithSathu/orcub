using System.Collections.Generic;
using Orcub.Core;
using UnityEngine;

namespace Orcub.Game
{
    /// <summary>Visual for one gear-ring. Root sits at the ring centre and is rotated to the model's gap angle.</summary>
    public sealed class RingView : MonoBehaviour
    {
        public const int BodyOrder = 0;
        public const int LiftedOrderBoost = 100;

        public int RingId { get; private set; }
        public readonly List<HolderView> OwnedHolders = new List<HolderView>();

        Ring _ring;
        Transform _visual;   // child that carries shake / pull / scale so the logical root stays put
        SpriteRenderer _body;
        SpriteRenderer _trim;
        Color _color;

        SpriteRenderer _liftShadow;
        float _lift;          // 0..1, eased toward 1 while the ring is held
        bool _picked;
        float _hintTimer;
        float _wobbleTime;

        // Intro drop-in
        bool _introHidden;
        float _introDelay = -1f;
        float _introT;
        readonly List<SpriteRenderer> _introRenderers = new List<SpriteRenderer>();
        readonly List<Color> _introColors = new List<Color>();
        public System.Action<RingView> Landed;

        float _hintTime = -1f, _hintDelta;

        float _shakeTime;
        float _pulseTime;
        float _pickScale = 1f;
        Vector2 _pullOffset;
        bool _flying;
        Vector2 _flyVelocity;
        float _flyTime;

        public bool IsFlying => _flying;
        public Vector2 PullOffset => _pullOffset;

        public void Init(Ring ring, OrcubArt art)
        {
            RingId = ring.Id;
            _ring = ring;
            _color = art.RingColor(ring.Color);
            name = $"Ring_{ring.Id}";
            transform.localPosition = ring.Center;

            _visual = new GameObject("Visual").transform;
            _visual.SetParent(transform, false);

            var sprite = art.RingSprite(ring.Radius, out float scale, out Sprite trim);
            // Drop shadow that only appears while the ring is lifted by a finger.
            _liftShadow = CreateRenderer("LiftShadow", sprite, new Color(0.02f, 0.02f, 0.06f, 0f), BodyOrder - 1, art.spriteMaterial);
            _liftShadow.transform.localScale = Vector3.one * scale * 1.02f;
            _body = CreateRenderer("Body", sprite, _color, BodyOrder, art.spriteMaterial);
            _body.transform.localScale = Vector3.one * scale;
            if (trim != null)
            {
                _trim = CreateRenderer("Trim", trim, art.hardwareTint, BodyOrder + 1, art.spriteMaterial);
                _trim.transform.localScale = Vector3.one * scale;
            }
            Sync();
        }

        SpriteRenderer CreateRenderer(string childName, Sprite sprite, Color color, int order, Material material)
        {
            var go = new GameObject(childName);
            go.transform.SetParent(_visual, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.color = color;
            sr.sortingOrder = order;
            if (material != null) sr.sharedMaterial = material;
            return sr;
        }

        /// <summary>Holders welded to this ring live under the visual so they shake / fly with it.</summary>
        public Transform HolderParent => _visual;

        public void Sync()
        {
            if (_ring == null || _flying) return;
            transform.localRotation = Quaternion.Euler(0f, 0f, _ring.GapAngle);
        }

        public void Shake() => _shakeTime = 0.35f;

        /// <summary>Hint booster: the ring nods twice the way it should turn (visual only), with a pulse.</summary>
        public void ShowHint(float deltaDegrees)
        {
            _hintTime = 0f;
            _hintDelta = Mathf.Clamp(deltaDegrees, -35f, 35f);
            Pulse();
        }
        public void Pulse() => _pulseTime = 0.45f;
        public void SetPicked(bool picked)
        {
            _picked = picked;
            _pickScale = picked ? 1.04f : 1f;
        }

        /// <summary>When true the ring periodically wobbles to say "I'm free, pull me out".</summary>
        public bool FreeHint { get; set; }

        public void SetPullOffset(Vector2 worldOffset) => _pullOffset = worldOffset;

        public void SetLifted(bool lifted)
        {
            int boost = lifted ? LiftedOrderBoost : 0;
            foreach (var sr in GetComponentsInChildren<SpriteRenderer>(true))
            {
                // Base orders live in [-1, 50); lifted ones are base + boost.
                int baseOrder = sr.sortingOrder >= LiftedOrderBoost / 2 ? sr.sortingOrder - LiftedOrderBoost : sr.sortingOrder;
                sr.sortingOrder = baseOrder + boost;
            }
        }

        /// <summary>Hide the ring (and its holders) until <see cref="DropIn"/> is called.</summary>
        public void HideForIntro()
        {
            _introRenderers.Clear();
            _introColors.Clear();
            foreach (var sr in GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (sr == _liftShadow) continue;
                _introRenderers.Add(sr);
                _introColors.Add(sr.color);
            }
            _introHidden = true;
            _introDelay = -1f;
            SetIntroAlpha(0f);
        }

        /// <summary>Drop onto the board after <paramref name="delay"/> seconds: falls in from slightly large, settles with a bounce.</summary>
        public void DropIn(float delay)
        {
            if (!_introHidden) return;
            _introDelay = Mathf.Max(0f, delay);
            _introT = 0f;
        }

        public bool IsIntroPending => _introHidden;

        void SetIntroAlpha(float a)
        {
            for (int i = 0; i < _introRenderers.Count; i++)
            {
                if (_introRenderers[i] == null) continue;
                var c = _introColors[i];
                c.a *= a;
                _introRenderers[i].color = c;
            }
        }

        public void FlyAway(Vector2 direction, float speed)
        {
            _flying = true;
            _flyVelocity = direction.normalized * Mathf.Max(speed, 9f);
            SetLifted(true);
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (_flying)
            {
                _flyTime += dt;
                _flyVelocity *= 1f + 2.5f * dt; // accelerate out
                transform.localPosition += (Vector3)(_flyVelocity * dt);
                transform.Rotate(0f, 0f, 220f * dt * Mathf.Sign(_flyVelocity.x + 0.01f));
                float a = Mathf.Clamp01(1f - _flyTime / 0.7f);
                foreach (var sr in GetComponentsInChildren<SpriteRenderer>())
                {
                    var c = sr.color;
                    c.a = a;
                    sr.color = c;
                }
                if (_flyTime > 0.75f) Destroy(gameObject);
                return;
            }

            Sync();

            // Intro drop-in: scale from 1.3 with a soft overshoot, fade in, then report the landing.
            float introScale = 1f;
            if (_introHidden)
            {
                if (_introDelay < 0f)
                {
                    _visual.localScale = Vector3.one * 1.3f;
                    return;
                }
                if (_introDelay > 0f)
                {
                    _introDelay = Mathf.Max(0f, _introDelay - dt);
                    _visual.localScale = Vector3.one * 1.3f;
                    if (_introDelay > 0f) return;
                }
                _introT += dt / 0.34f;
                float k = Mathf.Clamp01(_introT);
                const float c1 = 1.9f, c3 = c1 + 1f;
                float back = 1f + c3 * Mathf.Pow(k - 1f, 3f) + c1 * Mathf.Pow(k - 1f, 2f); // ease-out-back
                introScale = Mathf.LerpUnclamped(1.3f, 1f, back);
                SetIntroAlpha(Mathf.Clamp01(k * 2.2f));
                _visual.localScale = Vector3.one * introScale;
                if (k >= 1f)
                {
                    _introHidden = false;
                    SetIntroAlpha(1f);
                    _visual.localScale = Vector3.one;
                    Landed?.Invoke(this);
                }
                return;
            }

            // Shake: quick damped wobble in world X, used when the player touches a locked ring.
            Vector2 shake = Vector2.zero;
            float shakeAngle = 0f;
            if (_shakeTime > 0f)
            {
                _shakeTime -= dt;
                float k = Mathf.Clamp01(_shakeTime / 0.35f);
                shake.x = Mathf.Sin(_shakeTime * 70f) * 0.07f * k;
                shakeAngle = Mathf.Sin(_shakeTime * 55f) * 4f * k;
            }

            // Free hint: a little wiggle every couple of seconds.
            if (FreeHint && !IsHeld)
            {
                _hintTimer += dt;
                if (_hintTimer > 2.3f)
                {
                    _hintTimer = 0f;
                    _wobbleTime = 0.55f;
                }
            }
            else _hintTimer = 1.6f; // first hint comes quickly after a ring frees up
            if (_wobbleTime > 0f)
            {
                _wobbleTime -= dt;
                float k = Mathf.Clamp01(_wobbleTime / 0.55f);
                shakeAngle += Mathf.Sin((1f - k) * Mathf.PI * 4f) * 5f * k;
            }

            if (_hintTime >= 0f)
            {
                _hintTime += dt;
                float k = Mathf.Clamp01(_hintTime / 1.6f);
                shakeAngle += Mathf.Sin(k * Mathf.PI * 2f) * Mathf.Sin(k * Mathf.PI) * _hintDelta;
                if (k >= 1f) _hintTime = -1f;
            }

            float pulse = 0f;
            if (_pulseTime > 0f)
            {
                _pulseTime -= dt;
                float t = 1f - Mathf.Clamp01(_pulseTime / 0.45f);
                pulse = Mathf.Sin(t * Mathf.PI) * 0.07f;
            }

            // _visual is in the ring's rotated frame; convert world offsets into that frame.
            Vector2 worldOffset = _pullOffset + shake;
            _visual.localPosition = Quaternion.Inverse(transform.localRotation) * worldOffset;
            _visual.localRotation = Quaternion.Euler(0f, 0f, shakeAngle);
            _lift = Mathf.MoveTowards(_lift, _picked || IsHeld ? 1f : 0f, dt * 7f);
            var sc = _liftShadow.color;
            sc.a = 0.38f * _lift;
            _liftShadow.color = sc;
            _liftShadow.enabled = _lift > 0.001f;
            // Shadow falls down-right in world space, further the higher the ring is lifted.
            Vector2 shadowWorld = new Vector2(0.06f, -0.11f) * _lift;
            _liftShadow.transform.localPosition = Quaternion.Inverse(transform.localRotation * _visual.localRotation) * shadowWorld;

            float targetScale = _pickScale + pulse;
            float s = Mathf.Lerp(_visual.localScale.x, targetScale, 1f - Mathf.Exp(-18f * dt));
            _visual.localScale = new Vector3(s, s, 1f);

            if (_pullOffset.sqrMagnitude > 0f && !IsHeld)
                _pullOffset = Vector2.Lerp(_pullOffset, Vector2.zero, 1f - Mathf.Exp(-14f * dt));
        }

        /// <summary>Set by the board while the player is dragging this ring; when false the pull offset springs back.</summary>
        public bool IsHeld { get; set; }

        public Color Color => _color;
    }
}
