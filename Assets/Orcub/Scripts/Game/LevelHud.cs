using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace Orcub.Game
{
    /// <summary>
    /// In-game UI, built in code (no prefabs):
    ///  - top band: Retry, "Level N", moves-left badge
    ///  - win card: stars pop in one by one, moves used, Next
    ///  - out-of-moves card: "+5 moves" (rewarded ad, once per attempt) or Retry
    ///  - DEV: level skip buttons + stats line at the bottom
    /// Everything sits inside the device safe area; the board camera leaves the top band free.
    /// </summary>
    public sealed class LevelHud : MonoBehaviour
    {
        public const float TopBandFraction = 0.11f;

        public event Action Restart, Prev, Next;

        static readonly Color Cream = new Color(1f, 0.95f, 0.88f);
        static readonly Color CardColor = new Color(0.98f, 0.93f, 0.86f);
        static readonly Color Plum = new Color(0.30f, 0.12f, 0.20f);
        static readonly Color Go = new Color(0.45f, 0.76f, 0.52f);
        static readonly Color Warn = new Color(0.95f, 0.45f, 0.38f);

        bool _devTools;
        Font _font;
        Sprite _star;
        CanvasGroup _group;
        RectTransform _safe;
        Text _title, _info, _moves, _movesLabel;
        RectTransform _movesBadge;
        Image _movesBg;
        readonly List<RectTransform> _hitAreas = new List<RectTransform>();
        Rect _lastSafe;
        static Sprite _pill;
        float _targetAlpha = 1f;
        int _movesLeft = 99;
        RectTransform _popup;
        Coroutine _popupAnim;

        public bool PopupOpen => _popup != null;

        public static LevelHud Create(Transform parent, Font font, Sprite star, bool devTools)
        {
            var go = new GameObject("HUD");
            go.transform.SetParent(parent, false);
            var hud = go.AddComponent<LevelHud>();
            hud._font = font != null ? font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            hud._star = star;
            hud._devTools = devTools;
            hud.BuildUi();
            return hud;
        }

        // ------------------------------------------------------------------ construction helpers

        void BuildUi()
        {
            if (EventSystem.current == null)
            {
                var es = new GameObject("EventSystem");
                es.transform.SetParent(transform, false);
                es.AddComponent<EventSystem>();
                es.AddComponent<InputSystemUIInputModule>();
            }

            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0.5f;
            gameObject.AddComponent<GraphicRaycaster>();
            _group = gameObject.AddComponent<CanvasGroup>();

            _safe = NewRect("SafeArea", transform);
            ApplySafeArea();

            var band = NewRect("TopBand", _safe);
            Stretch(band, new Vector2(0f, 1f - TopBandFraction), Vector2.one);

            _title = NewText("Title", band, 64, TextAnchor.MiddleCenter, Cream);
            Stretch(_title.rectTransform, new Vector2(0.27f, 0f), new Vector2(0.73f, 1f));
            AddShadow(_title);

            Button(band, "Retry", new Vector2(0.03f, 0.22f), new Vector2(0.24f, 0.78f), () => Restart?.Invoke(), 40,
                new Color(0f, 0f, 0f, 0.22f), Cream, track: true);

            // Moves badge
            _movesBadge = NewRect("Moves", band);
            Stretch(_movesBadge, new Vector2(0.76f, 0.1f), new Vector2(0.97f, 0.9f));
            _movesBg = _movesBadge.gameObject.AddComponent<Image>();
            _movesBg.sprite = Pill();
            _movesBg.type = Image.Type.Sliced;
            _movesBg.color = new Color(0f, 0f, 0f, 0.22f);
            _movesBg.raycastTarget = false;
            _moves = NewText("Count", _movesBadge, 60, TextAnchor.MiddleCenter, Cream);
            Stretch(_moves.rectTransform, new Vector2(0f, 0.28f), new Vector2(1f, 1f));
            _movesLabel = NewText("Label", _movesBadge, 32, TextAnchor.MiddleCenter, new Color(1f, 0.95f, 0.88f, 0.7f));
            Stretch(_movesLabel.rectTransform, new Vector2(0f, 0.02f), new Vector2(1f, 0.36f));
            _movesLabel.text = "moves";

            if (_devTools)
            {
                var bottom = NewRect("DevBand", _safe);
                Stretch(bottom, Vector2.zero, new Vector2(1f, 0.055f));
                Button(bottom, "‹", new Vector2(0.02f, 0.1f), new Vector2(0.13f, 0.95f), () => Prev?.Invoke(), 56,
                    new Color(0f, 0f, 0f, 0.18f), new Color(1f, 1f, 1f, 0.6f), track: true);
                Button(bottom, "›", new Vector2(0.87f, 0.1f), new Vector2(0.98f, 0.95f), () => Next?.Invoke(), 56,
                    new Color(0f, 0f, 0f, 0.18f), new Color(1f, 1f, 1f, 0.6f), track: true);
                _info = NewText("Info", bottom, 24, TextAnchor.MiddleCenter, new Color(1f, 1f, 1f, 0.45f));
                Stretch(_info.rectTransform, new Vector2(0.14f, 0f), new Vector2(0.86f, 1f));
                _info.horizontalOverflow = HorizontalWrapMode.Wrap;
            }
        }

        RectTransform NewRect(string name, Transform parent)
        {
            var rt = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            return rt;
        }

        static void Stretch(RectTransform rt, Vector2 min, Vector2 max)
        {
            rt.anchorMin = min;
            rt.anchorMax = max;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        Text NewText(string name, Transform parent, int size, TextAnchor anchor, Color color)
        {
            var t = NewRect(name, parent).gameObject.AddComponent<Text>();
            t.font = _font;
            t.fontSize = size;
            t.alignment = anchor;
            t.color = color;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        static void AddShadow(Graphic g, float alpha = 0.35f)
        {
            var s = g.gameObject.AddComponent<Shadow>();
            s.effectColor = new Color(0f, 0f, 0f, alpha);
            s.effectDistance = new Vector2(0f, -4f);
        }

        RectTransform Button(Transform parent, string label, Vector2 min, Vector2 max, Action onClick, int fontSize,
            Color bg, Color fg, bool track)
        {
            var rt = NewRect(label, parent);
            Stretch(rt, min, max);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = Pill();
            img.type = Image.Type.Sliced;
            img.color = bg;
            var btn = rt.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            var colors = btn.colors;
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            btn.colors = colors;
            btn.onClick.AddListener(() =>
            {
                Haptics.Play(Haptics.Impact.Light, 0.6f);
                onClick();
            });
            var t = NewText("Label", rt, fontSize, TextAnchor.MiddleCenter, fg);
            Stretch(t.rectTransform, Vector2.zero, Vector2.one);
            t.text = label;
            if (track) _hitAreas.Add(rt);
            return rt;
        }

        /// <summary>Rounded "pill" sprite generated at runtime (9-sliced), so the UI needs no texture assets.</summary>
        static Sprite Pill()
        {
            if (_pill != null) return _pill;
            const int n = 64, r = 28;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float cx = Mathf.Clamp(x + 0.5f, r, n - r), cy = Mathf.Clamp(y + 0.5f, r, n - r);
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                px[y * n + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(r - d + 0.5f));
            }
            tex.SetPixels(px);
            tex.Apply();
            _pill = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(r, r, r, r));
            return _pill;
        }

        void ApplySafeArea()
        {
            var sa = Screen.safeArea;
            _lastSafe = sa;
            if (Screen.width <= 0 || Screen.height <= 0) return;
            _safe.anchorMin = new Vector2(sa.xMin / Screen.width, sa.yMin / Screen.height);
            _safe.anchorMax = new Vector2(sa.xMax / Screen.width, sa.yMax / Screen.height);
            _safe.offsetMin = _safe.offsetMax = Vector2.zero;
        }

        // ------------------------------------------------------------------ runtime

        void Update()
        {
            if (Screen.safeArea != _lastSafe) ApplySafeArea();
            _group.alpha = Mathf.MoveTowards(_group.alpha, _targetAlpha, Time.unscaledDeltaTime * 3f);
            _group.interactable = _group.blocksRaycasts = _targetAlpha > 0.5f;

            // Moves badge pulses warm red when running low.
            bool low = _movesLeft <= 3;
            float pulse = low ? 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 6f) : 0f;
            _movesBg.color = low ? Color.Lerp(new Color(0f, 0f, 0f, 0.22f), new Color(Warn.r, Warn.g, Warn.b, 0.75f), pulse)
                                 : new Color(0f, 0f, 0f, 0.22f);
            float s = Mathf.Lerp(_movesBadge.localScale.x, 1f, 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime));
            _movesBadge.localScale = new Vector3(s, s, 1f);
        }

        public void SetLevel(int number, string info)
        {
            _title.text = $"Level {number}";
            if (_info != null) _info.text = info;
        }

        public void SetMoves(int movesLeft, bool bump = false)
        {
            _movesLeft = movesLeft;
            _moves.text = movesLeft.ToString();
            _movesLabel.text = movesLeft == 1 ? "move" : "moves";
            if (bump) _movesBadge.localScale = Vector3.one * 1.15f;
        }

        public void SetVisible(bool visible, bool instant = false)
        {
            _targetAlpha = visible ? 1f : 0f;
            if (instant && _group != null) _group.alpha = _targetAlpha;
        }

        /// <summary>True if a screen point is on a HUD control or a popup is open (the board should ignore it).</summary>
        public bool Covers(Vector2 screenPoint)
        {
            if (_popup != null) return true;
            if (_group.alpha < 0.5f) return false;
            foreach (var rt in _hitAreas)
                if (rt != null && RectTransformUtility.RectangleContainsScreenPoint(rt, screenPoint, null)) return true;
            return false;
        }

        // ------------------------------------------------------------------ popups

        RectTransform OpenCard(float heightFraction)
        {
            ClosePopup();
            _popup = NewRect("Popup", transform);
            Stretch(_popup, Vector2.zero, Vector2.one);
            var dim = _popup.gameObject.AddComponent<Image>();
            dim.color = new Color(0.05f, 0.01f, 0.04f, 0.55f);   // also blocks touches to the board

            var card = NewRect("Card", _popup);
            card.anchorMin = new Vector2(0.08f, 0.5f - heightFraction / 2f);
            card.anchorMax = new Vector2(0.92f, 0.5f + heightFraction / 2f);
            card.offsetMin = card.offsetMax = Vector2.zero;
            var img = card.gameObject.AddComponent<Image>();
            img.sprite = Pill();
            img.type = Image.Type.Sliced;
            img.pixelsPerUnitMultiplier = 0.45f;               // bigger corner radius on the card
            img.color = CardColor;
            var sh = card.gameObject.AddComponent<Shadow>();
            sh.effectColor = new Color(0f, 0f, 0f, 0.35f);
            sh.effectDistance = new Vector2(0f, -10f);

            if (_popupAnim != null) StopCoroutine(_popupAnim);
            _popupAnim = StartCoroutine(PopIn(card));
            return card;
        }

        IEnumerator PopIn(RectTransform card)
        {
            float t = 0f;
            while (t < 1f && card != null)
            {
                t += Time.unscaledDeltaTime / 0.32f;
                float k = Mathf.Clamp01(t);
                const float c1 = 1.7f, c3 = c1 + 1f;
                float back = 1f + c3 * Mathf.Pow(k - 1f, 3f) + c1 * Mathf.Pow(k - 1f, 2f);
                card.localScale = Vector3.one * Mathf.LerpUnclamped(0.7f, 1f, back);
                yield return null;
            }
        }

        public void ClosePopup()
        {
            if (_popup != null) Destroy(_popup.gameObject);
            _popup = null;
        }

        /// <summary>Level complete: stars pop in one by one, then Next.</summary>
        public void ShowWin(int level, int stars, int movesUsed, bool newBest, Action onNext)
        {
            var card = OpenCard(0.42f);
            var title = NewText("Title", card, 76, TextAnchor.MiddleCenter, Plum);
            Stretch(title.rectTransform, new Vector2(0f, 0.78f), new Vector2(1f, 0.95f));
            title.text = stars == 3 ? "Perfect!" : stars == 2 ? "Great!" : "Solved!";
            var sub = NewText("Sub", card, 36, TextAnchor.MiddleCenter, new Color(Plum.r, Plum.g, Plum.b, 0.7f));
            Stretch(sub.rectTransform, new Vector2(0f, 0.68f), new Vector2(1f, 0.78f));
            sub.text = $"Level {level}  ·  {movesUsed} moves" + (newBest ? "  ·  new best" : "");

            var row = NewRect("Stars", card);
            Stretch(row, new Vector2(0.12f, 0.3f), new Vector2(0.88f, 0.66f));
            var starImgs = new List<Image>();
            for (int i = 0; i < 3; i++)
            {
                var slot = NewRect("Star" + i, row);
                float x0 = i / 3f, x1 = (i + 1) / 3f;
                float lift = i == 1 ? 0.1f : 0f;               // middle star sits a little higher
                Stretch(slot, new Vector2(x0 + 0.02f, 0.05f + lift), new Vector2(x1 - 0.02f, 0.95f + lift));
                var empty = slot.gameObject.AddComponent<Image>();
                empty.sprite = _star;
                empty.preserveAspect = true;
                empty.color = new Color(0.25f, 0.1f, 0.16f, 0.25f);   // dim socket
                var full = NewRect("Full", slot).gameObject.AddComponent<Image>();
                Stretch(full.rectTransform, Vector2.zero, Vector2.one);
                full.sprite = _star;
                full.preserveAspect = true;
                full.transform.localScale = Vector3.zero;
                starImgs.Add(full);
            }

            var next = Button(card, "Next", new Vector2(0.22f, 0.07f), new Vector2(0.78f, 0.24f), () => { ClosePopup(); onNext(); }, 52, Go, Color.white, track: false);
            AddShadow(next.GetComponentInChildren<Text>(), 0.25f);
            StartCoroutine(PopStars(starImgs, stars));
        }

        IEnumerator PopStars(List<Image> imgs, int count)
        {
            yield return new WaitForSecondsRealtime(0.35f);
            for (int i = 0; i < count && i < imgs.Count; i++)
            {
                var img = imgs[i];
                if (img == null) yield break;
                Haptics.Play(i == count - 1 ? Haptics.Impact.Medium : Haptics.Impact.Light, 0.9f);
                float t = 0f;
                while (t < 1f && img != null)
                {
                    t += Time.unscaledDeltaTime / 0.28f;
                    float k = Mathf.Clamp01(t);
                    const float c1 = 2.2f, c3 = c1 + 1f;
                    float back = 1f + c3 * Mathf.Pow(k - 1f, 3f) + c1 * Mathf.Pow(k - 1f, 2f);
                    img.transform.localScale = Vector3.one * Mathf.Max(0f, back);
                    img.transform.localRotation = Quaternion.Euler(0, 0, (1f - k) * -25f);
                    yield return null;
                }
                yield return new WaitForSecondsRealtime(0.12f);
            }
        }

        /// <summary>Out of moves: watch an ad for +N moves (once per attempt), or retry.</summary>
        public void ShowOutOfMoves(int bonusMoves, bool canWatchAd, Action onWatchAd, Action onRetry)
        {
            var card = OpenCard(0.36f);
            var title = NewText("Title", card, 70, TextAnchor.MiddleCenter, Plum);
            Stretch(title.rectTransform, new Vector2(0f, 0.74f), new Vector2(1f, 0.94f));
            title.text = "Out of moves";
            var sub = NewText("Sub", card, 36, TextAnchor.MiddleCenter, new Color(Plum.r, Plum.g, Plum.b, 0.7f));
            Stretch(sub.rectTransform, new Vector2(0.06f, 0.56f), new Vector2(0.94f, 0.74f));
            sub.horizontalOverflow = HorizontalWrapMode.Wrap;
            sub.text = canWatchAd ? "So close! Keep going?" : "Give it another go.";

            if (canWatchAd)
            {
                var ad = Button(card, $"+{bonusMoves} moves  ·  watch ad", new Vector2(0.1f, 0.3f), new Vector2(0.9f, 0.5f),
                    () => { ClosePopup(); onWatchAd(); }, 44, Go, Color.white, track: false);
                AddShadow(ad.GetComponentInChildren<Text>(), 0.25f);
            }
            Button(card, "Retry", new Vector2(0.28f, 0.07f), new Vector2(0.72f, canWatchAd ? 0.24f : 0.4f),
                () => { ClosePopup(); onRetry(); }, 46, new Color(Plum.r, Plum.g, Plum.b, 0.85f), Cream, track: false);
        }

        /// <summary>Placeholder for a rewarded video until the ads SDK is wired in.</summary>
        public void ShowAdPlaceholder(float seconds, Action onFinished)
        {
            var card = OpenCard(0.2f);
            var t = NewText("Ad", card, 44, TextAnchor.MiddleCenter, Plum);
            Stretch(t.rectTransform, Vector2.zero, Vector2.one);
            t.text = "Rewarded ad plays here\n(placeholder)";
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            StartCoroutine(AfterRealtime(seconds, () => { ClosePopup(); onFinished(); }));
        }

        static IEnumerator AfterRealtime(float seconds, Action a)
        {
            yield return new WaitForSecondsRealtime(seconds);
            a();
        }
    }
}
