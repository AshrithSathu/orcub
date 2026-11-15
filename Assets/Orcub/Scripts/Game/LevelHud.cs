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
        public const float TrayFraction = 0.135f;
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
        readonly Dictionary<BoosterKind, int> _boosterCounts = new Dictionary<BoosterKind, int>();
        readonly Dictionary<BoosterKind, bool> _boosterReady = new Dictionary<BoosterKind, bool>();
        RectTransform _toast;
        float _livesRefresh;
        readonly List<RectTransform> _hitAreas = new List<RectTransform>();
        float _targetAlpha = 1f;
        int _movesLeft = 99;
        float _movesBump;
        RectTransform _popup, _card;
        bool _creamCard;
        bool _keepBar;              // the "New gear!" sheet leaves the top bar in view (approved intro card B)
        bool _dimBar;               // anchored fail sheets (approved out_of_moves_gold_B) leave the HUD dimmed in view
        Action _onDismiss;          // what closing the open sheet means (intro: "Got it")
        RectTransform _levelRt, _movesRt, _dividerRt;
        Image _boss;
        Color OnCard => _creamCard ? UiKit.Ink : UiKit.Cream;

        public bool PopupOpen => _popup != null;
        /// <summary>The level HUD is on screen (a level is being played), as opposed to a menu.</summary>
        public bool Shown => _targetAlpha > 0.5f;

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

        // All three HUD bands share the same side margins so their edges line up.
        const float SideMargin = 0.04f;

        /// <summary>
        /// The clay slab has a lip at the bottom; content is centred on its face (the part above the lip),
        /// not on the whole sprite, or everything reads as sitting low.
        /// </summary>
        static RectTransform Face(RectTransform slab)
        {
            var face = UiKit.Rect("Face", slab);
            UiKit.Anchor(face, 0.025f, 0.14f, 0.975f, 0.92f);
            return face;
        }

        /// <summary>Square child, centred vertically at x (0..1 of parent), sized as a share of the parent's height.</summary>
        static RectTransform SquareAt(RectTransform parent, string name, float x, float heightShare, Vector2 pivot)
        {
            var rt = UiKit.Rect(name, parent);
            rt.anchorMin = new Vector2(x, 0.5f - heightShare * 0.5f);
            rt.anchorMax = new Vector2(x, 0.5f + heightShare * 0.5f);
            rt.pivot = pivot;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            rt.gameObject.AddComponent<AspectRatioFitter>().aspectMode = AspectRatioFitter.AspectMode.HeightControlsWidth;
            return rt;
        }

        void BuildTopBar()
        {
            var kit = _kit;
            var band = UiKit.Rect("TopBand", _safe);
            UiKit.Anchor(band, 0f, 1f - TopBandFraction, 1f, 1f);
            var slab = kit.MakeImage(band, _art.uiSlab, UiKit.Slab, sliced: true).rectTransform;
            UiKit.Anchor(slab, SideMargin, 0.12f, 1f - SideMargin, 0.94f);
            var face = Face(slab);

            // left: pause
            var pauseSlot = SquareAt(face, "PauseSlot", 0.02f, 0.86f, new Vector2(0f, 0.5f));
            var pause = kit.RoundButton(pauseSlot, "Pause", _art.uiPause, UiKit.Coral, UiKit.Cream, () => Pause?.Invoke());
            UiKit.Anchor(pause, 0, 0, 1, 1);
            _hitAreas.Add(pause);

            // level
            _level = kit.MakeText(face, "Level 1", UiKit.Body, UiKit.Ink);
            _level.resizeTextForBestFit = true;
            _level.resizeTextMinSize = UiKit.Caption;
            _level.resizeTextMaxSize = UiKit.Body;
            _levelRt = _level.rectTransform;
            UiKit.Anchor(_levelRt, 0.16f, 0f, 0.43f, 1f);
            var divider = kit.Divider(face);
            _dividerRt = divider.rectTransform;
            UiKit.Anchor(_dividerRt, 0.445f, 0.2f, 0.45f, 0.8f);

            // boss levels: the approved "Boss" badge sits right after the level number
            if (_art.bossBadge != null)
            {
                _boss = kit.MakeImage(face, _art.BossBadgeTight, Color.white);
                _boss.preserveAspect = true;
                UiKit.Anchor(_boss.rectTransform, 0.37f, 0.02f, 0.575f, 0.98f);
                _boss.gameObject.SetActive(false);
            }

            // moves is the hero: number and caption are one group, centred on the face
            var moves = UiKit.Rect("Moves", face);
            _movesRt = moves;
            UiKit.Anchor(moves, 0.47f, 0f, 0.8f, 1f);
            _moves = kit.MakeText(moves, "0", 76, UiKit.Ink);
            var mrt = _moves.rectTransform;
            mrt.anchorMin = mrt.anchorMax = new Vector2(0.5f, 0.5f);
            mrt.sizeDelta = new Vector2(220f, 84f);
            mrt.anchoredPosition = new Vector2(0f, 14f);
            _movesLabel = kit.MakeText(moves, "moves", UiKit.Caption, new Color(UiKit.Ink.r, UiKit.Ink.g, UiKit.Ink.b, 0.7f));
            var lrt = _movesLabel.rectTransform;
            lrt.anchorMin = lrt.anchorMax = new Vector2(0.5f, 0.5f);
            lrt.sizeDelta = new Vector2(220f, 40f);
            lrt.anchoredPosition = new Vector2(0f, -40f);

            // right: lives (heart sits inside the bar, never past its edge)
            var heart = SquareAt(face, "Lives", 0.98f, 0.9f, new Vector2(1f, 0.5f));
            var hImg = heart.gameObject.AddComponent<Image>();
            hImg.sprite = _art.uiHeartClay;
            hImg.color = UiKit.HeartRed;
            hImg.preserveAspect = true;
            hImg.raycastTarget = false;
            _lives = kit.MakeText(heart, Lives.Count.ToString(), UiKit.Label, UiKit.Cream);
            UiKit.Anchor(_lives.rectTransform, 0f, 0.14f, 1f, 0.94f);
        }

        void BuildGoal()
        {
            var kit = _kit;
            var goal = UiKit.Rect("Goal", _safe);
            UiKit.Anchor(goal, SideMargin + 0.05f, 1f - TopBandFraction - GoalBandFraction, 1f - SideMargin - 0.05f, 1f - TopBandFraction);
            var pill = kit.MakeImage(goal, _art.uiSlab, UiKit.Slab, sliced: true).rectTransform;
            UiKit.Anchor(pill, 0f, 0.04f, 1f, 0.96f);
            var face = Face(pill);
            _goalText = kit.MakeText(face, "Free all gears", UiKit.Caption, UiKit.Ink, TextAnchor.MiddleLeft);
            UiKit.Anchor(_goalText.rectTransform, 0.06f, 0f, 0.56f, 1f);
            _goalIcons = UiKit.Rect("Gears", face);
            UiKit.Anchor(_goalIcons, 0.56f, 0.12f, 0.95f, 0.88f);
        }

        void BuildTray()
        {
            var kit = _kit;
            float y0 = (_devTools ? DevBandFraction : 0f) + 0.012f;
            var tray = UiKit.Rect("Tray", _safe);
            UiKit.Anchor(tray, SideMargin, y0, 1f - SideMargin, y0 + TrayFraction);
            var shelf = kit.Card(tray, 0.7f);
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
                UiKit.Anchor(slot, i / 3f, 0.1f, (i + 1) / 3f, 0.92f);
                // button and its label are one unit: label sits right under the button
                var holder = UiKit.Rect("Button", slot);
                UiKit.Anchor(holder, 0.5f, 0.3f, 0.5f, 1f);
                holder.gameObject.AddComponent<AspectRatioFitter>().aspectMode = AspectRatioFitter.AspectMode.HeightControlsWidth;
                var button = kit.RoundButton(holder, label, glyph, UiKit.Coral, UiKit.Cream, () => Booster?.Invoke(kind));
                UiKit.Anchor(button, 0, 0, 1, 1);
                var badge = kit.MakeImage(button, _art.uiRound, UiKit.CardCream);
                UiKit.Anchor(badge.rectTransform, 0.68f, 0.66f, 1.04f, 1.02f);
                var count = kit.MakeText(badge.transform, "3", UiKit.Caption, UiKit.Ink);
                UiKit.Anchor(count.rectTransform, 0, 0.08f, 1, 0.96f);
                var caption = kit.MakeText(slot, label, UiKit.Caption, UiKit.Ink, TextAnchor.UpperCenter);
                UiKit.Anchor(caption.rectTransform, 0f, 0f, 1f, 0.27f);
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
            float target = _popup != null && !_keepBar ? (_dimBar ? Mathf.Min(_targetAlpha, 0.45f) : 0f) : _targetAlpha;   // a card owns the screen: top bar and tray step back
            _group.alpha = Mathf.MoveTowards(_group.alpha, target, Time.unscaledDeltaTime * 5f);
            _group.interactable = _group.blocksRaycasts = target > 0.5f && _popup == null;

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
            _level.text = $"Level {number}";
            if (_info != null) _info.text = info;
        }

        /// <summary>Boss levels show the badge after the level number; the moves group shifts right to make room.</summary>
        public void SetBoss(bool boss)
        {
            if (_boss == null) return;
            _boss.gameObject.SetActive(boss);
            UiKit.Anchor(_levelRt, boss ? 0.135f : 0.16f, 0f, boss ? 0.37f : 0.43f, 1f);
            UiKit.Anchor(_dividerRt, boss ? 0.59f : 0.445f, 0.2f, boss ? 0.595f : 0.45f, 0.8f);
            UiKit.Anchor(_movesRt, boss ? 0.6f : 0.47f, 0f, boss ? 0.84f : 0.8f, 1f);
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
            _boosterCounts[kind] = count;
            RefreshBooster(kind);
        }

        /// <summary>Greys a booster out while it can't do anything (e.g. Undo before the first move).</summary>
        public void SetBoosterReady(BoosterKind kind, bool ready)
        {
            _boosterReady[kind] = ready;
            RefreshBooster(kind);
        }

        void RefreshBooster(BoosterKind kind)
        {
            if (!_boosters.TryGetValue(kind, out var b)) return;
            int count = _boosterCounts.TryGetValue(kind, out var c) ? c : 0;
            bool ready = !_boosterReady.TryGetValue(kind, out var r) || r;
            b.badge.text = count > 0 ? count.ToString() : "+";
            b.button.color = count > 0 && ready ? UiKit.Coral : UiKit.Dim;
            var glyph = b.button.transform.childCount > 0 ? b.button.transform.GetChild(0).GetComponent<Image>() : null;
            if (glyph != null) glyph.color = new Color(UiKit.Cream.r, UiKit.Cream.g, UiKit.Cream.b, ready ? 1f : 0.55f);
        }

        /// <summary>Short message just above the booster tray; replaces any toast already showing.</summary>
        public void ShowToast(string message)
        {
            if (_toast != null) Destroy(_toast.gameObject);
            float y0 = (_devTools ? DevBandFraction : 0f) + 0.008f + TrayFraction + 0.012f;
            _toast = UiKit.Rect("Toast", _safe);
            UiKit.Anchor(_toast, 0.5f, y0, 0.5f, y0);
            _toast.pivot = new Vector2(0.5f, 0f);
            var label = _kit.MakeText(_toast, message, UiKit.Caption, UiKit.Ink);
            float w = label.preferredWidth + 96f;
            _toast.sizeDelta = new Vector2(w, 96f);
            var pill = _kit.MakeImage(_toast, _art.uiPill, UiKit.CardCream, sliced: true);
            UiKit.Anchor(pill.rectTransform, 0f, 0f, 1f, 1f);
            pill.transform.SetAsFirstSibling();
            UiKit.Anchor(label.rectTransform, 0f, 0.06f, 1f, 0.94f);
            StartCoroutine(ToastLife(_toast, _toast.gameObject.AddComponent<CanvasGroup>()));
        }

        IEnumerator ToastLife(RectTransform rt, CanvasGroup g)
        {
            g.blocksRaycasts = false;
            float t = 0f;
            while (rt != null && t < 1.9f)
            {
                t += Time.unscaledDeltaTime;
                float a = t < 0.16f ? t / 0.16f : t > 1.6f ? 1f - (t - 1.6f) / 0.3f : 1f;
                g.alpha = a;
                float rise = 1f - Mathf.Pow(1f - Mathf.Clamp01(t / 0.2f), 3f);   // ease-out
                rt.anchoredPosition = new Vector2(0f, Mathf.Lerp(-16f, 0f, rise));
                yield return null;
            }
            if (rt != null) Destroy(rt.gameObject);
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
            if (_targetAlpha < 0.5f) return false;
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
            blocker.color = new Color(0.05f, 0.01f, 0.03f, 0f);
            if (cream) StartCoroutine(FadeScrim(blocker, 0.5f));   // wreath cards dim the world themselves
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

        static IEnumerator FadeScrim(Image scrim, float alpha)
        {
            float t = 0f;
            while (t < 1f && scrim != null)
            {
                t += Time.unscaledDeltaTime / 0.2f;
                var c = scrim.color;
                c.a = alpha * UiKit.EaseOut(Mathf.Clamp01(t));
                scrim.color = c;
                yield return null;
            }
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
            _keepBar = false;
            _dimBar = false;
            _onDismiss = null;
        }

        /// <summary>Closes the open sheet the way its own button would (tools / autosolve).</summary>
        public void ClosePopupAndNotify()
        {
            var a = _onDismiss;
            ClosePopup();
            a?.Invoke();
        }

        // ------------------------------------------------------------------ "New gear!" sheet

        /// <summary>
        /// Approved mech_intro_card_B: an anchored cream sheet over the dimmed board (top bar stays) with
        /// "New gear!", the piece illustration and an animated hint arrow, one line of rule, an optional number
        /// pill, and a single "Got it".
        /// </summary>
        public void ShowMechanicIntro(Sprite icon, MechanicIntro.Info info, Action onClose)
        {
            ClosePopup();
            _creamCard = true;
            _keepBar = true;
            _onDismiss = onClose;
            _popup = UiKit.Rect("Popup", _cards);
            UiKit.Anchor(_popup, 0, 0, 1, 1);
            var scrim = _popup.gameObject.AddComponent<Image>();
            scrim.color = new Color(0.05f, 0.01f, 0.03f, 0f);
            StartCoroutine(FadeScrim(scrim, 0.45f));
            Haptics.Play(Haptics.Impact.Light, 0.6f);

            var sheet = UiKit.Rect("IntroSheet", _popup);
            bool pill = !string.IsNullOrEmpty(info.Pill);
            UiKit.Anchor(sheet, 0.03f, 0f, 0.97f, pill ? 0.53f : 0.49f);
            var bg = _kit.MakeImage(sheet, _art.sheet != null ? _art.sheet : _art.uiCard, _art.sheet != null ? Color.white : UiKit.CardCream);
            bg.type = Image.Type.Sliced;
            bg.raycastTarget = true;
            UiKit.Anchor(bg.rectTransform, 0, 0, 1, 1);
            bg.rectTransform.offsetMin = new Vector2(0f, -60f);
            var body = UiKit.Rect("Body", sheet);
            UiKit.Anchor(body, 0.06f, 0.07f, 0.94f, 0.96f);
            var ink = UiKit.Ink;

            var title = _kit.MakeText(body, "New gear!", 96, ink);
            UiKit.Anchor(title.rectTransform, 0f, 0.85f, 1f, 1f);

            var art = UiKit.Rect("Art", body);
            UiKit.Anchor(art, 0.5f, pill ? 0.47f : 0.42f, 0.5f, 0.85f);
            art.pivot = new Vector2(0.5f, 0.5f);
            art.gameObject.AddComponent<AspectRatioFitter>().aspectMode = AspectRatioFitter.AspectMode.HeightControlsWidth;
            if (icon != null)
            {
                var pic = _kit.MakeImage(art, icon, Color.white);
                pic.preserveAspect = true;
                UiKit.Anchor(pic.rectTransform, 0, 0, 1, 1);
            }
            // animated hint arrow(s): drawn on, held, faded, repeated
            var arrows = new List<Image>();
            if (info.Opposed)
            {
                arrows.Add(HintArrow(art, -0.62f, 0.62f, -1f));
                arrows.Add(HintArrow(art, 0.62f, 0.62f, 1f));
            }
            else arrows.Add(HintArrow(art, 0f, 0.62f, info.Direction >= 0f ? 1f : -1f, 1.25f));
            StartCoroutine(AnimateArrows(arrows));

            var rule = _kit.MakeText(body, info.Rule, UiKit.Body, ink);
            rule.horizontalOverflow = HorizontalWrapMode.Wrap;
            UiKit.Anchor(rule.rectTransform, 0f, pill ? 0.33f : 0.27f, 1f, pill ? 0.47f : 0.42f);

            if (pill)
            {
                var badge = _kit.Pill(body, info.Pill, UiKit.Coral, UiKit.Title, () => { });
                badge.GetComponentInChildren<Text>().color = UiKit.Cream;
                UiKit.Anchor(badge, 0.27f, 0.2f, 0.73f, 0.315f);
                badge.GetComponent<Button>().interactable = false;
            }
            var got = _kit.Pill(body, "Got it", UiKit.Mint, 84, () => ClosePopupAndNotify());
            UiKit.Anchor(got, 0.04f, 0.01f, 0.96f, pill ? 0.18f : 0.21f);

            var slide = sheet.gameObject.AddComponent<CanvasGroup>();
            StartCoroutine(SlideUp(sheet, slide));
        }

        Image HintArrow(RectTransform parent, float x, float y, float direction, float size = 0.75f)
        {
            var rt = UiKit.Rect("HintArrow", parent);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f + x * 0.5f, 0.5f + y * 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(1f, 1f);
            var img = _kit.MakeImage(rt, MechSprites.ArcArrow, UiKit.Coral);
            img.type = Image.Type.Filled;
            img.fillMethod = Image.FillMethod.Horizontal;
            img.fillOrigin = (int)Image.OriginHorizontal.Right;   // drawn tail-first toward the head
            var irt = img.rectTransform;
            irt.anchorMin = irt.anchorMax = new Vector2(0.5f, 0.5f);
            float w = ((RectTransform)parent).rect.height * size * 0.6f;
            irt.sizeDelta = new Vector2(Mathf.Max(w, 160f * size), Mathf.Max(w, 160f * size));
            irt.localScale = new Vector3(direction >= 0f ? 1f : -1f, 1f, 1f);   // the sprite is counter-clockwise
            return img;
        }

        static IEnumerator AnimateArrows(List<Image> arrows)
        {
            while (arrows.Count > 0 && arrows[0] != null)
            {
                float t = 0f;
                while (t < 1.7f && arrows[0] != null)
                {
                    t += Time.unscaledDeltaTime;
                    float fill = UiKit.EaseOut(Mathf.Clamp01(t / 0.7f));
                    float a = t < 1.3f ? 1f : 1f - (t - 1.3f) / 0.4f;
                    foreach (var img in arrows)
                    {
                        if (img == null) continue;
                        img.fillAmount = fill;
                        img.color = new Color(UiKit.Coral.r, UiKit.Coral.g, UiKit.Coral.b, a);
                    }
                    yield return null;
                }
            }
        }

        static IEnumerator SlideUp(RectTransform sheet, CanvasGroup g)
        {
            float h = sheet.rect.height + 80f, t = 0f;
            while (t < 1f && sheet != null)
            {
                t += Time.unscaledDeltaTime / 0.28f;
                float k = UiKit.EaseOut(Mathf.Clamp01(t));
                sheet.anchoredPosition = new Vector2(0f, -h * (1f - k));
                g.alpha = Mathf.Clamp01(t * 3f);
                yield return null;
            }
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

        /// <summary>Legacy entry (tools): video-only offer.</summary>
        public void ShowOutOfMoves(int bonusMoves, bool canWatchAd, Action onWatchAd, Action onRetry, Action onHome,
            string title = "Out of moves", Action onUndo = null)
        {
            var offer = new ContinueOffer { Moves = bonusMoves, CanWatch = canWatchAd, OnWatch = onWatchAd, Title = title };
            if (onUndo != null) offer.Extra.Add(("Undo", _art.iconUndo, onUndo));
            ShowOutOfMoves(offer, onRetry, onHome);
        }

        /// <summary>What the out-of-moves sheet offers. Gold and video rows are hidden when unavailable.</summary>
        public sealed class ContinueOffer
        {
            public string Title = "Out of moves";
            public int Moves = 5;
            public int GoldPrice;
            /// <summary>Gold continue (null hides the row). Receives the idempotency key of this intent.</summary>
            public Func<string, System.Threading.Tasks.Task<SpendResult>> SpendGold;
            /// <summary>After a confirmed gold continue: the moves the server granted (0 on a replayed key).</summary>
            public Action<int> OnGoldContinued;
            public bool CanWatch;
            public Action OnWatch;
            /// <summary>Dead-end rescues (Undo, Unclamp) shown instead of the gold / video rows.</summary>
            public readonly List<(string label, Sprite glyph, Action action)> Extra = new List<(string, Sprite, Action)>();
            /// <summary>Shown in place of "Add N moves" (dead ends: why the board is stuck).</summary>
            public string Subtitle;
        }

        /// <summary>
        /// Out of moves (approved out_of_moves_gold_B): an anchored cream sheet that leaves the board in view. Header
        /// (broken-lamp art, title, close X = Home), "Add 5 moves" with the gold balance, ONE mint
        /// "+5 moves for 60 gold" (price escalates x1.5 per gold continue; the tap is the confirmation of the shown
        /// price; pending + inline retry; not enough gold opens the shop), a coral "Watch video for +5" (once per
        /// attempt), then Retry · Home. Dead ends (rust / spring) swap the continue rows for Undo / Unclamp.
        /// </summary>
        public void ShowOutOfMoves(ContinueOffer offer, Action onRetry, Action onHome)
        {
            ClosePopup();
            _creamCard = true;
            _dimBar = true;
            _onDismiss = onHome;
            var popup = _popup = UiKit.Rect("Popup", _cards);
            UiKit.Anchor(_popup, 0, 0, 1, 1);
            var scrim = _popup.gameObject.AddComponent<Image>();
            scrim.color = new Color(0.05f, 0.01f, 0.03f, 0f);
            StartCoroutine(FadeScrim(scrim, 0.45f));

            var kit = _kit;
            var ink = UiKit.Ink;
            const float side = 52f;
            var sheet = UiKit.Rect("MovesSheet", _popup);
            sheet.anchorMin = new Vector2(0.03f, 0f);
            sheet.anchorMax = new Vector2(0.97f, 0f);
            sheet.pivot = new Vector2(0.5f, 0f);
            var bg = kit.MakeImage(sheet, _art.sheet != null ? _art.sheet : _art.uiCard, _art.sheet != null ? Color.white : UiKit.CardCream);
            bg.type = Image.Type.Sliced;
            bg.raycastTarget = true;
            UiKit.Anchor(bg.rectTransform, 0, 0, 1, 1);
            bg.rectTransform.offsetMin = new Vector2(0f, -60f);

            // the sheet's content is laid out top-down above the home indicator (safe-area bottom)
            float safeBottom = _safe.anchorMin.y * _canvasRect.rect.height;
            float y = 44f;
            bool pending = false;
            var buttons = new List<RectTransform>();
            void Lock(bool on) { foreach (var b in buttons) ShopUi.SetInteractable(b, !on); }
            void Close(Action then) { if (_popup != popup || pending) return; ClosePopup(); then?.Invoke(); }

            // header: broken lamp (sits proud of the sheet's top edge), title, close
            int lampIdx = Array.IndexOf(_art.workshopIds ?? new string[0], "lamp");
            Sprite lamp = lampIdx >= 0 && lampIdx < _art.workshopBroken.Length ? _art.workshopBroken[lampIdx] : _art.chapterLamp;
            if (lamp != null)
            {
                var l = kit.MakeImage(sheet, lamp, Color.white);
                var lrt = l.rectTransform;
                lrt.anchorMin = lrt.anchorMax = new Vector2(0f, 1f);
                lrt.pivot = new Vector2(0f, 1f);
                lrt.sizeDelta = new Vector2(176f, 176f);
                lrt.anchoredPosition = new Vector2(34f, 50f);
            }
            var head = AccountUi.Row(sheet, "Head", y, 116f, lamp != null ? 196f : side, 150f);
            var title = kit.MakeText(head, offer.Title, 90, ink, TextAnchor.MiddleLeft);
            UiKit.Anchor(title.rectTransform, 0, 0, 1, 1);
            AccountUi.Fit(title, 640f, 90, UiKit.Title);
            var close = ShopUi.Close(kit, sheet, 40f, 40f, () => Close(onHome));
            buttons.Add(close);
            y += 116f + 22f;

            bool continues = offer.Extra.Count == 0 && (offer.SpendGold != null || offer.CanWatch);
            Text st = null;   // the subtitle doubles as the gold row's error line (no layout shift, no gap)
            if (continues || offer.Subtitle != null)
            {
                var sub = AccountUi.Row(sheet, "Sub", y, 64f, side + 6f, side);
                st = kit.MakeText(sub, offer.Subtitle ?? $"Add {offer.Moves} moves", UiKit.Body + 4, ink, TextAnchor.MiddleLeft);
                UiKit.Anchor(st.rectTransform, 0, 0, 1, 1);
                if (offer.SpendGold != null)
                {   // the balance the price is paid from
                    var bal = UiKit.Rect("Balance", sub);
                    bal.anchorMin = new Vector2(0.55f, 0f);
                    bal.anchorMax = new Vector2(1f, 1f);
                    bal.offsetMin = bal.offsetMax = Vector2.zero;
                    var bt = kit.MakeText(bal, ShopCatalog.Gold(ShopGateway.Current?.Gold ?? 0), UiKit.Body, ink, TextAnchor.MiddleRight);
                    UiKit.Anchor(bt.rectTransform, 0, 0, 1, 1);
                    var gi = kit.MakeImage(bal, ShopUi.GoldIcon(_art), Color.white);
                    var grt = gi.rectTransform;
                    grt.anchorMin = grt.anchorMax = new Vector2(1f, 0.5f);
                    grt.pivot = new Vector2(1f, 0.5f);
                    grt.sizeDelta = new Vector2(76f, 46f);
                    grt.anchoredPosition = new Vector2(-(bt.preferredWidth + 14f), 0f);
                    StartCoroutine(TrackBalance(bt, grt));
                }
                y += 64f + 18f;
            }

            Text helper = null;
            if (offer.Extra.Count > 0)
            {
                foreach (var (label, glyph, action) in offer.Extra)
                {
                    var row = AccountUi.Row(sheet, label, y, 150f, side, side);
                    var pill = kit.Pill(row, label, UiKit.Mint, UiKit.Title, () => Close(action), glyph, ink);
                    UiKit.Anchor(pill, 0, 0, 1, 1);
                    buttons.Add(pill);
                    y += 150f + 22f;
                }
            }
            else
            {
                if (offer.SpendGold != null)
                {
                    var row = AccountUi.Row(sheet, "Gold", y, 150f, side, side);
                    string idle = $"+{offer.Moves} moves for {offer.GoldPrice} gold";
                    var gold = kit.Pill(row, idle, UiKit.Mint, UiKit.Title, () => { }, ShopUi.GoldIcon(_art), Color.white);
                    UiKit.Anchor(gold, 0, 0, 1, 1);
                    buttons.Add(gold);
                    var label = gold.GetComponentInChildren<Text>();
                    y += 150f + 26f;
                    helper = st;
                    string subtitle = st != null ? st.text : "";
                    var spend = new GoldSpend();
                    bool applied = false;
                    var b = gold.GetComponent<Button>();
                    b.onClick.RemoveAllListeners();
                    b.onClick.AddListener(async () =>
                    {
                        if (pending || _popup != popup || applied) return;
                        Haptics.Play(Haptics.Impact.Light, 0.6f);
                        pending = true;
                        Lock(true);
                        label.text = "Adding moves…";
                        if (helper != null) { helper.text = subtitle; helper.color = ink; helper.fontSize = UiKit.Body + 4; }
                        var r = await spend.Run(offer.SpendGold, () => ShopFlow.OpenShop(), ShopGateway.Current?.Gold ?? 0, offer.GoldPrice);
                        pending = false;
                        if (this == null || _popup != popup) return;   // the sheet was closed meanwhile
                        Lock(false);
                        label.text = idle;
                        if (r.Ok)
                        {
                            applied = true;
                            Haptics.Notify(Haptics.Notice.Success);
                            ClosePopup();
                            offer.OnGoldContinued?.Invoke(r.ExtraMoves);
                            return;
                        }
                        if (spend.Error != null && helper != null)
                        {
                            helper.text = spend.Error;
                            helper.color = AccountUi.DeleteRed;
                            AccountUi.Fit(helper, 520f, UiKit.Body + 4, UiKit.Caption);
                            Haptics.Notify(Haptics.Notice.Warning);
                        }
                    });
                }
                if (offer.CanWatch)
                {
                    var row = AccountUi.Row(sheet, "Video", y, 134f, side + 6f, side + 6f);
                    var video = kit.Pill(row, $"Watch video for +{offer.Moves}", UiKit.Coral, UiKit.Title - 4, () => Close(offer.OnWatch), _art.uiFilm, UiKit.Cream);
                    UiKit.Anchor(video, 0, 0, 1, 1);
                    buttons.Add(video);
                    y += 134f + 20f;
                }
            }

            y += 18f;
            var pair = AccountUi.Row(sheet, "RetryHome", y, 132f, side, side);
            var retry = kit.Pill(pair, "Retry", UiKit.CardCream, UiKit.Title, () => Close(onRetry));
            UiKit.Anchor(retry, 0f, 0f, 0.48f, 1f);
            var home = kit.Pill(pair, "Home", UiKit.CardCream, UiKit.Title, () => Close(onHome));
            UiKit.Anchor(home, 0.52f, 0f, 1f, 1f);
            buttons.Add(retry);
            buttons.Add(home);
            y += 132f + 40f;

            sheet.sizeDelta = new Vector2(0f, y + safeBottom);
            var slide = sheet.gameObject.AddComponent<CanvasGroup>();
            StartCoroutine(SlideUp(sheet, slide));
        }

        IEnumerator TrackBalance(Text label, RectTransform icon)
        {
            while (label != null)
            {
                string v = ShopCatalog.Gold(ShopGateway.Current?.Gold ?? 0);
                if (label.text != v)
                {
                    label.text = v;
                    icon.anchoredPosition = new Vector2(-(label.preferredWidth + 14f), 0f);
                }
                yield return new WaitForSecondsRealtime(0.3f);
            }
        }

        /// <summary>Pause / settings: sound + haptics, Restart (costs a life), Resume, and a Home link.</summary>
        public void ShowPause(Action onRestart, Action onResume, Action onHome, Action onInfo = null)
        {
            var card = OpenCard(cream: true);
            Title(card, "Paused", 0.8f, 0.93f, 80);
            if (onInfo != null)
            {   // re-open this board's "New gear!" card
                var info = UiKit.Rect("Info", card);
                UiKit.Anchor(info, 0.85f, 0.81f, 0.98f, 0.94f);
                info.gameObject.AddComponent<AspectRatioFitter>().aspectMode = AspectRatioFitter.AspectMode.HeightControlsWidth;
                var disc = _kit.MakeImage(info, _art.uiRound, UiKit.Coral);
                disc.preserveAspect = true;
                disc.raycastTarget = true;
                UiKit.Anchor(disc.rectTransform, 0, 0, 1, 1);
                var i = _kit.MakeText(info, "i", UiKit.Title, UiKit.Cream);
                UiKit.Anchor(i.rectTransform, 0, 0.06f, 1, 1);
                UiKit.Tap(info.gameObject, () => { ClosePopup(); onInfo(); });
            }
            Toggle(card, _art.uiSound, "Sound", GameSettings.SoundOn, on => GameSettings.SoundOn = on, 0.67f, 0.78f);
            Toggle(card, _art.uiHaptics, "Haptics", GameSettings.HapticsOn, on => GameSettings.HapticsOn = on, 0.54f, 0.65f);
            var resume = _kit.Pill(card, "Resume", UiKit.Mint, UiKit.Title, () => { ClosePopup(); onResume(); });
            UiKit.Anchor(resume, 0.12f, 0.33f, 0.88f, 0.49f);
            var restart = _kit.Pill(card, "Restart  −1", UiKit.Coral, UiKit.Body, () => { ClosePopup(); onRestart(); }, _art.uiHeartClay, UiKit.Cream);
            UiKit.Anchor(restart, 0.2f, 0.19f, 0.8f, 0.3f);
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
            bool privacyRequired = AdsGateway.Service?.PrivacyOptionsRequired == true;
            var done = _kit.Pill(card, privacyRequired ? "Privacy choices" : "Done", UiKit.Mint, UiKit.Title, () =>
            {
                ClosePopup();
                if (privacyRequired) AdsGateway.Service?.ShowPrivacyOptions();
                onClose();
            });
            UiKit.Anchor(done, 0.18f, 0.16f, 0.82f, 0.32f);
        }

        /// <summary>"Restart level? / Leave level? - Uses 1 life" confirmation.</summary>
        public void ShowLifeConfirm(string title, string confirmLabel, Action onConfirm, Action onCancel)
        {
            var card = OpenCard(cream: true);
            Title(card, title, 0.68f, 0.84f, 72);
            var cost = UiKit.Rect("Cost", card);
            UiKit.Anchor(cost, 0f, 0.54f, 1f, 0.65f);
            var t = _kit.MakeText(cost, "Uses 1 life", UiKit.Body, UiKit.Ink, TextAnchor.MiddleLeft);
            float tw = t.preferredWidth, hw = 64f, gap = 16f, x0 = -(hw + gap + tw) * 0.5f;
            var h = _kit.MakeImage(cost, _art.uiHeartClay, UiKit.HeartRed);
            var hrt = h.rectTransform;
            hrt.anchorMin = hrt.anchorMax = new Vector2(0.5f, 0.5f);
            hrt.sizeDelta = new Vector2(hw, hw);
            hrt.anchoredPosition = new Vector2(x0 + hw * 0.5f, 0f);
            var trt = t.rectTransform;
            trt.anchorMin = trt.anchorMax = new Vector2(0.5f, 0.5f);
            trt.sizeDelta = new Vector2(tw + 4f, 80f);
            trt.anchoredPosition = new Vector2(x0 + hw + gap + tw * 0.5f, 0f);
            var ok = _kit.Pill(card, confirmLabel, UiKit.Coral, UiKit.Title, () => { ClosePopup(); onConfirm(); });
            UiKit.Anchor(ok, 0.14f, 0.32f, 0.86f, 0.49f);
            var cancel = _kit.Link(card, "Cancel", OnCard, () => { ClosePopup(); onCancel(); });
            UiKit.Anchor(cancel, 0.32f, 0.16f, 0.68f, 0.28f);
        }

        /// <summary>
        /// Out of lives (approved out_of_lives_gold_B): an anchored cream sheet with a close X; a status strip
        /// (heart, "0 / 5 lives", the regeneration timer); ONE mint "Refill for 90 gold" (tapping it is the explicit
        /// confirmation of the shown price; pending while the wallet answers, inline retry message on failure);
        /// a quiet cream "Watch video for 1 life" row; and a "Wait" link.
        /// </summary>
        public void ShowOutOfLives(Action onRefillAd, Func<System.Threading.Tasks.Task<bool>> refillGold, Action onGoldRefilled, Action onBack)
        {
            ClosePopup();
            _creamCard = true;
            _onDismiss = onBack;
            var popup = _popup = UiKit.Rect("Popup", _cards);
            UiKit.Anchor(_popup, 0, 0, 1, 1);
            var scrim = _popup.gameObject.AddComponent<Image>();
            scrim.color = new Color(0.05f, 0.01f, 0.03f, 0f);
            StartCoroutine(FadeScrim(scrim, 0.5f));
            // the dimmed screen stays inactive: the scrim image blocks taps (raycast target)

            var sheet = UiKit.Rect("LivesSheet", _popup);
            UiKit.Anchor(sheet, 0.03f, 0f, 0.97f, 0.41f);
            var bg = _kit.MakeImage(sheet, _art.sheet != null ? _art.sheet : _art.uiCard, _art.sheet != null ? Color.white : UiKit.CardCream);
            bg.type = Image.Type.Sliced;
            bg.raycastTarget = true;
            UiKit.Anchor(bg.rectTransform, 0, 0, 1, 1);
            bg.rectTransform.offsetMin = new Vector2(0f, -60f);
            var body = UiKit.Rect("Body", sheet);
            UiKit.Anchor(body, 0.05f, 0.06f, 0.95f, 0.96f);
            var ink = UiKit.Ink;
            bool busy = false;   // a gold refill is pending: every other control is inert

            // header: lamp art, title, close X
            if (_art.chapterLamp != null)
            {
                var lamp = _kit.MakeImage(body, _art.chapterLamp, Color.white);
                lamp.preserveAspect = true;
                UiKit.Anchor(lamp.rectTransform, 0f, 0.8f, 0.2f, 1.02f);
            }
            var title = _kit.MakeText(body, "Out of lives", UiKit.Display, ink);
            UiKit.Anchor(title.rectTransform, 0.2f, 0.82f, 0.84f, 1f);
            var close = _kit.RoundButton(body, "Close", _art.glyphClose != null ? _art.glyphClose : _art.uiRound, UiKit.CardCream, UiKit.Coral,
                () => { if (_popup == popup && !busy) { ClosePopup(); onBack?.Invoke(); } });
            UiKit.Anchor(close, 0.86f, 0.84f, 1f, 1f);
            close.gameObject.AddComponent<AspectRatioFitter>().aspectMode = AspectRatioFitter.AspectMode.HeightControlsWidth;

            // status strip: heart, "0 / 5 lives" | "Next life in mm:ss"
            var strip = UiKit.Rect("Status", body);
            UiKit.Anchor(strip, 0f, 0.64f, 1f, 0.79f);
            var slab = _kit.MakeImage(strip, _art.uiSlab != null ? _art.uiSlab : _art.uiPill, UiKit.Slab, sliced: true);
            UiKit.Anchor(slab.rectTransform, 0, 0, 1, 1);
            var heart = _kit.MakeImage(strip, _art.uiHeartClay, UiKit.HeartRed);
            heart.preserveAspect = true;
            UiKit.Anchor(heart.rectTransform, 0.04f, 0.14f, 0.15f, 0.86f);
            var count = _kit.MakeText(strip, $"{Mathf.Max(0, Lives.Count)} / {Lives.Max} lives", UiKit.Label, ink);
            UiKit.Anchor(count.rectTransform, 0.15f, 0f, 0.5f, 1f);
            var divider = _kit.Divider(strip);
            UiKit.Anchor(divider.rectTransform, 0.515f, 0.22f, 0.52f, 0.78f);
            var timer = _kit.MakeText(strip, "", UiKit.Label, ink);
            UiKit.Anchor(timer.rectTransform, 0.53f, 0f, 0.98f, 1f);
            StartCoroutine(Countdown(timer));

            // primary: refill with gold (explicit tap = confirmation of the displayed price)
            const string goldLabel = "Refill for 90 gold";
            var gold = _kit.Pill(body, goldLabel, UiKit.Mint, UiKit.Title, () => { }, _art.GoldBarTight != null ? _art.GoldBarTight : _art.goldBar, Color.white);
            UiKit.Anchor(gold, 0f, 0.42f, 1f, 0.6f);
            var helper = _kit.MakeText(body, "Refills all 5 lives", UiKit.Label, ink);
            UiKit.Anchor(helper.rectTransform, 0f, 0.33f, 1f, 0.42f);
            var goldText = gold.GetComponentInChildren<Text>();
            var goldButton = gold.GetComponent<Button>();
            goldButton.onClick.RemoveAllListeners();
            var lockable = new RectTransform[3];   // close, video, wait: inert while the refill is pending
            goldButton.onClick.AddListener(async () =>
            {
                if (busy || refillGold == null) return;
                Haptics.Play(Haptics.Impact.Light, 0.6f);
                var port = ShopGateway.Current;
                if (port != null && port.Gold < ShopCatalog.RefillGold) { ShopFlow.OpenShop(); return; }   // not enough gold: the shop
                busy = true;
                ShopUi.SetInteractable(lockable[0], false); ShopUi.SetInteractable(lockable[1], false); ShopUi.SetInteractable(lockable[2], false);
                goldText.text = "Refilling…";
                helper.text = "Refills all 5 lives";
                bool ok;
                try { ok = await refillGold(); }
                catch (Exception) { ok = false; }
                if (this == null || _popup != popup) return;   // the sheet was closed meanwhile
                busy = false;
                foreach (var l in lockable) ShopUi.SetInteractable(l, true);
                if (ok)
                {
                    Haptics.Notify(Haptics.Notice.Success);
                    ClosePopup();
                    onGoldRefilled?.Invoke();
                    return;
                }
                goldText.text = goldLabel;
                helper.text = "Couldn't refill. Check your gold and try again";
                Haptics.Notify(Haptics.Notice.Warning);
            });

            // secondary: rewarded video, quiet cream row
            var video = _kit.Pill(body, "Watch video for 1 life", UiKit.CardCream, UiKit.Label,
                () => { if (_popup == popup && !busy) { ClosePopup(); onRefillAd?.Invoke(); } }, _art.uiFilm, ink);
            UiKit.Anchor(video, 0.02f, 0.14f, 0.98f, 0.3f);
            var wait = _kit.Link(body, "Wait", OnCard, () => { if (_popup == popup && !busy) { ClosePopup(); onBack?.Invoke(); } });
            UiKit.Anchor(wait, 0.35f, 0f, 0.65f, 0.12f);
            lockable[0] = close; lockable[1] = video; lockable[2] = wait;

            var slide = sheet.gameObject.AddComponent<CanvasGroup>();
            StartCoroutine(SlideUp(sheet, slide));
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
