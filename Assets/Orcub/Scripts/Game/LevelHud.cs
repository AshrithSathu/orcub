using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using Orcub.Core;

namespace Orcub.Game
{
    /// <summary>
    /// In-game UI (approved direction, decluttered):
    ///  - top bar: one cream clay slab with "Level N | moves" and a round mint restart button (#1)
    ///  - win card: inside the gear wreath (#4) - title, three clay stars, a Next pill, a small "Replay" link
    ///  - out-of-moves card: inside the wreath - title, one "+5 moves" pill with a film glyph, a small "Retry" link
    ///  - DEV: level skip buttons + stats line at the bottom (devTools only)
    /// The wreath itself is world-space (<see cref="WreathView"/>); this draws the text and buttons over it.
    /// </summary>
    public sealed class LevelHud : MonoBehaviour
    {
        public const float TopBandFraction = 0.11f;

        public event Action Pause, Prev, Next;

        static readonly Color Ink = new Color(0.23f, 0.09f, 0.15f);        // plum text on clay
        static readonly Color Cream = new Color(1f, 0.95f, 0.88f);         // text on felt
        static readonly Color Mint = new Color(0.49f, 0.80f, 0.56f);
        static readonly Color Coral = new Color(0.95f, 0.51f, 0.42f);
        static readonly Color StarGold = new Color(0.97f, 0.78f, 0.30f);
        static readonly Color StarTan = new Color(0.74f, 0.62f, 0.52f);
        static readonly Color SlabTint = new Color(0.98f, 0.92f, 0.82f);
        static readonly Color HeartRed = new Color(0.95f, 0.43f, 0.37f);
        static readonly Color Brass = new Color(0.89f, 0.70f, 0.38f);
        static readonly Color SwitchOff = new Color(0.55f, 0.42f, 0.48f);

        OrcubArt _art;
        bool _devTools;
        Font _font;
        CanvasGroup _group;
        RectTransform _canvasRect, _safe, _slab;
        Text _level, _moves, _movesLabel, _info, _lives;
        float _livesRefresh;
        readonly List<RectTransform> _hitAreas = new List<RectTransform>();
        Rect _lastSafe;
        float _targetAlpha = 1f;
        int _movesLeft = 99;
        float _movesBump;
        RectTransform _popup, _card;

        public bool PopupOpen => _popup != null;

        public static LevelHud Create(Transform parent, OrcubArt art, bool devTools)
        {
            var go = new GameObject("HUD");
            go.transform.SetParent(parent, false);
            var hud = go.AddComponent<LevelHud>();
            hud._art = art;
            hud._font = art.uiFont != null ? art.uiFont : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            hud._devTools = devTools;
            hud.BuildUi();
            return hud;
        }

        // ------------------------------------------------------------------ helpers

        RectTransform Rect(string name, Transform parent)
        {
            var rt = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            return rt;
        }

        static void Anchor(RectTransform rt, float x0, float y0, float x1, float y1)
        {
            rt.anchorMin = new Vector2(x0, y0);
            rt.anchorMax = new Vector2(x1, y1);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        Text Label(Transform parent, string text, int size, Color color, TextAnchor anchor = TextAnchor.MiddleCenter)
        {
            var t = Rect("Text", parent).gameObject.AddComponent<Text>();
            t.font = _font;
            t.fontSize = size;
            t.color = color;
            t.alignment = anchor;
            t.text = text;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        Image Img(Transform parent, Sprite sprite, Color color, bool sliced = false)
        {
            var img = Rect(sprite != null ? sprite.name : "Image", parent).gameObject.AddComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            img.raycastTarget = false;
            if (sliced)
            {
                img.type = Image.Type.Sliced;
                img.gameObject.AddComponent<RoundCaps>();
            }
            else img.preserveAspect = true;
            return img;
        }

        /// <summary>Puffy clay pill button: sliced pill sprite, label, optional glyph, squash on press.</summary>
        RectTransform Pill(Transform parent, string label, Color tint, int fontSize, Action onClick, Sprite glyph = null,
            Color? glyphTint = null)
        {
            var rt = Rect(label, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = _art.uiPill;
            img.type = Image.Type.Sliced;
            img.color = tint;
            rt.gameObject.AddComponent<RoundCaps>();
            var btn = rt.gameObject.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(() => { Haptics.Play(Haptics.Impact.Light, 0.6f); onClick(); });
            rt.gameObject.AddComponent<PressSquash>();
            var text = Label(rt, label, fontSize, Ink);
            if (glyph != null)
            {
                Anchor(text.rectTransform, 0.30f, 0.06f, 0.95f, 0.94f);
                var g = Img(rt, glyph, glyphTint ?? Ink);
                Anchor(g.rectTransform, 0.10f, 0.24f, 0.30f, 0.80f);
            }
            else Anchor(text.rectTransform, 0f, 0.06f, 1f, 0.94f);
            return rt;
        }

        /// <summary>Small underlined text action ("Replay", "Retry").</summary>
        RectTransform TextLink(Transform parent, string label, Action onClick)
        {
            var rt = Rect(label, parent);
            var hit = rt.gameObject.AddComponent<Image>();
            hit.color = new Color(0, 0, 0, 0);
            var btn = rt.gameObject.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(() => { Haptics.Play(Haptics.Impact.Light, 0.5f); onClick(); });
            rt.gameObject.AddComponent<PressSquash>();
            var t = Label(rt, label, 44, Cream);
            Anchor(t.rectTransform, 0f, 0.15f, 1f, 1f);
            var line = Img(rt, null, new Color(Cream.r, Cream.g, Cream.b, 0.7f), sliced: false);
            Anchor(line.rectTransform, 0.3f, 0.08f, 0.7f, 0.12f);
            return rt;
        }

        // ------------------------------------------------------------------ build

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
            scaler.matchWidthOrHeight = 0f;   // scale with width: phone layouts stay identical across heights
            gameObject.AddComponent<GraphicRaycaster>();
            _group = gameObject.AddComponent<CanvasGroup>();
            _canvasRect = (RectTransform)transform;

            _safe = Rect("SafeArea", transform);
            ApplySafeArea();

            // ---- top bar: one clay slab
            var band = Rect("TopBand", _safe);
            Anchor(band, 0f, 1f - TopBandFraction, 1f, 1f);
            _slab = Img(band, _art.uiSlab, SlabTint, sliced: true).rectTransform;
            Anchor(_slab, 0.04f, 0.14f, 0.96f, 0.92f);

            // round coral pause button at the slab's left end (approved top bar option 3)
            var pause = Rect("Pause", _slab);
            Anchor(pause, 0.055f, 0.14f, 0.055f, 0.86f);
            pause.pivot = new Vector2(0f, 0.5f);
            var pImg = pause.gameObject.AddComponent<Image>();
            pImg.sprite = _art.uiRound;
            pImg.color = Coral;
            pImg.preserveAspect = true;
            pause.gameObject.AddComponent<AspectRatioFitter>().aspectMode = AspectRatioFitter.AspectMode.HeightControlsWidth;
            var pBtn = pause.gameObject.AddComponent<Button>();
            pBtn.transition = Selectable.Transition.None;
            pBtn.onClick.AddListener(() => { Haptics.Play(Haptics.Impact.Light, 0.6f); Pause?.Invoke(); });
            pause.gameObject.AddComponent<PressSquash>();
            var pGlyph = Img(pause, _art.uiPause, Cream);
            Anchor(pGlyph.rectTransform, 0.27f, 0.25f, 0.73f, 0.75f);
            _hitAreas.Add(pause);

            _level = Label(_slab, "Level 1", 52, Ink);
            Anchor(_level.rectTransform, 0.2f, 0.08f, 0.5f, 0.92f);
            var divider = Img(_slab, null, new Color(Ink.r, Ink.g, Ink.b, 0.22f));
            Anchor(divider.rectTransform, 0.515f, 0.28f, 0.52f, 0.72f);
            _moves = Label(_slab, "0", 58, Ink, TextAnchor.MiddleRight);
            Anchor(_moves.rectTransform, 0.53f, 0.08f, 0.63f, 0.92f);
            _movesLabel = Label(_slab, "moves", 38, Ink, TextAnchor.MiddleLeft);
            Anchor(_movesLabel.rectTransform, 0.645f, 0.08f, 0.8f, 0.86f);

            // lives: clay heart with the count, at the slab's right end
            var heart = Rect("Lives", _slab);
            Anchor(heart, 0.95f, 0.04f, 0.95f, 0.96f);
            heart.pivot = new Vector2(1f, 0.5f);
            var hImg = heart.gameObject.AddComponent<Image>();
            hImg.sprite = _art.uiHeartClay;
            hImg.color = HeartRed;
            hImg.preserveAspect = true;
            hImg.raycastTarget = false;
            heart.gameObject.AddComponent<AspectRatioFitter>().aspectMode = AspectRatioFitter.AspectMode.HeightControlsWidth;
            _lives = Label(heart, Lives.Count.ToString(), 44, Cream);
            Anchor(_lives.rectTransform, 0f, 0.1f, 1f, 0.9f);

            if (_devTools)
            {
                var bottom = Rect("DevBand", _safe);
                Anchor(bottom, 0f, 0f, 1f, 0.05f);
                var prev = DevButton(bottom, "‹", 0.02f, 0.13f, () => Prev?.Invoke());
                var next = DevButton(bottom, "›", 0.87f, 0.98f, () => Next?.Invoke());
                _hitAreas.Add(prev);
                _hitAreas.Add(next);
                _info = Label(bottom, "", 22, new Color(1f, 1f, 1f, 0.4f));
                Anchor(_info.rectTransform, 0.14f, 0f, 0.86f, 1f);
                _info.horizontalOverflow = HorizontalWrapMode.Wrap;
            }
        }

        RectTransform DevButton(Transform parent, string label, float x0, float x1, Action onClick)
        {
            var rt = Rect(label, parent);
            Anchor(rt, x0, 0.08f, x1, 0.95f);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = _art.uiPill;
            img.type = Image.Type.Sliced;
            img.color = new Color(1f, 1f, 1f, 0.12f);
            rt.gameObject.AddComponent<RoundCaps>();
            var btn = rt.gameObject.AddComponent<Button>();
            btn.onClick.AddListener(() => onClick());
            var t = Label(rt, label, 52, new Color(1f, 1f, 1f, 0.6f));
            Anchor(t.rectTransform, 0, 0, 1, 1);
            return rt;
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

            // moves number: bumps on change, turns coral and breathes when 3 or fewer remain
            _movesBump = Mathf.MoveTowards(_movesBump, 0f, Time.unscaledDeltaTime * 3f);
            bool low = _movesLeft <= 3;
            float breathe = low ? 0.06f * (0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 6f)) : 0f;
            _moves.transform.localScale = Vector3.one * (1f + _movesBump * 0.25f + breathe);
            _moves.color = low ? Color.Lerp(Ink, Coral, 0.9f) : Ink;

            _livesRefresh -= Time.unscaledDeltaTime;
            if (_livesRefresh <= 0f)
            {
                _livesRefresh = 1f;
                _lives.text = Lives.Count.ToString();
            }

            if (_card != null)
            {
                // keep the card content inside the wreath's ring, whatever the screen size
                float w = _canvasRect.rect.width * WreathView.WidthOfView * 0.86f;
                _card.sizeDelta = new Vector2(w, w);
            }
        }

        public void SetLevel(int number, string info)
        {
            _level.text = $"Level {number}";
            if (_info != null) _info.text = info;
        }

        public void SetMoves(int movesLeft, bool bump = false)
        {
            _movesLeft = movesLeft;
            _moves.text = movesLeft.ToString();
            _movesLabel.text = movesLeft == 1 ? "move" : "moves";
            if (bump) _movesBump = 1f;
        }

        public void SetVisible(bool visible, bool instant = false)
        {
            _targetAlpha = visible ? 1f : 0f;
            if (instant && _group != null) _group.alpha = _targetAlpha;
        }

        /// <summary>True if a screen point is on a HUD control or a card is open (the board should ignore it).</summary>
        public bool Covers(Vector2 screenPoint)
        {
            if (_popup != null) return true;
            if (_group.alpha < 0.5f) return false;
            foreach (var rt in _hitAreas)
                if (rt != null && RectTransformUtility.RectangleContainsScreenPoint(rt, screenPoint, null)) return true;
            return false;
        }

        // ------------------------------------------------------------------ cards (drawn inside the wreath)

        RectTransform OpenCard()
        {
            ClosePopup();
            _popup = Rect("Popup", transform);
            Anchor(_popup, 0, 0, 1, 1);
            var blocker = _popup.gameObject.AddComponent<Image>();
            blocker.color = new Color(0, 0, 0, 0);        // the dim is the wreath's; this only blocks touches
            _card = Rect("Card", _popup);
            _card.anchorMin = _card.anchorMax = new Vector2(0.5f, 0.5f);
            _card.pivot = new Vector2(0.5f, 0.5f);
            float w = _canvasRect.rect.width * WreathView.WidthOfView * 0.86f;
            _card.sizeDelta = new Vector2(w, w);
            StartCoroutine(FadeIn(_card.gameObject.AddComponent<CanvasGroup>()));
            return _card;
        }

        static IEnumerator FadeIn(CanvasGroup g)
        {
            g.alpha = 0f;
            yield return new WaitForSecondsRealtime(0.28f);   // let the wreath arcs swing in first
            float t = 0f;
            while (t < 1f && g != null)
            {
                t += Time.unscaledDeltaTime / 0.2f;
                g.alpha = t;
                g.transform.localScale = Vector3.one * Mathf.Lerp(0.92f, 1f, t);
                yield return null;
            }
        }

        public void ClosePopup()
        {
            if (_popup != null) Destroy(_popup.gameObject);
            _popup = null;
            _card = null;
        }

        /// <summary>Level complete: title, three clay stars (they pop in one by one), Next, Replay link.</summary>
        public void ShowWin(int level, int stars, Action onNext, Action onReplay)
        {
            var card = OpenCard();
            var title = Label(card, $"Level {level}", 84, Cream);
            Anchor(title.rectTransform, 0f, 0.77f, 1f, 0.93f);
            Shadow(title);

            var full = new List<Image>();
            for (int i = 0; i < 3; i++)
            {
                float x0 = 0.08f + i * 0.285f, lift = i == 1 ? 0.05f : 0f;
                var socket = Img(card, _art.uiStarClay, StarTan);
                Anchor(socket.rectTransform, x0, 0.49f + lift, x0 + 0.27f, 0.74f + lift);
                var star = Img(socket.transform, _art.uiStarClay, StarGold);
                Anchor(star.rectTransform, 0, 0, 1, 1);
                star.transform.localScale = Vector3.zero;
                full.Add(star);
            }
            var next = Pill(card, "Next", Mint, 64, () => { ClosePopup(); onNext(); });
            Anchor(next, 0.12f, 0.29f, 0.88f, 0.46f);
            var replay = TextLink(card, "Replay", () => { ClosePopup(); onReplay(); });
            Anchor(replay, 0.32f, 0.15f, 0.68f, 0.26f);   // inside the ring, clear of the arc ends
            StartCoroutine(PopStars(full, stars));
        }

        IEnumerator PopStars(List<Image> stars, int count)
        {
            yield return new WaitForSecondsRealtime(0.6f);
            for (int i = 0; i < count && i < stars.Count; i++)
            {
                var img = stars[i];
                if (img == null) yield break;
                Haptics.Play(i == count - 1 ? Haptics.Impact.Medium : Haptics.Impact.Light, 0.9f);
                float t = 0f;
                while (t < 1f && img != null)
                {
                    t += Time.unscaledDeltaTime / 0.3f;
                    float k = Mathf.Clamp01(t);
                    const float c1 = 2.2f, c3 = c1 + 1f;
                    float back = 1f + c3 * Mathf.Pow(k - 1f, 3f) + c1 * Mathf.Pow(k - 1f, 2f);
                    img.transform.localScale = Vector3.one * Mathf.Max(0f, back);
                    img.transform.localRotation = Quaternion.Euler(0, 0, (1f - k) * -30f);
                    yield return null;
                }
                yield return new WaitForSecondsRealtime(0.14f);
            }
        }

        /// <summary>Out of moves: one "+N moves" pill (rewarded ad) and a Retry link. No pill if the ad was used.</summary>
        public void ShowOutOfMoves(int bonusMoves, bool canWatchAd, Action onWatchAd, Action onRetry)
        {
            var card = OpenCard();
            var title = Label(card, "Out of moves", 76, Cream);
            Anchor(title.rectTransform, 0f, 0.68f, 1f, 0.85f);
            Shadow(title);
            if (canWatchAd)
            {
                var ad = Pill(card, $"+{bonusMoves} moves", Coral, 60, () => { ClosePopup(); onWatchAd(); }, _art.uiFilm);
                Anchor(ad, 0.06f, 0.4f, 0.94f, 0.6f);
            }
            var retry = TextLink(card, "Retry", () => { ClosePopup(); onRetry(); });
            Anchor(retry, 0.32f, canWatchAd ? 0.22f : 0.42f, 0.68f, canWatchAd ? 0.33f : 0.54f);
        }

        /// <summary>Clay toggle: icon + label + pill track with a sliding cream knob.</summary>
        void Toggle(Transform parent, Sprite icon, string label, bool on, Action<bool> changed, float y0, float y1)
        {
            var row = Rect(label, parent);
            Anchor(row, 0.08f, y0, 0.92f, y1);
            var ic = Img(row, icon, Cream);
            Anchor(ic.rectTransform, 0f, 0.12f, 0.16f, 0.88f);
            var text = Label(row, label, 46, Cream, TextAnchor.MiddleLeft);
            Anchor(text.rectTransform, 0.2f, 0f, 0.62f, 1f);

            var track = Rect("Switch", row);
            Anchor(track, 0.66f, 0.1f, 1f, 0.9f);
            var tImg = track.gameObject.AddComponent<Image>();
            tImg.sprite = _art.uiPill;
            tImg.type = Image.Type.Sliced;
            track.gameObject.AddComponent<RoundCaps>();
            var knob = Rect("Knob", track);
            var kImg = knob.gameObject.AddComponent<Image>();
            kImg.sprite = _art.uiRound;
            kImg.color = new Color(1f, 0.97f, 0.92f);
            kImg.preserveAspect = true;
            kImg.raycastTarget = false;
            knob.gameObject.AddComponent<AspectRatioFitter>().aspectMode = AspectRatioFitter.AspectMode.HeightControlsWidth;

            bool state = on;
            void Show()
            {
                tImg.color = state ? Mint : SwitchOff;
                float x = state ? 0.97f : 0.03f;
                knob.anchorMin = new Vector2(x, 0.04f);
                knob.anchorMax = new Vector2(x, 0.96f);
                knob.pivot = new Vector2(state ? 1f : 0f, 0.5f);
                knob.offsetMin = knob.offsetMax = Vector2.zero;
            }
            Show();
            var btn = track.gameObject.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(() =>
            {
                state = !state;
                Show();
                changed(state);
                Haptics.Play(Haptics.Impact.Light, 0.6f);   // fires only if haptics are (now) on
            });
        }

        /// <summary>Pause / settings: sound + haptics switches, Restart (costs a life), Resume.</summary>
        public void ShowPause(Action onRestart, Action onResume)
        {
            var card = OpenCard();
            var title = Label(card, "Paused", 80, Cream);
            Anchor(title.rectTransform, 0f, 0.8f, 1f, 0.95f);
            Shadow(title);
            Toggle(card, _art.uiSound, "Sound", GameSettings.SoundOn, on => GameSettings.SoundOn = on, 0.63f, 0.75f);
            Toggle(card, _art.uiHaptics, "Haptics", GameSettings.HapticsOn, on => GameSettings.HapticsOn = on, 0.49f, 0.61f);
            var restart = Pill(card, "−1   Restart", Coral, 50, () => { ClosePopup(); onRestart(); }, _art.uiHeartClay, Cream);
            Anchor(restart, 0.1f, 0.31f, 0.9f, 0.45f);
            var resume = Pill(card, "Resume", Mint, 62, () => { ClosePopup(); onResume(); });
            Anchor(resume, 0.14f, 0.13f, 0.86f, 0.29f);
        }

        /// <summary>"Restart level? Uses 1 life" confirmation.</summary>
        public void ShowRestartConfirm(Action onRestart, Action onCancel)
        {
            var card = OpenCard();
            var title = Label(card, "Restart level?", 72, Cream);
            Anchor(title.rectTransform, 0f, 0.68f, 1f, 0.84f);
            Shadow(title);
            var cost = Rect("Cost", card);
            Anchor(cost, 0.2f, 0.54f, 0.8f, 0.65f);
            var h = Img(cost, _art.uiHeartClay, HeartRed);
            Anchor(h.rectTransform, 0f, 0f, 0.24f, 1f);
            var t = Label(cost, "Uses 1 life", 48, Cream, TextAnchor.MiddleLeft);
            Anchor(t.rectTransform, 0.28f, 0f, 1f, 1f);
            var restart = Pill(card, "Restart", Coral, 62, () => { ClosePopup(); onRestart(); });
            Anchor(restart, 0.14f, 0.32f, 0.86f, 0.49f);
            var cancel = TextLink(card, "Cancel", () => { ClosePopup(); onCancel(); });
            Anchor(cancel, 0.32f, 0.17f, 0.68f, 0.28f);
        }

        /// <summary>Out of lives: big heart, live countdown, refill with ad, refill with cogs (disabled until cogs exist).</summary>
        public void ShowOutOfLives(Action onRefillAd, Action onRefillCogs, Action onBack)
        {
            var card = OpenCard();
            var title = Label(card, "Out of lives", 74, Cream);
            Anchor(title.rectTransform, 0f, 0.82f, 1f, 0.95f);
            Shadow(title);
            var heart = Img(card, _art.uiHeartClay, HeartRed);
            Anchor(heart.rectTransform, 0.36f, 0.62f, 0.64f, 0.82f);
            var timer = Label(card, "", 44, Cream);
            Anchor(timer.rectTransform, 0f, 0.53f, 1f, 0.62f);
            StartCoroutine(Countdown(timer));
            var ad = Pill(card, "Refill with ad", Coral, 46, () => { ClosePopup(); onRefillAd(); }, _art.uiFilm);
            Anchor(ad, 0.08f, 0.39f, 0.92f, 0.52f);
            var cogs = Pill(card, "Refill for 12 cogs", Mint, 44, () => { if (onRefillCogs != null) { ClosePopup(); onRefillCogs(); } },
                _art.uiCogClay, Brass);
            Anchor(cogs, 0.08f, 0.25f, 0.92f, 0.38f);
            if (onRefillCogs == null)
            {
                // cogs arrive with the currency step: show the option, but dimmed and inactive
                var g = cogs.gameObject.AddComponent<CanvasGroup>();
                g.alpha = 0.45f;
                g.interactable = false;
            }
            var back = TextLink(card, "Back", () => { ClosePopup(); onBack(); });
            Anchor(back, 0.34f, 0.14f, 0.66f, 0.24f);
        }

        IEnumerator Countdown(Text label)
        {
            while (label != null)
            {
                int s = Lives.SecondsToNext;
                label.text = s > 0 ? $"Next life in {Lives.FormatTimer(s)}" : "A life is ready!";
                yield return new WaitForSecondsRealtime(0.5f);
            }
        }

        /// <summary>Placeholder for a rewarded video until the ads SDK is wired in.</summary>
        public void ShowAdPlaceholder(float seconds, Action onFinished)
        {
            var card = OpenCard();
            var t = Label(card, "Rewarded ad plays here\n(placeholder)", 46, Cream);
            Anchor(t.rectTransform, 0, 0, 1, 1);
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            StartCoroutine(AfterRealtime(seconds, () => { ClosePopup(); onFinished(); }));
        }

        static void Shadow(Graphic g)
        {
            var s = g.gameObject.AddComponent<UnityEngine.UI.Shadow>();
            s.effectColor = new Color(0f, 0f, 0f, 0.4f);
            s.effectDistance = new Vector2(0f, -5f);
        }

        static IEnumerator AfterRealtime(float seconds, Action a)
        {
            yield return new WaitForSecondsRealtime(seconds);
            a();
        }
    }

    /// <summary>
    /// Keeps a horizontally-sliced pill/slab's rounded ends circular at any height. A sliced Image keeps its
    /// border at the sprite's pixel size; when the rect is shorter than the sprite, the caps squash into ovals.
    /// Scaling the border with the rect height (via pixelsPerUnitMultiplier) keeps them true semicircles.
    /// </summary>
    [RequireComponent(typeof(Image))]
    public sealed class RoundCaps : MonoBehaviour
    {
        Image _img;
        float _lastH = -1f;

        void LateUpdate()
        {
            if (_img == null) _img = GetComponent<Image>();
            var sp = _img.sprite;
            float h = ((RectTransform)transform).rect.height;
            if (sp == null || h <= 1f || Mathf.Approximately(h, _lastH)) return;
            _lastH = h;
            var canvas = _img.canvas;
            float refPpu = canvas != null ? canvas.referencePixelsPerUnit : 100f;
            // on-screen border = borderPx * refPpu / (spritePpu * multiplier); we want it = h * borderPx / spriteHeightPx
            _img.pixelsPerUnitMultiplier = sp.rect.height * refPpu / (sp.pixelsPerUnit * h);
        }
    }

    /// <summary>Clay-like squash when a button is pressed.</summary>
    public sealed class PressSquash : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        float _target = 1f;

        public void OnPointerDown(PointerEventData e) => _target = 0.93f;
        public void OnPointerUp(PointerEventData e) => _target = 1f;
        public void OnPointerExit(PointerEventData e) => _target = 1f;

        void Update()
        {
            float s = Mathf.Lerp(transform.localScale.x, _target, 1f - Mathf.Exp(-25f * Time.unscaledDeltaTime));
            transform.localScale = new Vector3(s, s, 1f);
        }
    }
}
