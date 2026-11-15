using System;
using System.Collections;
using System.Collections.Generic;
using Orcub.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Orcub.Game
{
    /// <summary>
    /// Owner of the daily-loop UI (approved features_v1): the Daily Puzzle screen (daily_puzzle_B), the daily gift
    /// sheet (daily_gift_B), the win-streak pieces (win_streak_B) and the notification opt-in (notifications_B).
    /// Every date, reward and permission goes through the daily port; nothing is granted or asked for here.
    /// </summary>
    public sealed class DailyFlow : MonoBehaviour
    {
        public const int ScreenOrder = 260, SheetOrder = 560;
        public static DailyFlow Instance { get; private set; }

        UiKit _kit;
        Action _play, _back;
        DailyScreen _screen;
        AccountOverlay _gift, _optIn, _fresh;
        string _giftOfferedDay;

        public bool ScreenOpen => _screen != null;
        public bool SheetOpen => _gift != null || _optIn != null || _fresh != null;

        public static DailyFlow Create(Transform parent, UiKit kit, Action play, Action back)
        {
            var go = new GameObject("DailyFlow");
            go.transform.SetParent(parent, false);
            var f = go.AddComponent<DailyFlow>();
            f._kit = kit;
            f._play = play;
            f._back = back;
            Instance = f;
            TopBar.DailyBadge = () => DailyGateway.Current?.CanPlay == true;
            return f;
        }

        void OnDestroy() { if (Instance == this) { Instance = null; TopBar.DailyBadge = null; } }

        // ------------------------------------------------------------------ daily puzzle screen

        public void OpenScreen()
        {
            if (_screen != null) { _screen.gameObject.SetActive(true); _screen.Refresh(); return; }
            _screen = DailyScreen.Create(transform, _kit, () => _play?.Invoke(), () => { CloseScreen(); _back?.Invoke(); });
        }

        public void CloseScreen()
        {
            if (_screen != null) Destroy(_screen.gameObject);
            _screen = null;
        }

        public void HideScreen() { if (_screen != null) _screen.gameObject.SetActive(false); }
        public void ShowScreenAgain() { if (_screen != null) { _screen.gameObject.SetActive(true); _screen.Refresh(); } else OpenScreen(); }

        // ------------------------------------------------------------------ daily gift (daily_gift_B)

        /// <summary>Home: the gift sheet once per UTC day per session while it can be claimed.</summary>
        public bool TryOfferGift()
        {
            var port = DailyGateway.Current;
            if (port == null || !port.CanClaimGift || SheetOpen || _giftOfferedDay == port.TodayUtc) return false;
            _giftOfferedDay = port.TodayUtc;
            ShowGift();
            return true;
        }

        public void ShowGift()
        {
            if (_gift != null) return;
            _gift = DailySheets.Gift(transform, _kit, () => { var g = _gift; _gift = null; g?.Close(); });
        }

        // ------------------------------------------------------------------ notification opt-in (notifications_B)

        /// <summary>
        /// After a meaningful moment (a daily completion, or home after a win from the third session on), never at
        /// launch. Records the weekly cooldown before showing; only Continue leads to the iOS permission.
        /// </summary>
        public bool TryOfferOptIn()
        {
            var port = DailyGateway.Current;
            if (port == null || SheetOpen || !port.ShouldOfferOptIn) return false;
            if (!port.OptInShown()) return false;
            ShowOptIn(port);
            return true;
        }

        public void ShowOptIn(IDailyPort port)
        {
            if (_optIn != null) return;
            _optIn = DailySheets.OptIn(transform, _kit,
                onContinue: async () =>
                {
                    var o = _optIn; _optIn = null; o?.Close();
                    try { if (port != null) await port.ContinueOptIn(); }
                    catch (Exception e) { Debug.LogWarning("Notification opt-in failed: " + e.Message); }
                },
                onLater: () => { var o = _optIn; _optIn = null; o?.Close(); });
        }

        // ------------------------------------------------------------------ win streak (win_streak_B)

        /// <summary>"A fresh start": a streak just ended with a loss. Try again runs the retry the player chose.</summary>
        public void ShowFreshStart(BoosterKind next, Action tryAgain)
        {
            if (_fresh != null) return;
            _fresh = DailySheets.FreshStart(transform, _kit, next, () =>
            {
                var o = _fresh; _fresh = null;
                if (o != null) o.Close(tryAgain); else tryAgain?.Invoke();
            });
        }
    }

    /// <summary>Shared daily-loop pieces.</summary>
    public static class DailyUi
    {
        public static readonly Color MintCheck = new Color(0.36f, 0.72f, 0.45f);
        public static readonly Color Muted = new Color(UiKit.Ink.r, UiKit.Ink.g, UiKit.Ink.b, 0.42f);
        public static readonly Color EmptyGear = new Color(0.93f, 0.85f, 0.72f);
        static readonly Color[] TrackColours = { new Color(0.55f, 0.85f, 0.6f), new Color(0.98f, 0.82f, 0.32f), new Color(0.96f, 0.6f, 0.5f) };

        public static Sprite BoosterIcon(OrcubArt art, BoosterKind k) =>
            k == BoosterKind.Hint ? art.iconHint : k == BoosterKind.Undo ? art.iconUndo : art.iconUnclamp;

        public static string BoosterName(BoosterKind k) => k == BoosterKind.Hint ? "Hint" : k == BoosterKind.Undo ? "Undo" : "Unclamp";

        public static Color BoosterTint(BoosterKind k) =>
            k == BoosterKind.Hint ? new Color(0.98f, 0.83f, 0.3f) : k == BoosterKind.Undo ? UiKit.Coral : new Color(0.72f, 0.6f, 0.9f);

        /// <summary>Icon + tint for a reward from the server table.</summary>
        public static (Sprite sprite, Color tint) RewardIcon(OrcubArt art, DailyRewardView r)
        {
            switch (r.Kind)
            {
                case "gold": return (art.goldBar != null ? art.goldBar : art.uiCogClay, Color.white);
                case "hint": return (art.iconHint, BoosterTint(BoosterKind.Hint));
                case "undo": return (art.iconUndo, BoosterTint(BoosterKind.Undo));
                case "unclamp": return (art.iconUnclamp, BoosterTint(BoosterKind.Unclamp));
                case "unlimited_lives_minutes": return (art.infinityHeart != null ? art.infinityHeart : art.uiHeartClay, art.infinityHeart != null ? Color.white : UiKit.HeartRed);
                default: return (art.uiStarClay, UiKit.StarGold);
            }
        }

        /// <summary>Mint check (completed day).</summary>
        public static Image Check(UiKit kit, Transform parent, Color? tint = null)
        {
            var c = kit.MakeImage(parent, AccountArt.Check, tint ?? MintCheck);
            return c;
        }

        /// <summary>A rounded mint outline around a cell (today), drawn as a mint card with a cream face inset.</summary>
        public static void Outline(UiKit kit, RectTransform rt, float width = 7f)
        {
            var outer = kit.Card(rt, 0.11f);
            outer.color = new Color(0.45f, 0.78f, 0.52f);
            UiKit.Anchor(outer.rectTransform, 0, 0, 1, 1);
            outer.transform.SetAsFirstSibling();
            var inner = kit.Card(rt, 0.09f);
            inner.color = new Color(0.99f, 0.94f, 0.85f);
            UiKit.Anchor(inner.rectTransform, 0, 0, 1, 1);
            inner.rectTransform.offsetMin = new Vector2(width, width);
            inner.rectTransform.offsetMax = new Vector2(-width, -width);
            inner.transform.SetSiblingIndex(1);
        }

        /// <summary>Three gear stations joined by dots: filled + checked for each win (approved win_streak_B).</summary>
        public static RectTransform Track(UiKit kit, RectTransform parent, int wins, float height)
        {
            var art = kit.Art;
            var track = UiKit.Rect("Track", parent);
            AccountUi.Slab(kit, track, new Color(0.95f, 0.88f, 0.77f), height);
            for (int i = 0; i < 3; i++)
            {
                float x = 0.17f + i * 0.33f;
                var gear = kit.MakeImage(track, art.homeGearRing, i < wins ? TrackColours[i] : EmptyGear);
                UiKit.Place(gear.rectTransform, x, 0.5f, height * 1.02f, height * 1.02f);
                if (i < wins)
                {
                    var disc = kit.MakeImage(gear.transform, art.uiRound, UiKit.CardCream);
                    UiKit.Anchor(disc.rectTransform, 0.27f, 0.27f, 0.73f, 0.73f);
                    var chk = Check(kit, disc.transform, UiKit.Ink);
                    UiKit.Anchor(chk.rectTransform, 0.2f, 0.22f, 0.8f, 0.82f);
                }
                if (i < 2)
                    for (int d = 0; d < 3; d++)
                    {
                        var dot = kit.MakeImage(track, art.uiRound, new Color(0.62f, 0.45f, 0.36f));
                        UiKit.Place(dot.rectTransform, x + 0.12f + d * 0.045f, 0.5f, 22f, 22f);
                    }
            }
            return track;
        }

        /// <summary>Level-start ticket: "Free Hint ready · 3/3 wins" (one line: it takes the goal line's slot).</summary>
        public static RectTransform Ticket(UiKit kit, RectTransform parent, BoosterKind kind)
        {
            var art = kit.Art;
            var t = UiKit.Rect("WinTicket", parent);
            AccountUi.Slab(kit, t, UiKit.Slab, 96f);
            var icon = kit.MakeImage(t, BoosterIcon(art, kind), BoosterTint(kind));
            UiKit.Anchor(icon.rectTransform, 0.05f, 0.04f, 0.15f, 0.96f);
            AccountUi.VRule(kit, t, 0.18f, 0.18f, 0.82f);
            var a = kit.MakeText(t, $"Free {BoosterName(kind)} ready · 3/3 wins", UiKit.Body, UiKit.Ink, TextAnchor.MiddleLeft);
            UiKit.Anchor(a.rectTransform, 0.22f, 0f, 0.98f, 1f);
            AccountUi.Fit(a, 560f, UiKit.Body, UiKit.Caption);
            return t;
        }

        /// <summary>The win card's streak strip (track, "2/3 wins", next-reward line), under the wreath card.</summary>
        public static RectTransform Strip(UiKit kit, RectTransform parent, int wins, BoosterKind next, int waitsForLevel = 0)
        {
            var card = UiKit.Rect("WinStreak", parent);
            card.anchorMin = new Vector2(0f, 1f);
            card.anchorMax = new Vector2(1f, 1f);
            card.pivot = new Vector2(0.5f, 1f);
            float y = 30f;
            var trackRow = AccountUi.Row(card, "TrackRow", y, 120f, 60f, 60f);
            var track = Track(kit, trackRow, wins, 120f);
            UiKit.Anchor(track, 0, 0, 1, 1);
            y += 120f + 18f;
            var count = kit.MakeText(AccountUi.Row(card, "Count", y, 66f, 30f, 30f), $"{Mathf.Min(wins, 3)}/3 wins", UiKit.Title - 4, UiKit.Ink);
            UiKit.Anchor(count.rectTransform, 0, 0, 1, 1);
            y += 66f;
            var line = kit.MakeText(AccountUi.Row(card, "Line", y, 52f, 30f, 30f), DailyCalendar.WinsLine(wins, next, waitsForLevel), UiKit.Label, UiKit.Ink);
            UiKit.Anchor(line.rectTransform, 0, 0, 1, 1);
            AccountUi.Fit(line, 800f, UiKit.Label, UiKit.Caption);
            y += 52f + 30f;
            AccountUi.Panel(kit, card, UiKit.CardCream, 90f);
            card.sizeDelta = new Vector2(0f, y);
            return card;
        }
    }

    /// <summary>Approved daily_puzzle_B destination: month calendar, streak, Play today, and this week.</summary>
    public sealed class DailyScreen : MonoBehaviour
    {
        UiKit _kit;
        Action _play, _back;
        RectTransform _safe, _body, _calendar, _grid, _week, _hero;
        Text _month, _streakTitle, _streakLine, _playLabel, _statusTitle, _statusLine;
        RectTransform _playBtn;
        Image _statusIcon, _statusCheck;
        CanvasGroup _group;
        float _refresh;
        string _builtFor;

        public static DailyScreen Create(Transform parent, UiKit kit, Action play, Action back)
        {
            var (canvas, safe) = UiKit.Screen("Daily", parent, DailyFlow.ScreenOrder);
            var s = canvas.gameObject.AddComponent<DailyScreen>();
            s._kit = kit;
            s._play = play;
            s._back = back;
            s._safe = safe;
            s._group = canvas.GetComponent<CanvasGroup>();
            kit.Felt(canvas.transform).transform.SetAsFirstSibling();
            s.Build();
            s.Refresh();
            s.StartCoroutine(UiKit.FadeIn(s._group));
            return s;
        }

        void Build()
        {
            var kit = _kit;
            var art = kit.Art;
            TopBar.Create(_safe, kit, art.glyphBack, UiKit.Ink, () => _back?.Invoke());

            _body = UiKit.Rect("Body", _safe);
            UiKit.Anchor(_body, 0.04f, 0f, 0.96f, 0.905f);

            float y = 0f;
            var word = kit.MakeText(AccountUi.Row(_body, "Word", y, 130f, 0f, 0f), "orcub", 120, UiKit.Cream);
            UiKit.Anchor(word.rectTransform, 0, 0, 1, 1);
            UiKit.Shadow(word, 0.3f);
            y += 130f;
            _hero = AccountUi.Row(_body, "Hero", y, 230f, 0f, 0f);
            if (art.homeHero != null)
            {
                var hero = kit.MakeImage(_hero, art.homeHero, Color.white);
                UiKit.Anchor(hero.rectTransform, 0.32f, 0f, 0.68f, 1f);
            }
            y += 230f + 22f;

            // ---- calendar card
            const float calH = 1070f;
            _calendar = AccountUi.Row(_body, "Calendar", y, calH, 0f, 0f);
            AccountUi.Panel(kit, _calendar, UiKit.CardCream, 96f);
            var head = AccountUi.Row(_calendar, "Head", 34f, 150f, 46f, 46f);
            var cal = kit.MakeImage(head, FeatureArt.Calendar, Color.white);
            var crt = cal.rectTransform;
            crt.anchorMin = crt.anchorMax = new Vector2(0f, 0.5f);
            crt.pivot = new Vector2(0f, 0.5f);
            crt.sizeDelta = new Vector2(170f, 158f);
            crt.anchoredPosition = new Vector2(30f, 0f);
            AccountUi.VRule(kit, head, 0.25f, 0.14f, 0.86f);
            var title = kit.MakeText(head, "Daily Puzzle", UiKit.Display + 4, UiKit.Ink, TextAnchor.LowerLeft);
            UiKit.Anchor(title.rectTransform, 0.29f, 0.46f, 1f, 1f);
            _month = kit.MakeText(head, "", UiKit.Title - 6, UiKit.Ink, TextAnchor.UpperLeft);
            UiKit.Anchor(_month.rectTransform, 0.3f, 0f, 1f, 0.46f);

            _grid = AccountUi.Row(_calendar, "Grid", 206f, 560f, 40f, 40f);

            var streak = AccountUi.Row(_calendar, "Streak", 790f, 124f, 110f, 46f);
            var sIcon = kit.MakeImage(streak, FeatureArt.CalendarCheck, Color.white);
            var srt = sIcon.rectTransform;
            srt.anchorMin = srt.anchorMax = new Vector2(0f, 0.5f);
            srt.pivot = new Vector2(0f, 0.5f);
            srt.sizeDelta = new Vector2(132f, 124f);
            AccountUi.VRule(kit, streak, 0.2f, 0.12f, 0.88f);
            _streakTitle = kit.MakeText(streak, "", UiKit.Title, UiKit.Ink, TextAnchor.LowerLeft);
            UiKit.Anchor(_streakTitle.rectTransform, 0.24f, 0.46f, 1f, 1f);
            _streakLine = kit.MakeText(streak, "", UiKit.Body, new Color(0.48f, 0.3f, 0.4f), TextAnchor.UpperLeft);
            UiKit.Anchor(_streakLine.rectTransform, 0.24f, 0f, 1f, 0.46f);

            var playRow = AccountUi.Row(_calendar, "Play", 930f, 140f, 40f, 40f);
            var play = kit.Pill(playRow, "Play today", UiKit.Mint, 88, () =>
            {
                if (DailyGateway.Current?.CanPlay != true) return;
                Haptics.Play(Haptics.Impact.Medium, 0.7f);
                _play?.Invoke();
            });
            UiKit.Anchor(play, 0, 0, 1, 1);
            _playBtn = play;
            _playLabel = play.GetComponentInChildren<Text>();
            y += calH + 26f;

            // ---- this week + today's status
            _week = AccountUi.Row(_body, "Week", y, 340f, 0f, 0f);
            AccountUi.Panel(kit, _week, UiKit.CardCream, 80f);
            var status = AccountUi.Row(_week, "Status", 200f, 116f, 80f, 40f);
            var disc = kit.MakeImage(status, art.uiRound, new Color(0.42f, 0.76f, 0.5f));
            var drt = disc.rectTransform;
            drt.anchorMin = drt.anchorMax = new Vector2(0f, 0.5f);
            drt.pivot = new Vector2(0f, 0.5f);
            drt.sizeDelta = new Vector2(112f, 112f);
            _statusIcon = disc;
            _statusCheck = DailyUi.Check(kit, disc.transform, UiKit.CardCream);
            UiKit.Anchor(_statusCheck.rectTransform, 0.24f, 0.24f, 0.76f, 0.78f);
            AccountUi.VRule(kit, status, 0.2f, 0.12f, 0.88f);
            _statusTitle = kit.MakeText(status, "", UiKit.Title, UiKit.Ink, TextAnchor.LowerLeft);
            UiKit.Anchor(_statusTitle.rectTransform, 0.24f, 0.46f, 1f, 1f);
            _statusLine = kit.MakeText(status, "", UiKit.Body, new Color(0.48f, 0.3f, 0.4f), TextAnchor.UpperLeft);
            UiKit.Anchor(_statusLine.rectTransform, 0.24f, 0f, 1f, 0.46f);
        }

        void Update()
        {
            // short phones: the hero illustration steps aside so the calendar never clips
            if (_hero != null && _body.rect.height > 10f)
            {
                bool room = _body.rect.height >= 1830f;
                if (_hero.gameObject.activeSelf != room)
                {
                    _hero.gameObject.SetActive(room);
                    float shift = room ? 0f : 252f;
                    _calendar.anchoredPosition = new Vector2(0f, -(130f + 252f) + shift);
                    _week.anchoredPosition = new Vector2(0f, -(130f + 252f + 1070f + 26f) + shift);
                }
            }
            _refresh -= Time.unscaledDeltaTime;
            if (_refresh > 0f) return;
            _refresh = 1f;
            Refresh();   // UTC rollover / service changes while open
        }

        public void Refresh()
        {
            var port = DailyGateway.Current;
            string today = port?.TodayUtc ?? DailyCalendar.Key(DateTime.UtcNow.Date);
            Func<string, DailyDay> state = d => port?.StateFor(d) ?? (string.CompareOrdinal(d, today) < 0 ? DailyDay.Missed : d == today ? DailyDay.Available : DailyDay.Upcoming);
            _month.text = DailyCalendar.MonthTitle(today);

            string key = today + "|" + (port != null ? port.StateFor(today).ToString() : "-");
            if (key != _builtFor) { _builtFor = key; BuildGrid(DailyCalendar.Month(today, state)); BuildWeek(DailyCalendar.Week(today, state)); }

            int streak = port?.Streak ?? 0;
            _streakTitle.text = DailyCalendar.StreakTitle(streak);
            _streakLine.text = DailyCalendar.NextMilestone(streak) ?? (port?.HasFreeze == true ? "Streak freeze ready" : "");

            bool done = state(today) == DailyDay.Completed;
            bool can = port != null && port.CanPlay;
            _playLabel.text = done ? "Done for today" : can ? "Play today" : "Not available yet";
            _playLabel.fontSize = done || !can ? UiKit.Title : 88;
            var img = _playBtn.GetComponent<Image>();
            img.color = can ? UiKit.Mint : AccountUi.TileTan;
            _playBtn.GetComponent<Button>().interactable = can;

            _statusIcon.sprite = done ? _kit.Art.uiRound : FeatureArt.Calendar;
            _statusIcon.color = done ? new Color(0.42f, 0.76f, 0.5f) : Color.white;
            _statusCheck.enabled = done;
            _statusTitle.text = done ? "Today claimed" : "Today’s puzzle is ready";   // the mint disc carries the check (no font fallback glyph)
            _statusLine.text = done ? "Back tomorrow" : streak > 0 ? "Play to keep your streak" : "A new puzzle every day";
        }

        void BuildGrid(List<DayCell> cells)
        {
            for (int i = _grid.childCount - 1; i >= 0; i--) Destroy(_grid.GetChild(i).gameObject);
            var kit = _kit;
            ShopUi.InnerCard(kit, _grid, 60f, new Color(0.99f, 0.93f, 0.83f));
            int rows = cells.Count / 7;
            const float head = 74f, pad = 12f;
            float rowH = (560f - head - pad * 2f) / Mathf.Max(5, rows);
            for (int c = 0; c < 7; c++)
            {
                var r = AccountUi.Row(_grid, DailyCalendar.WeekDays[c], pad, head, 0f, 0f);
                r.anchorMin = new Vector2(c / 7f, 1f);
                r.anchorMax = new Vector2((c + 1) / 7f, 1f);
                r.offsetMin = new Vector2(0f, r.offsetMin.y);
                r.offsetMax = new Vector2(0f, r.offsetMax.y);
                var t = kit.MakeText(r, DailyCalendar.WeekDays[c], UiKit.Label, UiKit.Ink);
                UiKit.Anchor(t.rectTransform, 0, 0, 1, 1);
            }
            for (int i = 0; i < cells.Count; i++)
            {
                var cell = cells[i];
                if (cell.Mark == DayMark.Empty) continue;
                int row = i / 7, col = i % 7;
                var rt = AccountUi.Row(_grid, cell.UtcKey, pad + head + row * rowH, rowH, 0f, 0f);
                rt.anchorMin = new Vector2(col / 7f, 1f);
                rt.anchorMax = new Vector2((col + 1) / 7f, 1f);
                rt.offsetMin = new Vector2(6f, rt.offsetMin.y + 4f);
                rt.offsetMax = new Vector2(-6f, rt.offsetMax.y - 4f);
                bool today = cell.Mark == DayMark.Today || cell.Mark == DayMark.TodayDone;
                bool check = cell.Mark == DayMark.Completed || cell.Mark == DayMark.TodayDone;
                if (today) DailyUi.Outline(kit, rt);
                var n = kit.MakeText(rt, cell.Day.ToString(), today ? UiKit.Body - 2 : UiKit.Body + 4, cell.Mark == DayMark.Missed ? DailyUi.Muted : UiKit.Ink);
                UiKit.Anchor(n.rectTransform, 0f, check || today ? 0.4f : 0f, 1f, today ? 0.98f : 1f);
                if (check)
                {
                    var ck = DailyUi.Check(kit, rt);
                    UiKit.Place(ck.rectTransform, 0.5f, 0.24f, 52f, 44f);
                }
                else if (today)
                {
                    var t = kit.MakeText(rt, "Today", 24, UiKit.Ink);
                    UiKit.Anchor(t.rectTransform, 0f, 0.08f, 1f, 0.42f);
                }
            }
        }

        void BuildWeek(List<DayCell> cells)
        {
            var kit = _kit;
            var old = _week.Find("Days");
            if (old != null) Destroy(old.gameObject);
            var days = AccountUi.Row(_week, "Days", 26f, 160f, 34f, 34f);
            for (int i = 0; i < 7; i++)
            {
                var col = UiKit.Rect(DailyCalendar.WeekDays[i], days);
                UiKit.Anchor(col, i / 7f, 0f, (i + 1) / 7f, 1f);
                var label = kit.MakeText(col, DailyCalendar.WeekDays[i], UiKit.Label, UiKit.Ink);
                UiKit.Anchor(label.rectTransform, 0f, 0.66f, 1f, 1f);
                var mark = UiKit.Rect("Mark", col);
                UiKit.Anchor(mark, 0.08f, 0f, 0.92f, 0.62f);
                var cell = cells[i];
                bool today = cell.Mark == DayMark.Today || cell.Mark == DayMark.TodayDone;
                if (today) DailyUi.Outline(kit, mark);
                if (cell.Mark == DayMark.Completed || cell.Mark == DayMark.TodayDone)
                {
                    var ck = DailyUi.Check(kit, mark);
                    UiKit.Place(ck.rectTransform, 0.5f, 0.5f, 64f, 54f);
                }
                else
                {   // an empty ring: the day is still open (or was missed)
                    var ring = kit.MakeImage(mark, kit.Art.uiRound, cell.Mark == DayMark.Missed ? DailyUi.EmptyGear : new Color(0.78f, 0.64f, 0.5f));
                    UiKit.Place(ring.rectTransform, 0.5f, 0.5f, 50f, 50f);
                    var hole = kit.MakeImage(ring.transform, kit.Art.uiRound, UiKit.CardCream);
                    UiKit.Anchor(hole.rectTransform, 0.2f, 0.2f, 0.8f, 0.8f);
                }
            }
            var rule = kit.Divider(_week);
            UiKit.Anchor(rule.rectTransform, 0.06f, 1f, 0.94f, 1f);
            rule.rectTransform.offsetMin = new Vector2(0f, -194f);
            rule.rectTransform.offsetMax = new Vector2(0f, -190f);
        }
    }

    /// <summary>The daily-loop sheets: gift (daily_gift_B), opt-in (notifications_B), fresh start (win_streak_B).</summary>
    public static class DailySheets
    {
        public static AccountOverlay Gift(Transform parent, UiKit kit, Action close)
        {
            var art = kit.Art;
            var port = DailyGateway.Current;
            var o = AccountOverlay.Create(parent, kit, "DailyGift", DailyFlow.SheetOrder, 0.55f);
            var card = o.Panel;
            // the opening box sits proud of the sheet's top edge (approved daily_gift_B). review m, d07: kept low and
            // the sheet compact, so the box clears Home's task chip above the sheet
            var box = kit.MakeImage(card, FeatureArt.GiftOpen, Color.white);
            box.preserveAspect = true;
            var brt = box.rectTransform;
            brt.anchorMin = brt.anchorMax = new Vector2(0.5f, 1f);
            brt.pivot = new Vector2(0.5f, 0f);
            brt.sizeDelta = new Vector2(260f, 225f);
            brt.anchoredPosition = new Vector2(-335f, -140f);   // left of the title, rising ~85 above the edge

            float y = 50f;
            var title = kit.MakeText(AccountUi.Row(card, "Title", y, 100f, 270f, 140f), "Daily gift", 86, UiKit.Ink);
            UiKit.Anchor(title.rectTransform, 0, 0, 1, 1);
            ShopUi.Close(kit, card, 40f, 40f, close);
            y += 100f + 20f;

            // today's gift, then what the next two days bring (the server rotates one reward per UTC day)
            var row = AccountUi.Row(card, "Gifts", y, 260f, 46f, 46f);
            ShopUi.InnerCard(kit, row, 60f, new Color(0.99f, 0.93f, 0.83f));
            string today = port?.TodayUtc ?? DailyCalendar.Key(DateTime.UtcNow.Date);
            string[] captions = { "Today", "Tomorrow", "Next day" };
            for (int i = 0; i < 3; i++)
            {
                var col = UiKit.Rect("Day" + i, row);
                UiKit.Anchor(col, i / 3f, 0f, (i + 1) / 3f, 1f);
                if (i > 0) AccountUi.VRule(kit, row, i / 3f, 0.14f, 0.86f);
                var reward = port != null ? port.GiftFor(DailyCalendar.Key(DailyCalendar.Parse(today).AddDays(i))) : new DailyRewardView("gold", 20);
                var (sprite, tint) = DailyUi.RewardIcon(art, reward);
                var ic = kit.MakeImage(col, sprite, tint);
                UiKit.Anchor(ic.rectTransform, 0.18f, 0.4f, 0.82f, 0.92f);
                var label = kit.MakeText(col, DailyCalendar.RewardText(reward), UiKit.Body, UiKit.Ink);
                UiKit.Anchor(label.rectTransform, 0.02f, 0.2f, 0.98f, 0.4f);
                AccountUi.Fit(label, 280f, UiKit.Body, UiKit.Caption);
                var cap = kit.MakeText(col, captions[i], UiKit.Caption - 4, i == 0 ? UiKit.Ink : DailyUi.Muted);
                UiKit.Anchor(cap.rectTransform, 0f, 0.04f, 1f, 0.2f);
                if (i > 0) { ic.color = new Color(tint.r, tint.g, tint.b, 0.45f); label.color = DailyUi.Muted; }
            }
            y += 260f + 24f;

            var collectRow = AccountUi.Row(card, "Collect", y, 136f, 70f, 70f);
            bool claimable = port != null && port.CanClaimGift;
            var collect = kit.Pill(collectRow, claimable ? "Collect gift" : "Collected today", claimable ? UiKit.Mint : AccountUi.TileTan, 80, () => { });
            UiKit.Anchor(collect, 0, 0, 1, 1);
            var label2 = collect.GetComponentInChildren<Text>();
            y += 136f + 8f;
            var helper = kit.MakeText(AccountUi.Row(card, "Helper", y, 46f, 46f, 46f),
                claimable ? "" : $"Next gift {DailyCalendar.Countdown(DateTimeOffset.UtcNow, port?.NextGiftAt ?? DateTimeOffset.UtcNow).ToLowerInvariant()}",
                UiKit.Label, AccountUi.InkSoft);
            UiKit.Anchor(helper.rectTransform, 0, 0, 1, 1);
            y += 46f + 18f;

            bool busy = false;
            var b = collect.GetComponent<Button>();
            b.onClick.RemoveAllListeners();
            b.onClick.AddListener(async () =>
            {
                if (busy || port == null || !port.CanClaimGift) return;
                busy = true;
                Haptics.Play(Haptics.Impact.Light, 0.6f);
                label2.text = "Collecting…";
                ShopUi.SetInteractable(collect, false);
                bool ok;
                try { ok = await port.ClaimGift(); }
                catch (Exception) { ok = false; }
                if (o == null) return;
                busy = false;
                if (ok)
                {
                    Haptics.Notify(Haptics.Notice.Success);
                    label2.text = "Collected!";
                    o.StartCoroutine(After(0.5f, close));
                    return;
                }
                ShopUi.SetInteractable(collect, true);
                label2.text = "Collect gift";
                helper.text = "Couldn’t collect. Try again";
                helper.color = AccountUi.DeleteRed;
                Haptics.Notify(Haptics.Notice.Warning);
            });
            if (!claimable) ShopUi.SetInteractable(collect, false);

            AccountUi.Panel(kit, card, UiKit.CardCream, 104f);
            o.BottomCard(y, 40f);
            o.Enter();
            return o;
        }

        static IEnumerator After(float seconds, Action a)
        {
            yield return new WaitForSecondsRealtime(seconds);
            a?.Invoke();
        }

        /// <summary>
        /// Pre-permission sheet (notifications_B): calm copy, equal Not now / Continue. Only Continue leads to the
        /// iOS permission prompt; Not now just closes. Strings mirror Daily's DailyStrings.English.
        /// </summary>
        public static AccountOverlay OptIn(Transform parent, UiKit kit, Action onContinue, Action onLater)
        {
            var o = AccountOverlay.Create(parent, kit, "NotificationOptIn", DailyFlow.SheetOrder, 0.55f);
            var card = o.Panel;
            float y = 46f;
            var head = AccountUi.Row(card, "Head", y, 150f, 40f, 40f);
            var bell = kit.MakeImage(head, FeatureArt.Bell, Color.white);
            var brt = bell.rectTransform;
            brt.anchorMin = brt.anchorMax = new Vector2(0f, 0.5f);
            brt.pivot = new Vector2(0f, 0.5f);
            brt.sizeDelta = new Vector2(170f, 158f);
            var title = kit.MakeText(head, "Play on your time", UiKit.Display, UiKit.Ink, TextAnchor.MiddleLeft);
            UiKit.Anchor(title.rectTransform, 0, 0, 1, 1);
            title.rectTransform.offsetMin = new Vector2(186f, 0f);
            AccountUi.Fit(title, 700f, UiKit.Display, UiKit.Title);
            y += 150f + 18f;
            var bodyRow = AccountUi.Row(card, "Body", y, 140f, 70f, 70f);
            var body = AccountUi.Wrapped(kit, bodyRow, "We’ll tell you when lives refill and the daily puzzle is ready", UiKit.Body + 6, UiKit.Ink, TextAnchor.MiddleCenter);
            body.lineSpacing = 0.95f;
            y += 140f + 30f;
            var pair = AccountUi.Row(card, "Choices", y, 146f, 40f, 40f);
            var later = kit.Pill(pair, "Not now", UiKit.CardCream, UiKit.Title, () => onLater?.Invoke());
            UiKit.Anchor(later, 0f, 0f, 0.49f, 1f);
            var go = kit.Pill(pair, "Continue", UiKit.Mint, UiKit.Title, () => onContinue?.Invoke());
            UiKit.Anchor(go, 0.51f, 0f, 1f, 1f);
            y += 146f + 26f;
            var foot = kit.MakeText(AccountUi.Row(card, "Footer", y, 56f, 40f, 40f), "iOS will ask next. You choose.", UiKit.Body, UiKit.Ink);
            UiKit.Anchor(foot.rectTransform, 0, 0, 1, 1);
            y += 56f + 40f;
            AccountUi.Panel(kit, card, new Color(0.99f, 0.92f, 0.8f), 104f);
            o.BottomCard(y, 60f);
            o.Enter();
            return o;
        }

        /// <summary>"A fresh start" (win_streak_B): gentle reset copy, an empty track, and Try again.</summary>
        public static AccountOverlay FreshStart(Transform parent, UiKit kit, BoosterKind next, Action tryAgain)
        {
            var o = AccountOverlay.Create(parent, kit, "FreshStart", DailyFlow.SheetOrder, 0.55f);
            var card = o.Panel;
            float y = 46f;
            var title = kit.MakeText(AccountUi.Row(card, "Title", y, 110f, 40f, 40f), "A fresh start", UiKit.Display + 6, UiKit.Ink);
            UiKit.Anchor(title.rectTransform, 0, 0, 1, 1);
            y += 110f + 4f;
            var line = kit.MakeText(AccountUi.Row(card, "Line", y, 56f, 40f, 40f), $"Win 3 in a row for a free {DailyUi.BoosterName(next)}.", UiKit.Body, UiKit.Ink);
            UiKit.Anchor(line.rectTransform, 0, 0, 1, 1);
            y += 56f + 30f;
            var trackRow = AccountUi.Row(card, "Track", y, 130f, 70f, 70f);
            var track = DailyUi.Track(kit, trackRow, 0, 130f);
            UiKit.Anchor(track, 0, 0, 1, 1);
            y += 130f + 40f;
            var again = AccountUi.Row(card, "TryAgain", y, 146f, 70f, 70f);
            var pill = kit.Pill(again, "Try again", UiKit.Mint, 80, () => tryAgain?.Invoke());
            UiKit.Anchor(pill, 0, 0, 1, 1);
            y += 146f + 46f;
            AccountUi.Panel(kit, card, UiKit.CardCream, 104f);
            o.CenterCard(0.84f, y);
            o.Enter();
            return o;
        }
    }
}
