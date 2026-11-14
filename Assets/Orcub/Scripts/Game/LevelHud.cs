using System;
using System.Collections;
using System.Collections.Generic;
using Orcub.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace Orcub.Game
{
    /// <summary>
    /// In-game UI, layout v2 (approved option C, decluttered):
    ///   top bar     - pause | "Lv N" | big moves number with a small caption | lives heart
    ///   goal pill   - "Free all N gears" + one small gear per ring, in the ring's colour; they empty as rings leave
    ///   board       - fills the space between the goal pill and the tray
    ///   tray        - cream clay shelf with Hint, Undo, Unclamp (count badges)
    ///   cards       - results in the gear wreath, menus on the cream card (see <see cref="WreathView"/>)
    /// </summary>
    public sealed class LevelHud : MonoBehaviour
    {
        public const float TopBandFraction = 0.10f;
        public const float GoalBandFraction = 0.055f;
        public const float TrayFraction = 0.155f;
        const float DevBandFraction = 0.045f;

        /// <summary>Share of the safe area the board must keep clear at the top.</summary>
        public static float TopReserve => TopBandFraction + GoalBandFraction + 0.01f;
        /// <summary>Share of the safe area the board must keep clear at the bottom.</summary>
        public float BottomReserve => TrayFraction + (_devTools ? DevBandFraction : 0f) + 0.015f;

        public event Action Pause, Prev, Next;
        public event Action<BoosterKind> Booster;

        UiKit _kit;
        OrcubArt _art;
        bool _devTools;
        CanvasGroup _group;
        RectTransform _canvasRect, _safe, _goalIcons, _cards;
        Text _level, _moves, _movesLabel, _info, _lives, _goalText;
        readonly List<Image> _goalGears = new List<Image>();
        readonly Dictionary<BoosterKind, (Image button, Text badge)> _boosters = new Dictionary<BoosterKind, (Image, Text)>();
        float _livesRefresh;
        readonly List<RectTransform> _hitAreas = new List<RectTransform>();
        float _targetAlpha = 1f;
        int _movesLeft = 99;
        float _movesBump;
        RectTransform _popup, _card;
        bool _creamCard;
        Color OnCard => _creamCard ? UiKit.Ink : UiKit.Cream;

        public bool PopupOpen => _popup != null;

        public static LevelHud Create(Transform parent, OrcubArt art, bool devTools)
        {
            var go = new GameObject("HUD");
            go.transform.SetParent(parent, false);
            var hud = go.AddComponent<LevelHud>();
            hud._art = art;
            hud._kit = new UiKit(art);
            hud._devTools = devTools;
            hud.BuildUi();
            return hud;
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
            scaler.matchWidthOrHeight = 0f;
            gameObject.AddComponent<GraphicRaycaster>();
            _group = gameObject.AddComponent<CanvasGroup>();
            _canvasRect = (RectTransform)transform;
            _safe = UiKit.Rect("SafeArea", transform);
            gameObject.AddComponent<SafeAreaFit>().Target = _safe;

            // Cards live on their own canvas above every screen (home, map), independent of the HUD's fade.
            var cardsGo = new GameObject("Cards");
            cardsGo.transform.SetParent(transform, false);
            var cardsCanvas = cardsGo.AddComponent<Canvas>();
            cardsCanvas.overrideSorting = true;
            cardsCanvas.sortingOrder = 400;
            cardsGo.AddComponent<GraphicRaycaster>();
            _cards = (RectTransform)cardsGo.transform;
            UiKit.Anchor(_cards, 0, 0, 1, 1);
            cardsGo.AddComponent<CanvasGroup>().ignoreParentGroups = true;

            BuildTopBar();
            BuildGoal();
            BuildTray();
            if (_devTools) BuildDev();
        }

        void BuildTopBar()
        {
            var kit = _kit;
            var band = UiKit.Rect("TopBand", _safe);
            UiKit.Anchor(band, 0f, 1f - TopBandFraction, 1f, 1f);
            var slab = kit.MakeImage(band, _art.uiSlab, UiKit.Slab, sliced: true).rectTransform;
            UiKit.Anchor(slab, 0.04f, 0.1f, 0.96f, 0.95f);

            var pause = kit.RoundButton(slab, "Pause", _art.uiPause, UiKit.Coral, UiKit.Cream, () => Pause?.Invoke());
            UiKit.Anchor(pause, 0.045f, 0.1f, 0.045f, 0.9f);
            pause.pivot = new Vector2(0f, 0.5f);
            pause.gameObject.AddComponent<AspectRatioFitter>().aspectMode = AspectRatioFitter.AspectMode.HeightControlsWidth;
            _hitAreas.Add(pause);

            _level = kit.MakeText(slab, "Lv 1", UiKit.Body, UiKit.Ink);
            UiKit.Anchor(_level.rectTransform, 0.2f, 0f, 0.42f, 1f);
            var divider = kit.MakeImage(slab, null, new Color(UiKit.Ink.r, UiKit.Ink.g, UiKit.Ink.b, 0.2f));
            UiKit.Anchor(divider.rectTransform, 0.435f, 0.26f, 0.44f, 0.74f);

            // moves is the hero: big number, small caption underneath
            _moves = kit.MakeText(slab, "0", 74, UiKit.Ink);
            UiKit.Anchor(_moves.rectTransform, 0.46f, 0.36f, 0.78f, 0.98f);
            _movesLabel = kit.MakeText(slab, "moves", 30, new Color(UiKit.Ink.r, UiKit.Ink.g, UiKit.Ink.b, 0.7f));
            UiKit.Anchor(_movesLabel.rectTransform, 0.46f, 0.12f, 0.78f, 0.4f);

            var heart = UiKit.Rect("Lives", slab);
            UiKit.Anchor(heart, 0.955f, 0.04f, 0.955f, 0.96f);
            heart.pivot = new Vector2(1f, 0.5f);
            var hImg = heart.gameObject.AddComponent<Image>();
            hImg.sprite = _art.uiHeartClay;
            hImg.color = UiKit.HeartRed;
            hImg.preserveAspect = true;
            hImg.raycastTarget = false;
            heart.gameObject.AddComponent<AspectRatioFitter>().aspectMode = AspectRatioFitter.AspectMode.HeightControlsWidth;
            _lives = kit.MakeText(heart, Lives.Count.ToString(), UiKit.Label, UiKit.Cream);
            UiKit.Anchor(_lives.rectTransform, 0f, 0.1f, 1f, 0.9f);
        }

        void BuildGoal()
        {
            var kit = _kit;
            var goal = UiKit.Rect("Goal", _safe);
            UiKit.Anchor(goal, 0.12f, 1f - TopBandFraction - GoalBandFraction, 0.88f, 1f - TopBandFraction);
            var pill = kit.MakeImage(goal, _art.uiSlab, UiKit.Slab, sliced: true).rectTransform;
            UiKit.Anchor(pill, 0f, 0.05f, 1f, 0.95f);
            _goalText = kit.MakeText(pill, "Free all gears", UiKit.Caption, UiKit.Ink, TextAnchor.MiddleLeft);
            UiKit.Anchor(_goalText.rectTransform, 0.08f, 0f, 0.56f, 1f);
            _goalIcons = UiKit.Rect("Gears", pill);
            UiKit.Anchor(_goalIcons, 0.57f, 0.16f, 0.94f, 0.84f);
        }

        void BuildTray()
        {
            var kit = _kit;
            float y0 = (_devTools ? DevBandFraction : 0f) + 0.008f;
            var tray = UiKit.Rect("Tray", _safe);
            UiKit.Anchor(tray, 0.04f, y0, 0.96f, y0 + TrayFraction);
            var shelf = kit.Card(tray);
            UiKit.Anchor(shelf.rectTransform, 0f, 0f, 1f, 1f);
            _hitAreas.Add(tray);   // the whole shelf belongs to the UI, never the board

            (BoosterKind kind, Sprite glyph, string label)[] items =
            {
                (BoosterKind.Hint, _art.iconHint, "Hint"),
                (BoosterKind.Undo, _art.iconUndo, "Undo"),
                (BoosterKind.Unclamp, _art.iconUnclamp, "Unclamp"),
            };
            for (int i = 0; i < items.Length; i++)
            {
                var (kind, glyph, label) = items[i];
                var slot = UiKit.Rect(label, tray);
                UiKit.Anchor(slot, i / 3f, 0f, (i + 1) / 3f, 1f);
                var button = kit.RoundButton(slot, label, glyph, UiKit.Coral, UiKit.Cream, () => Booster?.Invoke(kind));
                UiKit.Anchor(button, 0.5f, 0.3f, 0.5f, 0.92f);
                button.gameObject.AddComponent<AspectRatioFitter>().aspectMode = AspectRatioFitter.AspectMode.HeightControlsWidth;
                var badge = kit.MakeImage(button, _art.uiRound, UiKit.CardCream);
                UiKit.Anchor(badge.rectTransform, 0.66f, 0.62f, 1.06f, 1.02f);
                var count = kit.MakeText(badge.transform, "3", UiKit.Caption, UiKit.Ink);
                UiKit.Anchor(count.rectTransform, 0, 0.05f, 1, 0.95f);
                var caption = kit.MakeText(slot, label, UiKit.Caption, UiKit.Ink);
                UiKit.Anchor(caption.rectTransform, 0f, 0.04f, 1f, 0.28f);
                _boosters[kind] = (button.GetComponent<Image>(), count);
            }
        }

        void BuildDev()
        {
            var kit = _kit;
            var bottom = UiKit.Rect("DevBand", _safe);
            UiKit.Anchor(bottom, 0f, 0f, 1f, DevBandFraction);
            var prev = kit.Pill(bottom, "‹", new Color(1f, 1f, 1f, 0.14f), UiKit.Label, () => Prev?.Invoke());
            UiKit.Anchor(prev, 0.02f, 0.08f, 0.14f, 0.95f);
            var next = kit.Pill(bottom, "›", new Color(1f, 1f, 1f, 0.14f), UiKit.Label, () => Next?.Invoke());
            UiKit.Anchor(next, 0.86f, 0.08f, 0.98f, 0.95f);
            _hitAreas.Add(prev);
            _hitAreas.Add(next);
            _info = kit.MakeText(bottom, "", 22, new Color(1f, 1f, 1f, 0.4f));
            UiKit.Anchor(_info.rectTransform, 0.15f, 0f, 0.85f, 1f);
            _info.horizontalOverflow = HorizontalWrapMode.Wrap;
        }

        // ------------------------------------------------------------------ runtime

        void Update()
        {
            _group.alpha = Mathf.MoveTowards(_group.alpha, _targetAlpha, Time.unscaledDeltaTime * 3f);
            _group.interactable = _group.blocksRaycasts = _targetAlpha > 0.5f;

            _movesBump = Mathf.MoveTowards(_movesBump, 0f, Time.unscaledDeltaTime * 3f);
            bool low = _movesLeft <= 3;
            float breathe = low ? 0.06f * (0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 6f)) : 0f;
            _moves.transform.localScale = Vector3.one * (1f + _movesBump * 0.22f + breathe);
            _moves.color = low ? Color.Lerp(UiKit.Ink, UiKit.Coral, 0.9f) : UiKit.Ink;

            _livesRefresh -= Time.unscaledDeltaTime;
            if (_livesRefresh <= 0f)
            {
                _livesRefresh = 1f;
                _lives.text = Lives.Count.ToString();
            }

            if (_card != null)
            {
                float w = _canvasRect.rect.width * WreathView.WidthOfView * 0.86f;
                _card.sizeDelta = new Vector2(w, w);
            }
        }

        public void SetLevel(int number, string info)
        {
            _level.text = $"Lv {number}";
            if (_info != null) _info.text = info;
        }

        public void SetMoves(int movesLeft, bool bump = false)
        {
            _movesLeft = movesLeft;
            _moves.text = movesLeft.ToString();
            _movesLabel.text = movesLeft == 1 ? "move" : "moves";
            if (bump) _movesBump = 1f;
        }

        /// <summary>One small gear per ring, in the ring's colour.</summary>
        public void SetGoal(IReadOnlyList<Color> ringColours)
        {
            foreach (var g in _goalGears) if (g != null) Destroy(g.gameObject);
            _goalGears.Clear();
            int n = ringColours.Count;
            _goalText.text = $"Free all {n} gears";
            for (int i = 0; i < n; i++)
            {
                var img = _kit.MakeImage(_goalIcons, _art.homeGearRing, ringColours[i]);
                float w = 1f / Mathf.Max(n, 5);
                float x0 = 1f - (n - i) * w;   // right-aligned row
                UiKit.Anchor(img.rectTransform, x0, 0f, x0 + w * 0.9f, 1f);
                _goalGears.Add(img);
            }
        }

        public void MarkGoalFreed(int ringIndex)
        {
            if (ringIndex < 0 || ringIndex >= _goalGears.Count || _goalGears[ringIndex] == null) return;
            StartCoroutine(FadeGear(_goalGears[ringIndex]));
            int left = 0;
            foreach (var g in _goalGears) if (g != null && g.color.a > 0.5f) left++;
            _goalText.text = left <= 1 ? "Last gear!" : $"{left - 1} gears to go";
        }

        static IEnumerator FadeGear(Image img)
        {
            var c = img.color;
            float t = 0f;
            while (t < 1f && img != null)
            {
                t += Time.unscaledDeltaTime / 0.3f;
                img.color = new Color(c.r, c.g, c.b, Mathf.Lerp(1f, 0.22f, t));
                img.transform.localScale = Vector3.one * (1f + 0.3f * Mathf.Sin(Mathf.Clamp01(t) * Mathf.PI));
                yield return null;
            }
        }

        public void SetBoosterCount(BoosterKind kind, int count)
        {
            if (!_boosters.TryGetValue(kind, out var b)) return;
            b.badge.text = count > 0 ? count.ToString() : "+";
            b.button.color = count > 0 ? UiKit.Coral : UiKit.Dim;
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

        // ------------------------------------------------------------------ cards

        RectTransform OpenCard(bool cream = false)
        {
            ClosePopup();
            _creamCard = cream;
            _popup = UiKit.Rect("Popup", _cards);
            UiKit.Anchor(_popup, 0, 0, 1, 1);
            var blocker = _popup.gameObject.AddComponent<Image>();
            blocker.color = new Color(0, 0, 0, 0);
            _card = UiKit.Rect("Card", _popup);
            _card.anchorMin = _card.anchorMax = new Vector2(0.5f, 0.5f);
            _card.pivot = new Vector2(0.5f, 0.5f);
            float w = _canvasRect.rect.width * WreathView.WidthOfView * 0.86f;
            _card.sizeDelta = new Vector2(w, w);
            if (cream)
            {
                var panel = _kit.Card(_card);
                UiKit.Anchor(panel.rectTransform, -0.04f, -0.02f, 1.04f, 1.02f);
                panel.transform.SetAsFirstSibling();
            }
            StartCoroutine(FadeIn(_card.gameObject.AddComponent<CanvasGroup>()));
            return _card;
        }

        static IEnumerator FadeIn(CanvasGroup g)
        {
            g.alpha = 0f;
            yield return new WaitForSecondsRealtime(0.28f);
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

        Text Title(RectTransform card, string text, float y0, float y1, int size = UiKit.Display)
        {
            var t = _kit.MakeText(card, text, size, OnCard);
            UiKit.Anchor(t.rectTransform, 0f, y0, 1f, y1);
            if (!_creamCard) UiKit.Shadow(t);
            return t;
        }

        /// <summary>Two text links side by side (e.g. "Retry · Home").</summary>
        void LinkPair(RectTransform card, string a, Action onA, string b, Action onB, float y0, float y1)
        {
            var left = _kit.Link(card, a, OnCard, () => { ClosePopup(); onA(); });
            UiKit.Anchor(left, 0.16f, y0, 0.48f, y1);
            var right = _kit.Link(card, b, OnCard, () => { ClosePopup(); onB(); });
            UiKit.Anchor(right, 0.52f, y0, 0.84f, y1);
        }

        /// <summary>Level complete: title, three clay stars (pop in one by one), Next, and a Home link.</summary>
        public void ShowWin(int level, int stars, Action onNext, Action onHome)
        {
            var card = OpenCard();
            Title(card, $"Level {level}", 0.77f, 0.93f);
            var full = new List<Image>();
            for (int i = 0; i < 3; i++)
            {
                float x0 = 0.08f + i * 0.285f, lift = i == 1 ? 0.05f : 0f;
                var socket = _kit.MakeImage(card, _art.uiStarClay, UiKit.StarTan);
                UiKit.Anchor(socket.rectTransform, x0, 0.49f + lift, x0 + 0.27f, 0.74f + lift);
                var star = _kit.MakeImage(socket.transform, _art.uiStarClay, UiKit.StarGold);
                UiKit.Anchor(star.rectTransform, 0, 0, 1, 1);
                star.transform.localScale = Vector3.zero;
                full.Add(star);
            }
            var next = _kit.Pill(card, "Next", UiKit.Mint, UiKit.Title, () => { ClosePopup(); onNext(); });
            UiKit.Anchor(next, 0.12f, 0.29f, 0.88f, 0.46f);
            var home = _kit.Link(card, "Home", OnCard, () => { ClosePopup(); onHome(); });
            UiKit.Anchor(home, 0.32f, 0.15f, 0.68f, 0.27f);
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

        /// <summary>Out of moves: one "+N moves" pill (rewarded ad, once), then Retry · Home.</summary>
        public void ShowOutOfMoves(int bonusMoves, bool canWatchAd, Action onWatchAd, Action onRetry, Action onHome)
        {
            var card = OpenCard();
            Title(card, "Out of moves", 0.68f, 0.85f, 76);
            if (canWatchAd)
            {
                var ad = _kit.Pill(card, $"+{bonusMoves} moves", UiKit.Coral, UiKit.Title, () => { ClosePopup(); onWatchAd(); }, _art.uiFilm);
                UiKit.Anchor(ad, 0.06f, 0.4f, 0.94f, 0.6f);
            }
            float y = canWatchAd ? 0.2f : 0.42f;
            LinkPair(card, "Retry", onRetry, "Home", onHome, y, y + 0.12f);
        }

        /// <summary>Pause / settings: sound + haptics, Restart (costs a life), Resume, and a Home link.</summary>
        public void ShowPause(Action onRestart, Action onResume, Action onHome)
        {
            var card = OpenCard(cream: true);
            Title(card, "Paused", 0.82f, 0.96f, 80);
            Toggle(card, _art.uiSound, "Sound", GameSettings.SoundOn, on => GameSettings.SoundOn = on, 0.67f, 0.78f);
            Toggle(card, _art.uiHaptics, "Haptics", GameSettings.HapticsOn, on => GameSettings.HapticsOn = on, 0.54f, 0.65f);
            var restart = _kit.Pill(card, "−1   Restart", UiKit.Coral, UiKit.Body, () => { ClosePopup(); onRestart(); }, _art.uiHeartClay, UiKit.Cream);
            UiKit.Anchor(restart, 0.1f, 0.37f, 0.9f, 0.5f);
            var resume = _kit.Pill(card, "Resume", UiKit.Mint, UiKit.Title, () => { ClosePopup(); onResume(); });
            UiKit.Anchor(resume, 0.14f, 0.19f, 0.86f, 0.35f);
            var home = _kit.Link(card, "Home", OnCard, () => { ClosePopup(); onHome(); });
            UiKit.Anchor(home, 0.32f, 0.05f, 0.68f, 0.17f);
        }

        /// <summary>Settings from the home screen: sound, haptics, Done.</summary>
        public void ShowSettings(Action onClose)
        {
            var card = OpenCard(cream: true);
            Title(card, "Settings", 0.76f, 0.92f, 80);
            Toggle(card, _art.uiSound, "Sound", GameSettings.SoundOn, on => GameSettings.SoundOn = on, 0.56f, 0.68f);
            Toggle(card, _art.uiHaptics, "Haptics", GameSettings.HapticsOn, on => GameSettings.HapticsOn = on, 0.42f, 0.54f);
            var done = _kit.Pill(card, "Done", UiKit.Mint, UiKit.Title, () => { ClosePopup(); onClose(); });
            UiKit.Anchor(done, 0.18f, 0.16f, 0.82f, 0.32f);
        }

        /// <summary>"Restart level? / Leave level? - Uses 1 life" confirmation.</summary>
        public void ShowLifeConfirm(string title, string confirmLabel, Action onConfirm, Action onCancel)
        {
            var card = OpenCard(cream: true);
            Title(card, title, 0.68f, 0.84f, 72);
            var cost = UiKit.Rect("Cost", card);
            UiKit.Anchor(cost, 0.2f, 0.54f, 0.8f, 0.65f);
            var h = _kit.MakeImage(cost, _art.uiHeartClay, UiKit.HeartRed);
            UiKit.Anchor(h.rectTransform, 0f, 0f, 0.24f, 1f);
            var t = _kit.MakeText(cost, "Uses 1 life", UiKit.Body, UiKit.Ink, TextAnchor.MiddleLeft);
            UiKit.Anchor(t.rectTransform, 0.28f, 0f, 1f, 1f);
            var ok = _kit.Pill(card, confirmLabel, UiKit.Coral, UiKit.Title, () => { ClosePopup(); onConfirm(); });
            UiKit.Anchor(ok, 0.14f, 0.32f, 0.86f, 0.49f);
            var cancel = _kit.Link(card, "Cancel", OnCard, () => { ClosePopup(); onCancel(); });
            UiKit.Anchor(cancel, 0.32f, 0.16f, 0.68f, 0.28f);
        }

        /// <summary>Out of lives: big heart, live countdown, refill with ad, refill with cogs (dim until cogs exist).</summary>
        public void ShowOutOfLives(Action onRefillAd, Action onRefillCogs, Action onBack)
        {
            var card = OpenCard(cream: true);
            Title(card, "Out of lives", 0.82f, 0.95f, 74);
            var heart = _kit.MakeImage(card, _art.uiHeartClay, UiKit.HeartRed);
            UiKit.Anchor(heart.rectTransform, 0.36f, 0.62f, 0.64f, 0.82f);
            var timer = _kit.MakeText(card, "", UiKit.Body, UiKit.Ink);
            UiKit.Anchor(timer.rectTransform, 0f, 0.53f, 1f, 0.62f);
            StartCoroutine(Countdown(timer));
            var ad = _kit.Pill(card, "Refill with ad", UiKit.Coral, UiKit.Body, () => { ClosePopup(); onRefillAd(); }, _art.uiFilm);
            UiKit.Anchor(ad, 0.08f, 0.39f, 0.92f, 0.52f);
            var cogs = _kit.Pill(card, "Refill for 12 cogs", UiKit.Mint, UiKit.Label,
                () => { if (onRefillCogs != null) { ClosePopup(); onRefillCogs(); } }, _art.uiCogClay, UiKit.Brass);
            UiKit.Anchor(cogs, 0.08f, 0.25f, 0.92f, 0.38f);
            if (onRefillCogs == null)
            {
                var g = cogs.gameObject.AddComponent<CanvasGroup>();
                g.alpha = 0.45f;
                g.interactable = false;
            }
            var back = _kit.Link(card, "Back", OnCard, () => { ClosePopup(); onBack(); });
            UiKit.Anchor(back, 0.34f, 0.13f, 0.66f, 0.24f);
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

        /// <summary>Clay toggle: icon + label + pill track with a sliding cream knob.</summary>
        void Toggle(Transform parent, Sprite icon, string label, bool on, Action<bool> changed, float y0, float y1)
        {
            var kit = _kit;
            var row = UiKit.Rect(label, parent);
            UiKit.Anchor(row, 0.08f, y0, 0.92f, y1);
            var ic = kit.MakeImage(row, icon, OnCard);
            UiKit.Anchor(ic.rectTransform, 0f, 0.12f, 0.16f, 0.88f);
            var text = kit.MakeText(row, label, UiKit.Body, OnCard, TextAnchor.MiddleLeft);
            UiKit.Anchor(text.rectTransform, 0.2f, 0f, 0.62f, 1f);

            var track = UiKit.Rect("Switch", row);
            UiKit.Anchor(track, 0.66f, 0.08f, 1f, 0.92f);
            var tImg = track.gameObject.AddComponent<Image>();
            tImg.sprite = _art.uiPill;
            tImg.type = Image.Type.Sliced;
            track.gameObject.AddComponent<RoundCaps>();
            var knob = UiKit.Rect("Knob", track);
            var kImg = knob.gameObject.AddComponent<Image>();
            kImg.sprite = _art.uiRound;
            kImg.color = new Color(1f, 0.97f, 0.92f);
            kImg.preserveAspect = true;
            kImg.raycastTarget = false;
            knob.gameObject.AddComponent<AspectRatioFitter>().aspectMode = AspectRatioFitter.AspectMode.HeightControlsWidth;

            bool state = on;
            void Show()
            {
                tImg.color = state ? UiKit.Mint : UiKit.Dim;
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
                Haptics.Play(Haptics.Impact.Light, 0.6f);
            });
        }

        /// <summary>Placeholder for a rewarded video until the ads SDK is wired in.</summary>
        public void ShowAdPlaceholder(float seconds, Action onFinished)
        {
            var card = OpenCard();
            var t = _kit.MakeText(card, "Rewarded ad plays here\n(placeholder)", UiKit.Body, UiKit.Cream);
            UiKit.Anchor(t.rectTransform, 0, 0, 1, 1);
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            StartCoroutine(AfterRealtime(seconds, () => { ClosePopup(); onFinished(); }));
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
