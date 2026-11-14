using UnityEngine;

namespace Orcub.Game
{
    /// <summary>
    /// The gear-wreath frame behind the win / out-of-moves cards: two big lit clay arcs joined by tan clamps,
    /// over a dimmed board. World-space (so it gets the same live clay lighting as the rings); the card text and
    /// buttons are drawn on top by <see cref="LevelHud"/>.
    /// Animation: arcs swing in from opposite sides and settle with a small overshoot, then the clamps snap on.
    /// </summary>
    public sealed class WreathView : MonoBehaviour
    {
        const int DimOrder = 200, ArcOrder = 210, ClampOrder = 214;
        const float InDuration = 0.5f, OutDuration = 0.22f;

        OrcubArt _art;
        Camera _cam;
        SpriteRenderer _dim, _arcA, _arcB;
        SpriteRenderer[] _clamps;
        Transform _frame;
        float _t = -1f, _outT = -1f;
        bool _clampsSnapped;
        static Sprite _white;

        /// <summary>Diameter of the wreath (outer teeth) as a fraction of the screen width.</summary>
        public const float WidthOfView = 0.88f;

        public static WreathView Create(Transform parent, OrcubArt art, Camera cam)
        {
            var go = new GameObject("Wreath");
            go.transform.SetParent(parent, false);
            var w = go.AddComponent<WreathView>();
            w._art = art;
            w._cam = cam;
            w.Build();
            go.SetActive(false);
            return w;
        }

        SpriteRenderer Sprite(Transform parent, string name, Sprite sprite, Color color, int order, bool lit = true)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.color = color;
            sr.sortingOrder = order;
            var mat = lit ? _art.spriteMaterial : (_art.unlitMaterial != null ? _art.unlitMaterial : _art.spriteMaterial);
            if (mat != null) sr.sharedMaterial = mat;
            return sr;
        }

        void Build()
        {
            if (_white == null)
            {
                var tex = new Texture2D(4, 4);
                var px = new Color[16];
                for (int i = 0; i < 16; i++) px[i] = Color.white;
                tex.SetPixels(px);
                tex.Apply();
                _white = UnityEngine.Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);
            }
            _dim = Sprite(transform, "Dim", _white, new Color(0.06f, 0.01f, 0.04f, 0f), DimOrder, lit: false);
            _frame = new GameObject("Frame").transform;
            _frame.SetParent(transform, false);
            _arcA = Sprite(_frame, "ArcA", _art.wreathArcA, _art.RingColor(0), ArcOrder);       // coral
            _arcB = Sprite(_frame, "ArcB", _art.wreathArcB, _art.RingColor(1), ArcOrder + 1);   // amber
            // Clamps where the two arcs meet (upper right) and on the coral arc's lower-left end.
            _clamps = new[]
            {
                ClampAt(35f),
                ClampAt(212f),
            };
        }

        SpriteRenderer ClampAt(float angleDeg)
        {
            var t = new GameObject("Clamp").transform;
            t.SetParent(_frame, false);
            float a = angleDeg * Mathf.Deg2Rad;
            t.localPosition = new Vector3(Mathf.Cos(a), Mathf.Sin(a)) * _art.wreathRadius;
            t.localRotation = Quaternion.Euler(0, 0, angleDeg + 90f);
            t.localScale = Vector3.one * 1.45f;
            return Sprite(t, "Body", _art.clamp, _art.hardwareTint, ClampOrder);
        }

        public bool Visible => gameObject.activeSelf && _outT < 0f;

        /// <param name="frame">false = only dim the board (cream cards bring their own panel)</param>
        public void Show(bool frame = true)
        {
            gameObject.SetActive(true);
            _frame.gameObject.SetActive(frame);
            _t = 0f;
            _outT = -1f;
            _clampsSnapped = false;
            Layout();
        }

        public void Hide()
        {
            if (!gameObject.activeSelf || _outT >= 0f) return;
            _outT = 0f;
        }

        void Layout()
        {
            var c = _cam.transform.position;
            float viewH = _cam.orthographicSize * 2f, viewW = viewH * _cam.aspect;
            transform.position = new Vector3(c.x, c.y, 0f);
            _dim.transform.localScale = new Vector3(viewW * 1.2f, viewH * 1.2f, 1f);
            float outer = _art.wreathRadius + 0.55f;
            float s = WidthOfView * viewW / (2f * outer);
            _frame.localScale = Vector3.one * s;
        }

        static float Back(float k, float c1 = 1.6f)
        {
            float c3 = c1 + 1f;
            return 1f + c3 * Mathf.Pow(k - 1f, 3f) + c1 * Mathf.Pow(k - 1f, 2f);
        }

        void Update()
        {
            Layout();
            float dt = Time.unscaledDeltaTime;
            float alpha;
            if (_outT >= 0f)
            {
                _outT += dt;
                float k = Mathf.Clamp01(_outT / OutDuration);
                alpha = 1f - k;
                _frame.localRotation = Quaternion.Euler(0, 0, k * 12f);
                if (k >= 1f)
                {
                    _outT = -1f;
                    gameObject.SetActive(false);
                    return;
                }
            }
            else
            {
                _t += dt;
                float k = Mathf.Clamp01(_t / InDuration);
                float e = Back(k);
                alpha = Mathf.Clamp01(k * 2.5f);
                // the arcs swing in from opposite directions and lock together
                _arcA.transform.localRotation = Quaternion.Euler(0, 0, Mathf.LerpUnclamped(55f, 0f, e));
                _arcB.transform.localRotation = Quaternion.Euler(0, 0, Mathf.LerpUnclamped(-55f, 0f, e));
                _frame.localScale *= Mathf.LerpUnclamped(0.86f, 1f, e);
                // clamps snap on as the arcs land
                float ck = Mathf.Clamp01((_t - InDuration * 0.75f) / 0.2f);
                foreach (var cl in _clamps) cl.transform.parent.localScale = Vector3.one * 1.45f * Back(ck, 2.4f);
                if (ck > 0f && !_clampsSnapped)
                {
                    _clampsSnapped = true;
                    Haptics.Play(Haptics.Impact.Light, 0.7f);
                }
            }
            var d = _dim.color;
            d.a = 0.975f * alpha;   // linear-space blend: needs ~0.97 to read as "well back" so the card stands alone
            _dim.color = d;
            foreach (var sr in new[] { _arcA, _arcB, _clamps[0], _clamps[1] })
            {
                var col = sr.color;
                col.a = alpha;
                sr.color = col;
            }
        }
    }
}
