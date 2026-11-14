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
        const float CardHeight = 360f, Gap = 34f;

        UiKit _kit;
        Action<int> _onOpen;
        Action _onBack;
        CanvasGroup _group;
        RectTransform _content;
        ScrollRect _scroll;
        TopBar _bar;
        Text _subtitle;

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
            view.gameObject.AddComponent<RectMask2D>();
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
        }

        void Populate()
        {
            for (int i = _content.childCount - 1; i >= 0; i--) Destroy(_content.GetChild(i).gameObject);
            int count = Chapters.Count;
            int earned = 0;
            for (int c = 0; c < count; c++) earned += Chapters.Stars(c);
            _subtitle.text = $"{LevelLibrary.Count} levels · {earned}/{LevelLibrary.Count * 3} stars";
            _content.sizeDelta = new Vector2(0f, count * (CardHeight + Gap) + 80f);
            int current = Chapters.Of(Chapters.Frontier);
            for (int c = 0; c < count; c++) Card(c, c == current);
            _scroll.verticalNormalizedPosition = 1f;
        }

        void Card(int chapter, bool isCurrent)
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
            var cap = kit.MakeText(rt, $"Chapter {chapter + 1} · Levels {Chapters.First(chapter) + 1}–{Chapters.Last(chapter) + 1}",
                UiKit.Caption, soft, TextAnchor.MiddleLeft);
            UiKit.Anchor(cap.rectTransform, 0.38f, 0.76f, 0.98f, 0.92f);
            var name = kit.MakeText(rt, Chapters.Name(chapter), 54, ink, TextAnchor.MiddleLeft);
            UiKit.Anchor(name.rectTransform, 0.38f, 0.56f, 0.86f, 0.76f);

            var star = kit.MakeImage(rt, art.uiStarClay, UiKit.StarGold);
            UiKit.Anchor(star.rectTransform, 0.38f, 0.38f, 0.45f, 0.55f);
            var stars = kit.MakeText(rt, $"{Chapters.Stars(chapter)}/{Chapters.LevelCount(chapter) * 3}", UiKit.Body, ink, TextAnchor.MiddleLeft);
            UiKit.Anchor(stars.rectTransform, 0.47f, 0.37f, 0.8f, 0.56f);

            if (isCurrent)
            {
                var go = kit.Pill(rt, "Continue", UiKit.Mint, UiKit.Body, () => _onOpen?.Invoke(chapter));
                UiKit.Anchor(go, 0.38f, 0.11f, 0.84f, 0.33f);
            }
            else if (nextLocked)
            {
                var need = kit.MakeImage(rt, art.uiPill, new Color(0.78f, 0.7f, 0.66f), sliced: true);
                UiKit.Anchor(need.rectTransform, 0.38f, 0.12f, 0.82f, 0.31f);
                var t = kit.MakeText(need.transform, $"Finish chapter {chapter}", UiKit.Caption, soft);
                UiKit.Anchor(t.rectTransform, 0, 0.06f, 1, 0.94f);
            }

            if (!unlocked)
            {
                var lk = kit.MakeImage(rt, art.homeLock, new Color(0.62f, 0.54f, 0.52f));
                UiKit.Anchor(lk.rectTransform, 0.86f, 0.38f, 0.96f, 0.62f);
                return;
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
        }
    }
}
