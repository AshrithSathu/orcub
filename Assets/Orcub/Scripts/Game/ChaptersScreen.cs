using System;
using Orcub.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Orcub.Game
{
    /// <summary>
    /// "All levels" (approved nav v3, chapters A): one full-width clay card per chapter of 20 levels.
    /// The current chapter carries the only primary button (Continue); finished chapters show a chevron;
    /// the next locked chapter says what unlocks it; later ones just show a padlock.
    /// </summary>
    public sealed class ChaptersScreen : MonoBehaviour
    {
        const float CardHeight = 400f, Gap = 34f;

        UiKit _kit;
        Action<int> _onOpen;
        Action _onBack;
        CanvasGroup _group;
        RectTransform _content;
        ScrollRect _scroll;
        TopBar _bar;
        Text _subtitle;
        // L8: only the cards around the viewport exist (the saga is infinite); built / destroyed as it scrolls
        const int Buffer = 3;
        readonly System.Collections.Generic.Dictionary<int, RectTransform> _cards = new System.Collections.Generic.Dictionary<int, RectTransform>();
        readonly System.Collections.Generic.List<int> _drop = new System.Collections.Generic.List<int>();
        int _count, _current;

        public static ChaptersScreen Create(Transform parent, UiKit kit, Action<int> onOpen, Action onBack)
        {
            var (canvas, safe) = UiKit.Screen("Chapters", parent, 205);
            var c = canvas.gameObject.AddComponent<ChaptersScreen>();
            c._kit = kit;
            c._onOpen = onOpen;
            c._onBack = onBack;
            c._group = canvas.GetComponent<CanvasGroup>();
            kit.Felt(canvas.transform).transform.SetAsFirstSibling();
            c.Build(safe);
            canvas.gameObject.SetActive(false);
            return c;
        }

        public void Show()
        {
            gameObject.SetActive(true);
            _bar.Refresh();
            Populate();
            StartCoroutine(UiKit.FadeIn(_group));
        }

        public void Hide() => gameObject.SetActive(false);

        void Build(RectTransform safe)
        {
            var kit = _kit;
            _bar = TopBar.Create(safe, kit, kit.Art.glyphBack != null ? kit.Art.glyphBack : null, UiKit.Ink, () => _onBack?.Invoke());
            if (kit.Art.glyphBack == null)
            {
                var arrow = kit.MakeText(_bar.transform.GetChild(0), "‹", UiKit.Display, UiKit.Ink);
                UiKit.Anchor(arrow.rectTransform, 0, 0.06f, 1, 1);
            }

            var title = kit.MakeText(safe, "All levels", 96, UiKit.Cream);
            UiKit.Anchor(title.rectTransform, 0f, 0.83f, 1f, 0.9f);
            UiKit.Shadow(title, 0.3f);
            _subtitle = kit.MakeText(safe, "", UiKit.Body, new Color(UiKit.Cream.r, UiKit.Cream.g, UiKit.Cream.b, 0.8f));
            UiKit.Anchor(_subtitle.rectTransform, 0f, 0.795f, 1f, 0.83f);

            var view = UiKit.Rect("Viewport", safe);
            UiKit.Anchor(view, 0f, 0f, 1f, 0.78f);
            view.gameObject.AddComponent<RectMask2D>().softness = new Vector2Int(0, 50);
            var hit = view.gameObject.AddComponent<Image>();
            hit.color = new Color(0, 0, 0, 0);
            _content = UiKit.Rect("Content", view);
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);
            _scroll = view.gameObject.AddComponent<ScrollRect>();
            _scroll.content = _content;
            _scroll.viewport = view;
            _scroll.horizontal = false;
            _scroll.decelerationRate = 0.12f;
            _scroll.onValueChanged.AddListener(_ => RefreshWindow());
        }

        void Populate()
        {
            for (int i = _content.childCount - 1; i >= 0; i--) Destroy(_content.GetChild(i).gameObject);
            _cards.Clear();
            _count = Chapters.Count;
            _subtitle.text = $"{EarnedStars()} stars earned";   // the saga never ends: no total
            _content.sizeDelta = new Vector2(0f, _count * (CardHeight + Gap) + 80f);
            _current = Chapters.Of(Chapters.Frontier);
            _scroll.verticalNormalizedPosition = 1f;   // the start (chapter 1) on top, as approved
            _content.anchoredPosition = new Vector2(_content.anchoredPosition.x, 0f);
            RefreshWindow();
        }

        /// <summary>Star total: one pass over the stored best-star keys up to the frontier (no per-card work).</summary>
        static int _starsTotal = -1, _starsLast = -1, _starsVersion = -1;

        /// <summary>
        /// Review i, L9: the pass over every level's key runs only when the frontier or a best-star count changed
        /// since the last time (a deep infinite player has thousands of keys).
        /// </summary>
        static int EarnedStars()
        {
            int last = Mathf.Max(LevelLibrary.CuratedCount, Progress.HighestUnlocked + 1);
            if (_starsTotal >= 0 && last == _starsLast && Progress.StarsVersion == _starsVersion) return _starsTotal;
            int total = 0;
            for (int i = 0; i < last; i++) total += Progress.BestStars(i);
            _starsTotal = total; _starsLast = last; _starsVersion = Progress.StarsVersion;
            return total;
        }

        /// <summary>Builds the cards in view (± <see cref="Buffer"/>) and destroys the ones scrolled far away.</summary>
        void RefreshWindow()
        {
            if (_content == null || _count == 0) return;
            float pitch = CardHeight + Gap;
            float top = Mathf.Max(0f, _content.anchoredPosition.y - 20f);
            float viewH = ((RectTransform)_scroll.viewport).rect.height;
            int first = Mathf.Max(0, Mathf.FloorToInt(top / pitch) - Buffer);
            int last = Mathf.Min(_count - 1, Mathf.CeilToInt((top + Mathf.Max(viewH, pitch)) / pitch) + Buffer);
            _drop.Clear();
            foreach (var kv in _cards) if (kv.Key < first || kv.Key > last) _drop.Add(kv.Key);
            foreach (int c in _drop) { if (_cards[c] != null) Destroy(_cards[c].gameObject); _cards.Remove(c); }
            for (int c = first; c <= last; c++)
                if (!_cards.ContainsKey(c)) _cards[c] = Card(c, c == _current);
        }

        RectTransform Card(int chapter, bool isCurrent)
        {
            var kit = _kit;
            var art = kit.Art;
            bool unlocked = Chapters.Unlocked(chapter);
            bool nextLocked = !unlocked && Chapters.Unlocked(chapter - 1);

            var rt = UiKit.Rect($"Chapter {chapter + 1}", _content);
            rt.anchorMin = new Vector2(0.06f, 1f);
            rt.anchorMax = new Vector2(0.94f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(0f, CardHeight);
            rt.anchoredPosition = new Vector2(0f, -(20f + chapter * (CardHeight + Gap)));

            var bg = kit.Card(rt, 0.55f);
            UiKit.Anchor(bg.rectTransform, 0, 0, 1, 1);
            if (!unlocked) bg.color = new Color(0.86f, 0.78f, 0.72f);   // locked chapters sit back

            var pic = kit.MakeImage(rt, Chapters.Art(art, chapter), unlocked ? Color.white : new Color(1f, 1f, 1f, 0.75f));
            UiKit.Anchor(pic.rectTransform, 0.04f, 0.12f, 0.31f, 0.88f);
            var divider = kit.Divider(rt);
            UiKit.Anchor(divider.rectTransform, 0.335f, 0.14f, 0.34f, 0.86f);

            var ink = UiKit.Ink;
            var soft = new Color(ink.r, ink.g, ink.b, 0.72f);
            // text block: sits high when a button follows, centred when it doesn't
            bool hasButton = isCurrent || nextLocked;
            float lift = hasButton ? 0f : -0.12f;
            var cap = kit.MakeText(rt, $"Chapter {chapter + 1} · Levels {Chapters.First(chapter) + 1}–{Chapters.Last(chapter) + 1}",
                UiKit.Caption, soft, TextAnchor.MiddleLeft);
            UiKit.Anchor(cap.rectTransform, 0.38f, 0.75f + lift, 0.98f, 0.9f + lift);
            var name = kit.MakeText(rt, Chapters.Name(chapter), 54, ink, TextAnchor.MiddleLeft);
            UiKit.Anchor(name.rectTransform, 0.38f, 0.56f + lift, 0.86f, 0.75f + lift);

            var star = kit.MakeImage(rt, art.uiStarClay, UiKit.StarGold);
            UiKit.Anchor(star.rectTransform, 0.38f, 0.4f + lift, 0.45f, 0.55f + lift);
            var stars = kit.MakeText(rt, $"{Chapters.Stars(chapter)}/{Chapters.LevelCount(chapter) * 3}", UiKit.Body, ink, TextAnchor.MiddleLeft);
            UiKit.Anchor(stars.rectTransform, 0.47f, 0.39f + lift, 0.8f, 0.56f + lift);

            if (isCurrent)
            {
                var go = kit.Pill(rt, "Continue", UiKit.Mint, UiKit.Body, () => _onOpen?.Invoke(chapter));
                UiKit.Anchor(go, 0.38f, 0.08f, 0.84f, 0.35f);
            }
            else if (nextLocked)
            {
                var need = kit.MakeImage(rt, art.uiPill, new Color(0.78f, 0.7f, 0.66f), sliced: true);
                UiKit.Anchor(need.rectTransform, 0.38f, 0.1f, 0.82f, 0.3f);
                var t = kit.MakeText(need.transform, $"Finish chapter {chapter}", UiKit.Caption, soft);
                UiKit.Anchor(t.rectTransform, 0, 0.06f, 1, 0.94f);
            }

            if (!unlocked)
            {
                var lk = kit.MakeImage(rt, art.homeLock, new Color(0.62f, 0.54f, 0.52f));
                UiKit.Anchor(lk.rectTransform, 0.86f, 0.38f, 0.96f, 0.62f);
                return rt;
            }
            if (art.glyphChevron != null)
            {
                var chev = kit.MakeImage(rt, art.glyphChevron, ink);
                UiKit.Anchor(chev.rectTransform, 0.89f, 0.42f, 0.95f, 0.58f);
            }
            else
            {
                var chev = kit.MakeText(rt, "›", UiKit.Display, ink);
                UiKit.Anchor(chev.rectTransform, 0.87f, 0.4f, 0.97f, 0.62f);
            }

            // the whole card opens the chapter map (Continue is just the obvious target on the current one)
            var hit = rt.gameObject.AddComponent<Image>();
            hit.color = new Color(0, 0, 0, 0);
            UiKit.Tap(rt.gameObject, () => _onOpen?.Invoke(chapter));
            return rt;
        }
    }
}
