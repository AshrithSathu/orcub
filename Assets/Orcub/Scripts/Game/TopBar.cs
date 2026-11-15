using System;
using Orcub.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Orcub.Game
{
    /// <summary>
    /// The one top bar for every menu screen (home, chapters, map), so lives and gold never move
    /// (approved shop_v1 currency_A; daily_puzzle_B adds the Daily chip on home):
    ///   [square: settings or back]  [ heart 5  Full  |  gold 120 (+) ]  [ calendar Daily ]
    /// The mint + opens the shop. A purchase plays the approved purchase_success_B burst on the counter.
    /// </summary>
    public sealed class TopBar : MonoBehaviour
    {
        RectTransform _root, _button, _pill, _heart, _divider, _gold, _plus, _chip, _added;
        Text _lives, _livesLabel, _count, _addedText;
        Image _dailyDot;
        UiKit _kit;
        float _refresh, _lastW = -1f;
        long _shown = -1, _target;
        float _countAnim = 1f;
        long _animFrom;

        /// <summary>Every visible bar plays the counter burst for a confirmed purchase (balance before, after).</summary>
        public static event Action<long, long> GoldAdded;
        public static void CelebrateGold(long before, long after) => GoldAdded?.Invoke(before, after);

        /// <summary>Home's Daily chip; null keeps it hidden. <see cref="DailyBadge"/> lights the dot.</summary>
        public static Func<bool> DailyBadge;

        public static TopBar Create(RectTransform safe, UiKit kit, Sprite glyph, Color glyphTint, Action onButton,
            Action onDaily = null)
        {
            var root = UiKit.Rect("TopBar", safe);
            UiKit.Anchor(root, 0.04f, 0.918f, 0.96f, 0.985f);
            var bar = root.gameObject.AddComponent<TopBar>();
            bar._root = root;
            bar._kit = kit;
            var art = kit.Art;

            bar._button = kit.SquareButton(root, "Button", glyph, glyphTint, onButton);
            bar._button.anchorMin = new Vector2(0f, 0f);
            bar._button.anchorMax = new Vector2(0f, 1f);
            bar._button.pivot = new Vector2(0f, 0.5f);
            bar._button.offsetMin = bar._button.offsetMax = Vector2.zero;

            bar._pill = kit.MakeImage(root, art.uiSlab, UiKit.Slab, sliced: true).rectTransform;
            UiKit.Anchor(bar._pill, 0f, 0f, 1f, 1f);
            var face = UiKit.Rect("Face", bar._pill);
            UiKit.Anchor(face, 0f, 0.1f, 1f, 1f);   // optical centre: the slab's lip sits under the face

            // lives: heart with the count inside, then "Full" or the countdown
            var heart = kit.MakeImage(face, art.uiHeartClay, UiKit.HeartRed);
            bar._heart = heart.rectTransform;
            bar._lives = kit.MakeText(heart.transform, "5", UiKit.Label, UiKit.Cream);
            UiKit.Anchor(bar._lives.rectTransform, 0f, 0.1f, 1f, 0.9f);
            bar._livesLabel = kit.MakeText(face, "Full", UiKit.Body, UiKit.Ink, TextAnchor.MiddleLeft);

            bar._divider = kit.Divider(face).rectTransform;

            // gold: pyramid bars, the count, and the mint + (opens the shop)
            var gold = kit.MakeImage(face, art.goldBar != null ? art.GoldBarTight : art.uiCogClay, art.goldBar != null ? Color.white : UiKit.Brass);
            gold.preserveAspect = true;
            bar._gold = gold.rectTransform;
            bar._count = kit.MakeText(face, "0", UiKit.Body, UiKit.Ink, TextAnchor.MiddleLeft);
            bar._plus = kit.RoundButton(face, "Shop", null, UiKit.Mint, UiKit.Cream, () => ShopFlow.OpenShop());
            var plusGlyph = bar._plus.GetChild(0).gameObject;   // RoundButton's empty glyph image
            Destroy(plusGlyph);
            var plus = kit.MakeText(bar._plus, "+", 72, UiKit.Cream);
            UiKit.Anchor(plus.rectTransform, 0f, 0.06f, 1f, 1f);
            UiKit.Shadow(plus, 0.18f);

            // the whole gold group is one 44pt+ target for the shop
            var goldHit = UiKit.Rect("GoldHit", face);
            goldHit.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0);
            UiKit.Tap(goldHit.gameObject, () => ShopFlow.OpenShop(), 0.4f);
            goldHit.SetSiblingIndex(0);
            bar._goldHit = goldHit;

            if (onDaily != null)
            {
                var chip = UiKit.Rect("Daily", root);
                bar._chip = chip;
                var slab = kit.MakeImage(chip, art.uiSlab, UiKit.Slab, sliced: true);
                slab.raycastTarget = true;
                UiKit.Anchor(slab.rectTransform, 0, 0, 1, 1);
                UiKit.Tap(chip.gameObject, onDaily);
                var cface = UiKit.Rect("Face", chip);
                UiKit.Anchor(cface, 0f, 0.1f, 1f, 1f);
                var cal = kit.MakeImage(cface, FeatureArt.Calendar, Color.white);
                cal.preserveAspect = true;
                UiKit.Anchor(cal.rectTransform, 0.08f, 0.12f, 0.42f, 0.9f);
                var label = kit.MakeText(cface, "Daily", UiKit.Label, UiKit.Ink, TextAnchor.MiddleLeft);
                UiKit.Anchor(label.rectTransform, 0.45f, 0f, 1f, 1f);
                bar._dailyDot = kit.MakeImage(chip, art.uiRound, UiKit.Coral);
                UiKit.Anchor(bar._dailyDot.rectTransform, 0.84f, 0.7f, 1.0f, 1.04f);
                bar._dailyDot.gameObject.AddComponent<AspectRatioFitter>().aspectMode = AspectRatioFitter.AspectMode.HeightControlsWidth;
            }

            // "+600" chip under the counter for a confirmed purchase
            var added = UiKit.Rect("Added", bar._pill);
            bar._added = added;
            var addedPill = kit.MakeImage(added, art.uiPill, UiKit.CardCream, sliced: true);
            UiKit.Anchor(addedPill.rectTransform, 0, 0, 1, 1);
            bar._addedText = kit.MakeText(added, "+0", UiKit.Caption, UiKit.Ink);
            UiKit.Anchor(bar._addedText.rectTransform, 0, 0.06f, 1, 0.94f);
            added.gameObject.SetActive(false);

            GoldAdded += bar.OnGoldAdded;
            return bar;
        }

        RectTransform _goldHit;

        public void Refresh() => _refresh = 0f;

        void OnDestroy() => GoldAdded -= OnGoldAdded;

        // a burst interrupted by leaving the screen never leaves its chip or bars behind
        void OnDisable()
        {
            if (_added != null) _added.gameObject.SetActive(false);
            foreach (var b in _bits) if (b != null) Destroy(b.gameObject);
            _bits.Clear();
            if (_countAnim < 1f) { _countAnim = 1f; _shown = -1; }
        }

        readonly System.Collections.Generic.List<RectTransform> _bits = new System.Collections.Generic.List<RectTransform>();

        static void Place(RectTransform rt, float x, float w, float h, float yOffset = 0f)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, yOffset);
        }

        float TextWidth(Text t, string sample)
        {
            var s = t.text;
            t.text = sample;
            float w = t.preferredWidth;
            t.text = s;
            return w;
        }

        /// <summary>Lays the row out in canvas units: lives group on the left, gold group on the right.</summary>
        void Layout()
        {
            float H = _root.rect.height, W = _root.rect.width;
            float gap = H * 0.14f;
            _button.sizeDelta = new Vector2(H, 0f);
            float chipW = _chip != null ? H * 1.5f : 0f;
            if (_chip != null)
            {
                _chip.anchorMin = new Vector2(1f, 0f);
                _chip.anchorMax = new Vector2(1f, 1f);
                _chip.pivot = new Vector2(1f, 0.5f);
                _chip.sizeDelta = new Vector2(chipW, 0f);
                _chip.anchoredPosition = Vector2.zero;
            }
            _pill.offsetMin = new Vector2(H + gap, 0f);
            _pill.offsetMax = new Vector2(_chip != null ? -(chipW + gap) : 0f, 0f);

            float pw = W - H - gap - (_chip != null ? chipW + gap : 0f);
            float fh = H * 0.9f;                  // face height
            float pad = H * 0.16f;
            float hs = fh * 0.8f;
            Place(_heart, pad, hs, hs);
            float labelX = pad + hs + H * 0.1f;
            float labelW = Mathf.Max(TextWidth(_livesLabel, "Full"), _livesLabel.preferredWidth) + 8f;
            Place(_livesLabel.rectTransform, labelX, labelW, fh);

            float ps = fh * (_chip != null ? 0.7f : 0.78f);
            float plusX = pw - pad * 0.7f - ps;
            Place(_plus, plusX, ps, ps);
            float countW = Mathf.Max(TextWidth(_count, "000"), _count.preferredWidth) + 6f;
            float countX = plusX - H * 0.12f - countW;
            Place(_count.rectTransform, countX, countW, fh);
            // with the Daily chip the pill is narrower (approved daily_puzzle_B): a smaller gold icon keeps "Full" clear
            float gh = fh * (_chip != null ? 0.44f : 0.56f), gw = gh * 1.7f;
            float goldX = countX - H * 0.1f - gw;
            float leftEnd0 = labelX + labelW, minGap = H * 0.3f;
            if (goldX < leftEnd0 + minGap)
            {   // a narrow pill (Daily chip): the gold icon gives way so "Full" / the timer never collides
                gw = Mathf.Max(H * 0.42f, gw - (leftEnd0 + minGap - goldX));
                gh = gw / 1.7f;
                goldX = countX - H * 0.06f - gw;
            }
            Place(_gold, goldX, gw, gh);
            Place(_goldHit, goldX - 10f, plusX + ps - goldX + 20f, fh);

            float leftEnd = labelX + labelW, divX = (leftEnd + goldX) * 0.5f;
            Place(_divider, divX - 2f, 4f, fh * 0.56f);

            // the "+N" chip hangs under the counter
            _added.anchorMin = _added.anchorMax = new Vector2(0f, 0f);
            _added.pivot = new Vector2(0.5f, 1f);
            _added.sizeDelta = new Vector2(Mathf.Max(150f, _addedText.preferredWidth + 56f), H * 0.5f);
            _added.anchoredPosition = new Vector2(countX + countW * 0.5f - 10f, H * 0.18f);
        }

        void Update()
        {
            if (!Mathf.Approximately(_lastW, _root.rect.width)) { _lastW = _root.rect.width; Layout(); }

            if (_countAnim < 1f)
            {
                _countAnim = Mathf.Min(1f, _countAnim + Time.unscaledDeltaTime / 0.9f);
                long v = _animFrom + (long)Math.Round((_target - _animFrom) * UiKit.EaseOut(_countAnim));
                if (v != _shown) { _shown = v; _count.text = ShopCatalog.Gold(v); Layout(); }
            }

            _refresh -= Time.unscaledDeltaTime;
            if (_refresh > 0f) return;
            _refresh = 0.5f;
            int lives = Lives.Count;
            _lives.text = lives.ToString();
            string label = lives >= Lives.Max ? "Full" : Lives.FormatTimer(Lives.SecondsToNext);
            bool relayout = label != _livesLabel.text;
            _livesLabel.text = label;
            long gold = ShopGateway.Current?.Gold ?? 0;
            if (_countAnim >= 1f && gold != _shown) { _shown = _target = gold; _count.text = ShopCatalog.Gold(gold); relayout = true; }
            if (_dailyDot != null) _dailyDot.enabled = DailyBadge?.Invoke() == true;
            if (relayout) Layout();
        }

        void OnGoldAdded(long before, long after)
        {
            if (!isActiveAndEnabled || after <= before) return;
            _animFrom = before;
            _target = after;
            _shown = before;
            _countAnim = 0f;
            _addedText.text = "+" + ShopCatalog.Gold(after - before);
            Layout();
            StartCoroutine(Burst());
        }

        System.Collections.IEnumerator Burst()
        {
            _added.gameObject.SetActive(true);
            var g = _added.GetComponent<CanvasGroup>();
            if (g == null) g = _added.gameObject.AddComponent<CanvasGroup>();
            var art = _kit.Art;
            var bits = new System.Collections.Generic.List<(RectTransform rt, Vector2 v, float spin)>();
            Vector2 origin = _gold.anchoredPosition + new Vector2(_gold.sizeDelta.x * 0.5f, 0f);
            for (int i = 0; i < 7 && art.goldParticle != null; i++)
            {
                var p = _kit.MakeImage(_pill, art.goldParticle, Color.white);
                var rt = p.rectTransform;
                Place(rt, origin.x, 64f, 64f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                float a = Mathf.Lerp(20f, 160f, i / 6f) * Mathf.Deg2Rad;
                _bits.Add(rt);
                bits.Add((rt, new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * UnityEngine.Random.Range(380f, 520f), UnityEngine.Random.Range(-240f, 240f)));
            }
            Haptics.Notify(Haptics.Notice.Success);
            float t = 0f;
            while (t < 1.6f)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / 0.7f);
                foreach (var (rt, v, spin) in bits)
                {
                    if (rt == null) continue;
                    rt.anchoredPosition = new Vector2(origin.x, 0f) + v * UiKit.EaseOut(k) * 0.5f + new Vector2(0f, -200f * k * k);
                    rt.localRotation = Quaternion.Euler(0, 0, spin * k);
                    rt.GetComponent<Image>().color = new Color(1, 1, 1, 1f - Mathf.Clamp01((t - 0.45f) / 0.3f));
                }
                g.alpha = t < 0.15f ? t / 0.15f : t > 1.3f ? 1f - (t - 1.3f) / 0.3f : 1f;
                _added.localScale = Vector3.one * (t < 0.2f ? Mathf.Lerp(0.8f, 1f, UiKit.EaseOut(t / 0.2f)) : 1f);
                yield return null;
            }
            foreach (var (rt, _, _) in bits) if (rt != null) Destroy(rt.gameObject);
            _added.gameObject.SetActive(false);
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

        /// <summary>
        /// Chapters shown: the infinite saga is windowed at the frontier's chapter plus the next (locked) one,
        /// never fewer than the ten hand-made chapters. Not a progression limit.
        /// </summary>
        public static int Count => Mathf.Max(LevelLibrary.CuratedCount / Size, Of(Frontier) + 2);
        public static string Name(int chapter) =>
            chapter < Names.Length ? Names[chapter] : LevelLibrary.ChapterInfo(chapter).Name;
        public static int First(int chapter) => chapter * Size;
        public static int Last(int chapter) => First(chapter) + Size - 1;
        public static int LevelCount(int chapter) => Size;
        public static int Of(int level) => Mathf.Max(0, level) / Size;
        public static int Frontier => Mathf.Max(0, Progress.HighestUnlocked);
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
