using System;
using System.Collections;
using System.Collections.Generic;
using Orcub.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Orcub.Game
{
    /// <summary>
    /// Home (approved direction A, "Workshop"), kept deliberately sparse:
    ///   top bar   - lives (+ time to next life), star balance, settings
    ///   centre    - the clay workbench; each object is broken until restored with stars
    ///   task card - the single next task, its star cost, and Restore (or how many stars are still needed)
    ///   bottom    - one big PLAY pill for the next level, and an "All levels" link to the map
    /// </summary>
    public sealed class HomeScreen : MonoBehaviour
    {
        UiKit _kit;
        Action _onPlay, _onMap, _onSettings;
        Feedback _fx;
        CanvasGroup _group;
        RectTransform _safe, _scene, _taskArea;
        Text _lives, _livesTimer, _stars, _playLabel;
        readonly Dictionary<string, Image> _props = new Dictionary<string, Image>();
        float _refresh;

        public static HomeScreen Create(Transform parent, UiKit kit, Feedback fx, Action onPlay, Action onMap, Action onSettings)
        {
            var (canvas, safe) = UiKit.Screen("Home", parent, 200);
            var h = canvas.gameObject.AddComponent<HomeScreen>();
            h._kit = kit;
            h._fx = fx;
            h._onPlay = onPlay;
            h._onMap = onMap;
            h._onSettings = onSettings;
            h._group = canvas.GetComponent<CanvasGroup>();
            h._safe = safe;
            kit.Felt(canvas.transform).transform.SetAsFirstSibling();
            h.Build();
            h.Refresh();
            return h;
        }

        public bool Visible => gameObject.activeSelf;

        public void Show()
        {
            gameObject.SetActive(true);
            Refresh();
            StartCoroutine(Fade(0f, 1f, 0.22f));
        }

        public void Hide() => gameObject.SetActive(false);

        IEnumerator Fade(float from, float to, float seconds)
        {
            float t = 0f;
            while (t < 1f)
            {
                t += Time.unscaledDeltaTime / seconds;
                _group.alpha = Mathf.Lerp(from, to, t);
                yield return null;
            }
        }

        void Build()
        {
            var kit = _kit;
            var art = kit.Art;

            // ---- top bar
            var bar = kit.MakeImage(_safe, art.uiSlab, UiKit.Slab, sliced: true).rectTransform;
            UiKit.Anchor(bar, 0.04f, 0.905f, 0.96f, 0.975f);

            var heart = kit.MakeImage(bar, art.uiHeartClay, UiKit.HeartRed);
            UiKit.Anchor(heart.rectTransform, 0.04f, 0.08f, 0.16f, 0.92f);
            _lives = kit.MakeText(heart.transform, "5", UiKit.Label, UiKit.Cream);
            UiKit.Anchor(_lives.rectTransform, 0, 0.1f, 1, 0.9f);
            _livesTimer = kit.MakeText(bar, "Full", UiKit.Caption, UiKit.Ink, TextAnchor.MiddleLeft);
            UiKit.Anchor(_livesTimer.rectTransform, 0.18f, 0f, 0.4f, 1f);

            var star = kit.MakeImage(bar, art.uiStarClay, UiKit.StarGold);
            UiKit.Anchor(star.rectTransform, 0.44f, 0.12f, 0.54f, 0.88f);
            _stars = kit.MakeText(bar, "0", UiKit.Body, UiKit.Ink, TextAnchor.MiddleLeft);
            UiKit.Anchor(_stars.rectTransform, 0.56f, 0f, 0.76f, 1f);

            var settings = kit.RoundButton(bar, "Settings", art.uiCogClay, UiKit.CardCream, UiKit.Ink, () => _onSettings?.Invoke());
            UiKit.Anchor(settings, 0.84f, 0.04f, 0.97f, 0.96f);
            settings.gameObject.AddComponent<AspectRatioFitter>().aspectMode = AspectRatioFitter.AspectMode.HeightControlsWidth;

            // ---- workshop scene (laid out from the Blender kit's reference box)
            _scene = UiKit.Rect("Workshop", _safe);
            UiKit.Anchor(_scene, 0.03f, 0.425f, 0.97f, 0.895f);
            var fit = _scene.gameObject.AddComponent<AspectRatioFitter>();
            fit.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            fit.aspectRatio = art.workshopBox.x / Mathf.Max(1f, art.workshopBox.y);
            if (art.workshopBench != null) AddProp("bench", art.workshopBench, art.workshopBenchPos, art.workshopBenchSize, null);
            for (int i = 0; i < art.workshopIds.Length; i++)
                AddProp(art.workshopIds[i], art.workshopRestored[i], art.workshopPos[i], art.workshopSize[i], art.workshopBroken[i]);

            // ---- task card
            _taskArea = UiKit.Rect("Task", _safe);
            UiKit.Anchor(_taskArea, 0.08f, 0.285f, 0.92f, 0.39f);

            // ---- play
            var levelTag = kit.MakeImage(_safe, art.uiSlab, UiKit.Slab, sliced: true).rectTransform;
            UiKit.Anchor(levelTag, 0.32f, 0.205f, 0.68f, 0.25f);
            _playLabel = kit.MakeText(levelTag, "Level 1", UiKit.Label, UiKit.Ink);
            UiKit.Anchor(_playLabel.rectTransform, 0, 0, 1, 1);
            var play = kit.Pill(_safe, "Play", UiKit.Mint, UiKit.Display, () => _onPlay?.Invoke());
            UiKit.Anchor(play, 0.12f, 0.09f, 0.88f, 0.205f);
            var map = kit.Link(_safe, "All levels", UiKit.Cream, () => _onMap?.Invoke());
            UiKit.Anchor(map, 0.3f, 0.015f, 0.7f, 0.08f);
        }

        /// <summary>Place a workshop piece using the kit's layout (centre + size in the reference box).</summary>
        void AddProp(string id, Sprite sprite, Vector2 pos, Vector2 size, Sprite broken)
        {
            if (sprite == null) return;
            var box = _kit.Art.workshopBox;
            var img = _kit.MakeImage(_scene, sprite, Color.white);
            Vector2 c = new Vector2(pos.x / box.x, pos.y / box.y);
            Vector2 half = new Vector2(size.x / box.x, size.y / box.y) * 0.5f;
            UiKit.Anchor(img.rectTransform, c.x - half.x, c.y - half.y, c.x + half.x, c.y + half.y);
            if (broken != null) _props[id] = img;
        }

        void Update()
        {
            _refresh -= Time.unscaledDeltaTime;
            if (_refresh > 0f) return;
            _refresh = 1f;
            int lives = Lives.Count;
            _lives.text = lives.ToString();
            int s = Lives.SecondsToNext;
            _livesTimer.text = lives >= Lives.Max ? "Full" : $"{Mathf.CeilToInt(s / 60f)}m";
        }

        public void Refresh()
        {
            var art = _kit.Art;
            _stars.text = StarBank.Available.ToString();
            int frontier = Mathf.Clamp(Progress.HighestUnlocked, 0, Mathf.Max(0, LevelLibrary.Count - 1));
            _playLabel.text = $"Level {frontier + 1}";
            for (int i = 0; i < art.workshopIds.Length; i++)
            {
                if (!_props.TryGetValue(art.workshopIds[i], out var img)) continue;
                img.sprite = Workshop.IsRestored(art.workshopIds[i]) ? art.workshopRestored[i] : art.workshopBroken[i];
            }
            BuildTask();
            _refresh = 0f;
        }

        void BuildTask()
        {
            for (int i = _taskArea.childCount - 1; i >= 0; i--) Destroy(_taskArea.GetChild(i).gameObject);
            var kit = _kit;
            var task = Workshop.Next;
            var card = kit.Card(_taskArea);
            UiKit.Anchor(card.rectTransform, 0, 0, 1, 1);
            if (task == null)
            {
                var done = kit.MakeText(_taskArea, "Workshop restored!", UiKit.Body, UiKit.Ink);
                UiKit.Anchor(done.rectTransform, 0, 0, 1, 1);
                return;
            }
            var name = kit.MakeText(_taskArea, task.Name, UiKit.Body, UiKit.Ink, TextAnchor.MiddleLeft);
            UiKit.Anchor(name.rectTransform, 0.07f, 0f, 0.55f, 1f);
            bool canAfford = StarBank.Available >= task.Cost;
            if (canAfford)
            {
                var btn = kit.Pill(_taskArea, $"{task.Cost}", UiKit.Mint, UiKit.Body, () => Restore(task), kit.Art.uiStarClay, UiKit.StarGold);
                UiKit.Anchor(btn, 0.58f, 0.16f, 0.95f, 0.84f);
            }
            else
            {
                var need = kit.MakeText(_taskArea, $"{task.Cost - StarBank.Available} more", UiKit.Caption, UiKit.Dim, TextAnchor.MiddleRight);
                UiKit.Anchor(need.rectTransform, 0.55f, 0f, 0.82f, 1f);
                var s = kit.MakeImage(_taskArea, kit.Art.uiStarClay, UiKit.StarTan);
                UiKit.Anchor(s.rectTransform, 0.83f, 0.2f, 0.95f, 0.8f);
            }
        }

        void Restore(WorkshopTask task)
        {
            if (!Workshop.TryRestore(task.Id)) return;
            _fx?.Release();
            Haptics.Play(Haptics.Impact.Medium, 1f);
            if (_props.TryGetValue(task.Id, out var img)) StartCoroutine(Pop(img.transform));
            Refresh();
        }

        static IEnumerator Pop(Transform t)
        {
            float k = 0f;
            while (k < 1f)
            {
                k += Time.unscaledDeltaTime / 0.4f;
                float s = 1f + 0.18f * Mathf.Sin(Mathf.Clamp01(k) * Mathf.PI) * (1f - k * 0.4f);
                t.localScale = new Vector3(s, s, 1f);
                yield return null;
            }
            t.localScale = Vector3.one;
        }
    }
}
