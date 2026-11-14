using System;
using Orcub.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Orcub.Game
{
    /// <summary>
    /// The one top bar for every menu screen (home, chapters, map), so lives and stars never move:
    ///   [square button: settings or back]  [ heart 5  Full  |  star 28 ]
    /// </summary>
    public sealed class TopBar : MonoBehaviour
    {
        RectTransform _root, _button, _pill;
        Text _lives, _livesLabel, _stars;
        float _refresh;

        public static TopBar Create(RectTransform safe, UiKit kit, Sprite glyph, Color glyphTint, Action onButton)
        {
            var root = UiKit.Rect("TopBar", safe);
            UiKit.Anchor(root, 0.04f, 0.918f, 0.96f, 0.985f);
            var bar = root.gameObject.AddComponent<TopBar>();
            bar._root = root;
            var art = kit.Art;

            bar._button = kit.SquareButton(root, "Button", glyph, glyphTint, onButton);
            bar._button.anchorMin = new Vector2(0f, 0f);
            bar._button.anchorMax = new Vector2(0f, 1f);
            bar._button.pivot = new Vector2(0f, 0.5f);
            bar._button.offsetMin = bar._button.offsetMax = Vector2.zero;

            bar._pill = kit.MakeImage(root, art.uiSlab, UiKit.Slab, sliced: true).rectTransform;
            UiKit.Anchor(bar._pill, 0f, 0f, 1f, 1f);

            // lives: heart with the count inside, then "Full" or the countdown
            var heart = kit.MakeImage(bar._pill, art.uiHeartClay, UiKit.HeartRed);
            UiKit.Anchor(heart.rectTransform, 0.05f, 0.1f, 0.21f, 0.9f);
            bar._lives = kit.MakeText(heart.transform, "5", UiKit.Label, UiKit.Cream);
            UiKit.Anchor(bar._lives.rectTransform, 0f, 0.1f, 1f, 0.9f);
            bar._livesLabel = kit.MakeText(bar._pill, "Full", UiKit.Body, UiKit.Ink, TextAnchor.MiddleLeft);
            UiKit.Anchor(bar._livesLabel.rectTransform, 0.24f, 0f, 0.5f, 1f);

            var divider = kit.Divider(bar._pill);
            UiKit.Anchor(divider.rectTransform, 0.535f, 0.22f, 0.54f, 0.78f);

            var star = kit.MakeImage(bar._pill, art.uiStarClay, UiKit.StarGold);
            UiKit.Anchor(star.rectTransform, 0.6f, 0.14f, 0.74f, 0.86f);
            bar._stars = kit.MakeText(bar._pill, "0", UiKit.Body, UiKit.Ink, TextAnchor.MiddleLeft);
            UiKit.Anchor(bar._stars.rectTransform, 0.76f, 0f, 0.97f, 1f);
            return bar;
        }

        public void Refresh() => _refresh = 0f;

        void Update()
        {
            // square button is as wide as the bar is tall; the pill takes the rest
            float h = _root.rect.height;
            float gap = h * 0.14f;
            _button.sizeDelta = new Vector2(h, 0f);
            _pill.offsetMin = new Vector2(h + gap, 0f);

            _refresh -= Time.unscaledDeltaTime;
            if (_refresh > 0f) return;
            _refresh = 1f;
            int lives = Lives.Count;
            _lives.text = lives.ToString();
            _livesLabel.text = lives >= Lives.Max ? "Full" : Lives.FormatTimer(Lives.SecondsToNext);
            _stars.text = StarBank.Available.ToString();
        }
    }

    /// <summary>Saga chapters: 20 levels each, unlocked in order.</summary>
    public static class Chapters
    {
        public const int Size = 20;

        static readonly string[] Names =
        {
            "The Workshop", "Clockworks", "Tool shed", "Music box", "Copper coast",
            "Tinker town", "Ratchet ridge", "Cog canyon", "Brass bay", "The grand mechanism",
        };

        public static int Count => Mathf.Max(1, (LevelLibrary.Count + Size - 1) / Size);
        public static string Name(int chapter) => Names[chapter % Names.Length];
        public static int First(int chapter) => chapter * Size;
        public static int Last(int chapter) => Mathf.Min(LevelLibrary.Count, (chapter + 1) * Size) - 1;
        public static int LevelCount(int chapter) => Last(chapter) - First(chapter) + 1;
        public static int Of(int level) => level / Size;
        public static int Frontier => Mathf.Clamp(Progress.HighestUnlocked, 0, Mathf.Max(0, LevelLibrary.Count - 1));
        public static bool Unlocked(int chapter) => First(chapter) <= Frontier;
        public static bool Complete(int chapter) => Frontier > Last(chapter);

        public static int Stars(int chapter)
        {
            int total = 0;
            for (int i = First(chapter); i <= Last(chapter); i++) total += Progress.BestStars(i);
            return total;
        }

        /// <summary>Chapter card art cycles through the four workshop objects (restored look).</summary>
        public static Sprite Art(OrcubArt art, int chapter)
        {
            int k = chapter % 4;
            if (k == 0 && art.chapterLamp != null) return art.chapterLamp;
            return k < art.workshopRestored.Length ? art.workshopRestored[k] : art.homeGearRing;
        }
    }
}
