using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Orcub.Game
{
    /// <summary>
    /// Brand splash that plays in the game scene and hands straight off to the board.
    ///
    ///   0.00 - 0.45  "orcub" on the felt (this exact frame is also the iOS launch screen)
    ///   0.45 - 0.95  r, c, u, b drift away and fade, one after another
    ///   0.55 - 1.50  the gear "o" (with its clamp) glides to the centre, grows and turns a little,
    ///                ratcheting softly, until it sits in the app-icon pose
    ///   1.50         it settles with a small squash, a soft clay "tock" and a haptic tap
    ///   1.50 - 2.15  hold on the mark (the brand moment)
    ///   2.15 - 2.60  the mark shrinks and fades while the board's rings drop in underneath
    ///
    /// Tapping anywhere skips straight to the exit.
    /// </summary>
    public sealed class SplashView : MonoBehaviour
    {
        const float LettersOutStart = 0.45f;
        const float ZoomStart = 0.55f;
        const float ZoomEnd = 1.50f;
        const float ExitStart = 2.15f;
        const float ExitDuration = 0.45f;
        const int BaseOrder = 300;

        // Mark geometry, in ring-sprite units (ring_r100: tube centre-line radius 1.0).
        const float RingBandOuter = 1.23f;       // radius + half tube width
        const float RingToothTip = 1.45f;        // radius + half tube + tooth height
        const float ClampOffsetDeg = 47f;        // clamp sits just past the gap's edge, as in the app icon
        const float ClampScale = 1.15f;
        const float WordGapAngle = -125f;        // in the word: gap lower-left (an "o" with a notch), clamp at the bottom
        const float LogoGapAngle = -50f;         // app-icon pose: gap lower-right, clamp on its right edge
        const float LogoWidthOfView = 0.46f;     // mark diameter (with teeth) as a fraction of screen width

        OrcubArt _art;
        Feedback _fx;
        Camera _cam;
        Action _onReveal, _onDone;

        Transform _word;                 // the whole word, fitted to the screen
        Transform _mark;                 // gear "o" + clamp, rotated so its local +X is the gap direction
        readonly List<Transform> _letters = new List<Transform>();
        readonly List<SpriteRenderer> _renderers = new List<SpriteRenderer>();
        readonly List<Color> _baseColors = new List<Color>();
        readonly List<SpriteRenderer> _letterRenderers = new List<SpriteRenderer>();
        readonly List<Vector3> _letterHomes = new List<Vector3>();

        Vector2 _markWordLocal;          // where the "o" sits inside the word
        float _markWordScale;            // its scale inside the word
        float _wordScale;
        Vector3 _wordPos;
        float _wordWidth, _wordHeight;

        // Zoom endpoints (world space), captured when the zoom starts.
        bool _zoomStarted, _settled, _revealed, _done;
        Vector3 _zoomFromPos;
        float _zoomFromScale, _zoomFromAngle;
        float _turnedForTicks;
        float _settleT = -1f;

        float _t;
        float _exitStart = ExitStart;

        public static SplashView Play(Transform parent, OrcubArt art, Camera cam, Feedback fx, ClayCrumbs crumbs,
            Action onReveal, Action onDone)
        {
            var go = new GameObject("Splash");
            go.transform.SetParent(parent, false);
            var s = go.AddComponent<SplashView>();
            s._art = art;
            s._cam = cam;
            s._fx = fx;
            s._onReveal = onReveal;
            s._onDone = onDone;
            s.Build();
            return s;
        }

        /// <summary>True while the splash is on screen and should own input.</summary>
        public bool IsBlocking => !_done && _t < _exitStart + 0.15f;

        SpriteRenderer Add(Transform parent, string name, Sprite sprite, Color color, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.color = color;
            sr.sortingOrder = BaseOrder + order;
            if (_art.spriteMaterial != null) sr.sharedMaterial = _art.spriteMaterial;
            _renderers.Add(sr);
            _baseColors.Add(color);
            return sr;
        }

        void Build()
        {
            _word = new GameObject("Word").transform;
            _word.SetParent(transform, false);

            // Word bounds, so the word can be centred and fitted.
            _markWordScale = _art.logoOHeight * 1.28f / (RingBandOuter * 2f);
            float oX = _art.logoOCenter.x - 0.34f;   // teeth need a little room from the "r"
            float minX = oX - RingToothTip * _markWordScale;
            float maxX = minX, minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < _art.logoLetters.Length; i++)
            {
                var b = _art.logoLetters[i].bounds;
                var c = _art.logoLetterCenters[i];
                maxX = Mathf.Max(maxX, c.x + b.extents.x);
                minY = Mathf.Min(minY, c.y - b.extents.y);
                maxY = Mathf.Max(maxY, c.y + b.extents.y);
            }
            var centre = new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f);
            _wordWidth = maxX - minX;
            _wordHeight = Mathf.Max(maxY - minY, 0.01f);

            // The mark: gear-ring + clamp as one rigid piece. Local +X = gap direction.
            _mark = new GameObject("Mark").transform;
            _mark.SetParent(_word, false);
            _markWordLocal = new Vector2(oX, _art.logoOCenter.y) - centre;
            _mark.localPosition = _markWordLocal;
            _mark.localScale = Vector3.one * _markWordScale;
            _mark.localRotation = Quaternion.Euler(0, 0, WordGapAngle);
            Add(_mark, "Ring", _art.RingSprite(1.0f, out _, out _), _art.RingColor(0), 0);
            var clamp = new GameObject("Clamp").transform;
            clamp.SetParent(_mark, false);
            float a = ClampOffsetDeg * Mathf.Deg2Rad;
            clamp.localPosition = new Vector2(Mathf.Cos(a), Mathf.Sin(a));          // on the tube centre-line
            clamp.localRotation = Quaternion.Euler(0, 0, ClampOffsetDeg + 90f);      // tube tangent, yoke outward
            clamp.localScale = Vector3.one * ClampScale;
            Add(clamp, "Clamp", _art.clamp, _art.hardwareTint, 5);
            if (_art.clampAccent != null) Add(clamp, "Dome", _art.clampAccent, _art.RingColor(2), 6);

            for (int i = 0; i < _art.logoLetters.Length; i++)
            {
                var t = new GameObject("letter_" + i).transform;
                t.SetParent(_word, false);
                t.localPosition = _art.logoLetterCenters[i] - centre;
                _letterHomes.Add(t.localPosition);
                int colorIndex = _art.logoLetterColors != null && i < _art.logoLetterColors.Length ? _art.logoLetterColors[i] : i + 1;
                _letterRenderers.Add(Add(t, "Body", _art.logoLetters[i], _art.RingColor(colorIndex), 1));
                _letters.Add(t);
            }
            Relayout();
        }

        float ViewH => _cam.orthographicSize * 2f;
        float ViewW => ViewH * _cam.aspect;
        Vector3 ViewCentre => new Vector3(_cam.transform.position.x, _cam.transform.position.y, 0f);

        /// <summary>Fit the word to the current camera: 80% of the view width, centred slightly above middle.</summary>
        public void Relayout()
        {
            _wordScale = Mathf.Min(ViewW * 0.80f / _wordWidth, ViewH * 0.22f / _wordHeight);
            _wordPos = ViewCentre + new Vector3(0f, ViewH * 0.04f, 0f);
            if (_zoomStarted) return;
            _word.position = _wordPos;
            _word.localScale = Vector3.one * _wordScale;
        }

        // Where the mark ends up: centre of the screen, app-icon pose.
        Vector3 LogoPos => ViewCentre + new Vector3(0f, ViewH * 0.04f, 0f);
        float LogoScale => LogoWidthOfView * ViewW / (2f * RingToothTip);

        static float EaseInOut(float x) => x < 0.5f ? 4f * x * x * x : 1f - Mathf.Pow(-2f * x + 2f, 3f) / 2f;
        static float EaseOutCubic(float x) => 1f - Mathf.Pow(1f - x, 3f);

        void Update()
        {
            if (_done) return;
#if UNITY_EDITOR
            // Editor-only: hold the first frame (used to capture the iOS launch image).
            if (UnityEditor.EditorPrefs.GetBool("Orcub.FreezeSplash", false))
            {
                Relayout();
                return;
            }
#endif
            Relayout();
            float dt = Time.deltaTime;
            _t += dt;

            var p = Pointer.current;
            if (p != null && p.press.wasPressedThisFrame && _t < _exitStart) _exitStart = _t; // tap to skip

            // 1) Letters drift away and fade, one after another.
            for (int i = 0; i < _letters.Count; i++)
            {
                float k = Mathf.Clamp01((_t - (LettersOutStart + 0.06f * i)) / 0.38f);
                float e = EaseOutCubic(k);
                _letters[i].localPosition = _letterHomes[i] + new Vector3(0.9f * e, -0.15f * e, 0f);
                _letters[i].localScale = Vector3.one * Mathf.Lerp(1f, 0.7f, e);
                SetAlphaFor(_letterRenderers[i], 1f - e);
            }

            // 2) The mark glides to the centre, grows, and turns into the app-icon pose.
            if (!_zoomStarted && _t >= ZoomStart)
            {
                _zoomStarted = true;
                _zoomFromPos = _mark.position;
                _zoomFromScale = _mark.lossyScale.x;
                _zoomFromAngle = WordGapAngle;
                _mark.SetParent(transform, true);
            }
            if (_zoomStarted && !_settled)
            {
                float k = Mathf.Clamp01((_t - ZoomStart) / (ZoomEnd - ZoomStart));
                float e = EaseInOut(k);
                _mark.position = Vector3.Lerp(_zoomFromPos, LogoPos, e);
                _mark.localScale = Vector3.one * Mathf.Lerp(_zoomFromScale, LogoScale, e) / transform.lossyScale.x;
                float angle = Mathf.Lerp(_zoomFromAngle, LogoGapAngle, e);
                _mark.rotation = Quaternion.Euler(0, 0, angle);
                // a few soft ratchet ticks as it turns
                const float detent = 15f;
                float turned = angle - _zoomFromAngle;
                if (Mathf.FloorToInt(turned / detent) != Mathf.FloorToInt(_turnedForTicks / detent))
                    _fx?.Tick(1.2f, 90f);
                _turnedForTicks = turned;
                if (k >= 1f)
                {
                    _settled = true;
                    _settleT = 0f;
                    _fx?.Settle();
                }
            }

            // 3) Settle: a small clay squash, then hold on the mark.
            if (_settled && _t < _exitStart)
            {
                _mark.position = LogoPos;
                _settleT += dt;
                float k = Mathf.Clamp01(_settleT / 0.32f);
                float squash = Mathf.Sin(k * Mathf.PI) * 0.07f * (1f - k);
                float s = LogoScale / transform.lossyScale.x;
                _mark.localScale = new Vector3(s * (1f + squash), s * (1f - squash), 1f);
            }

            // 4) Exit: the mark (or whatever is on screen when skipped) shrinks and fades; the board drops in.
            if (!_revealed && _t >= _exitStart + 0.05f)
            {
                _revealed = true;
                _onReveal?.Invoke();
            }
            if (_t >= _exitStart)
            {
                float k = Mathf.Clamp01((_t - _exitStart) / ExitDuration);
                float e = EaseOutCubic(k);
                float baseScale = _zoomStarted ? (_settled ? LogoScale : _mark.lossyScale.x) : _mark.lossyScale.x;
                if (_zoomStarted)
                    _mark.localScale = Vector3.one * baseScale * Mathf.Lerp(1f, 0.8f, e) / transform.lossyScale.x;
                SetAlphaAll(1f - e);
                if (k >= 1f) Finish();
            }
        }

        void SetAlphaFor(SpriteRenderer sr, float a)
        {
            int i = _renderers.IndexOf(sr);
            if (i < 0 || sr == null) return;
            var c = _baseColors[i];
            c.a *= Mathf.Clamp01(a);
            sr.color = c;
        }

        void SetAlphaAll(float a)
        {
            for (int i = 0; i < _renderers.Count; i++)
            {
                var sr = _renderers[i];
                if (sr == null) continue;
                var c = _baseColors[i];
                // letters may already be faded out; never bring them back
                c.a *= Mathf.Clamp01(Mathf.Min(a, _letterRenderers.Contains(sr) ? sr.color.a / Mathf.Max(_baseColors[i].a, 1e-4f) : 1f));
                sr.color = c;
            }
        }

        void Finish()
        {
            if (_done) return;
            _done = true;
            if (!_revealed) _onReveal?.Invoke();
            _onDone?.Invoke();
            Destroy(gameObject);
        }
    }
}
