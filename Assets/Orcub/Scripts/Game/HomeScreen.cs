using System;
using System.Collections;
using Orcub.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Orcub.Game
{
    /// <summary>
    /// Home (approved nav v3, direction A). One job: get the player into their next level.
    ///   top bar     settings · lives · stars (shared TopBar)
    ///   wordmark    "orcub" + the clay gear-ring illustration
    ///   task card   the next workshop object to fix and star progress toward it (Restore when affordable)
    ///   play card   "Level N", the stars already earned on it, and the one primary button: Play
    ///   all levels  secondary pill into the chapter list
    /// </summary>
    public sealed class HomeScreen : MonoBehaviour
    {
        UiKit _kit;
        Action _onPlay, _onMap, _onSettings;
        Feedback _fx;
        CanvasGroup _group;
        RectTransform _safe, _taskArea;
        TopBar _bar;
        Text _levelLabel;
        readonly Image[] _stars = new Image[3];

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
            StartCoroutine(UiKit.FadeIn(_group));
        }

        public void Hide() => gameObject.SetActive(false);

        void Build()
        {
            var kit = _kit;
            var art = kit.Art;

            _bar = TopBar.Create(_safe, kit, art.uiCogClay, UiKit.Brass, () => _onSettings?.Invoke());

            // ---- wordmark + hero illustration
            var word = kit.MakeText(_safe, "orcub", 150, UiKit.Cream);
            UiKit.Anchor(word.rectTransform, 0f, 0.8f, 1f, 0.895f);
            UiKit.Shadow(word, 0.3f);
            if (art.homeHero != null)
            {
                var hero = kit.MakeImage(_safe, art.homeHero, Color.white);
                UiKit.Anchor(hero.rectTransform, 0.18f, 0.53f, 0.82f, 0.8f);
            }
            var tagline = kit.MakeText(_safe, "Ready to unwind?", UiKit.Title, UiKit.Cream);
            UiKit.Anchor(tagline.rectTransform, 0f, 0.48f, 1f, 0.53f);

            // ---- next workshop task
            _taskArea = UiKit.Rect("Task", _safe);
            UiKit.Anchor(_taskArea, 0.12f, 0.375f, 0.88f, 0.465f);

            // ---- play card: the single primary action
            var playArea = UiKit.Rect("PlayCard", _safe);
            UiKit.Anchor(playArea, 0.08f, 0.115f, 0.92f, 0.355f);
            var card = kit.Card(playArea);
            UiKit.Anchor(card.rectTransform, 0f, 0f, 1f, 1f);
            _levelLabel = kit.MakeText(playArea, "Level 1", UiKit.Display, UiKit.Ink);
            UiKit.Anchor(_levelLabel.rectTransform, 0f, 0.74f, 1f, 0.93f);
            for (int i = 0; i < 3; i++)
            {
                var socket = kit.MakeImage(playArea, art.starSocket != null ? art.starSocket : art.uiStarClay,
                    art.starSocket != null ? Color.white : UiKit.StarTan);
                float x0 = 0.26f + i * 0.165f;
                UiKit.Anchor(socket.rectTransform, x0, 0.47f, x0 + 0.15f, 0.74f);
                _stars[i] = kit.MakeImage(socket.transform, art.uiStarClay, UiKit.StarGold);
                UiKit.Anchor(_stars[i].rectTransform, 0.1f, 0.1f, 0.9f, 0.9f);
            }
            var play = kit.Pill(playArea, "Play", UiKit.Mint, 96, () => _onPlay?.Invoke());
            UiKit.Anchor(play, 0.06f, 0.09f, 0.94f, 0.44f);

            // ---- secondary: every level, by chapter
            var all = kit.Pill(_safe, "All levels", UiKit.CardCream, UiKit.Body, () => _onMap?.Invoke(),
                art.glyphPath, Color.white);
            UiKit.Anchor(all, 0.22f, 0.025f, 0.78f, 0.09f);
        }

        public void Refresh()
        {
            _bar?.Refresh();
            int frontier = Chapters.Frontier;
            _levelLabel.text = $"Level {frontier + 1}";
            int best = Progress.BestStars(frontier);
            for (int i = 0; i < 3; i++) _stars[i].enabled = i < best;
            BuildTask();
        }

        void BuildTask()
        {
            for (int i = _taskArea.childCount - 1; i >= 0; i--) Destroy(_taskArea.GetChild(i).gameObject);
            var kit = _kit;
            var art = kit.Art;
            var card = kit.Card(_taskArea, 0.6f);
            UiKit.Anchor(card.rectTransform, 0, 0, 1, 1);
            var task = Workshop.Next;
            if (task == null)
            {
                var done = kit.MakeText(_taskArea, "Workshop restored!", UiKit.Body, UiKit.Ink);
                UiKit.Anchor(done.rectTransform, 0, 0, 1, 1);
                return;
            }

            int idx = Array.IndexOf(art.workshopIds, task.Id);
            if (idx >= 0 && idx < art.workshopBroken.Length && art.workshopBroken[idx] != null)
            {
                var icon = kit.MakeImage(_taskArea, art.workshopBroken[idx], Color.white);
                UiKit.Anchor(icon.rectTransform, 0.04f, 0.1f, 0.27f, 0.9f);
            }
            var divider = kit.Divider(_taskArea);
            UiKit.Anchor(divider.rectTransform, 0.305f, 0.2f, 0.31f, 0.8f);

            var name = kit.MakeText(_taskArea, task.Name, UiKit.Body, UiKit.Ink, TextAnchor.MiddleLeft);
            UiKit.Anchor(name.rectTransform, 0.35f, 0.5f, 0.97f, 0.92f);

            int have = Mathf.Min(StarBank.Available, task.Cost);
            if (have >= task.Cost)
            {
                var restore = kit.Pill(_taskArea, $"Restore  {task.Cost}", UiKit.Mint, UiKit.Label, () => Restore(task),
                    art.uiStarClay, UiKit.StarGold);
                UiKit.Anchor(restore, 0.35f, 0.1f, 0.93f, 0.48f);
            }
            else
            {
                var star = kit.MakeImage(_taskArea, art.uiStarClay, UiKit.StarGold);
                UiKit.Anchor(star.rectTransform, 0.35f, 0.12f, 0.43f, 0.46f);
                var progress = kit.MakeText(_taskArea, $"{have}/{task.Cost} stars", UiKit.Label,
                    new Color(UiKit.Ink.r, UiKit.Ink.g, UiKit.Ink.b, 0.75f), TextAnchor.MiddleLeft);
                UiKit.Anchor(progress.rectTransform, 0.45f, 0.08f, 0.97f, 0.5f);
            }
        }

        // ------------------------------------------------------------------ status card (welcome back / offline)

        RectTransform _status;
        Coroutine _statusRun;

        /// <summary>
        /// Temporary nonmodal card in the workshop strip (approved welcome_back_B / loader_v2_offline_B):
        /// icon | title over one line (optionally ending in a star). The workshop task returns afterwards,
        /// and nothing else on home moves.
        /// </summary>
        public void ShowStatus(Action<RectTransform> icon, string title, string line, bool lineStar, float seconds = 4f)
        {
            if (_statusRun != null) StopCoroutine(_statusRun);
            if (_status != null) Destroy(_status.gameObject);
            var kit = _kit;
            _status = UiKit.Rect("Status", _safe);
            UiKit.Anchor(_status, _taskArea.anchorMin.x, _taskArea.anchorMin.y, _taskArea.anchorMax.x, _taskArea.anchorMax.y);
            var card = kit.Card(_status, 0.6f);
            UiKit.Anchor(card.rectTransform, 0, 0, 1, 1);
            var iconArea = UiKit.Rect("Icon", _status);
            UiKit.Anchor(iconArea, 0.04f, 0.1f, 0.27f, 0.9f);
            icon?.Invoke(iconArea);
            var divider = kit.Divider(_status);
            UiKit.Anchor(divider.rectTransform, 0.305f, 0.2f, 0.31f, 0.8f);
            // title over line as one block, centred on the card face (the lip sits below it)
            var name = kit.MakeText(_status, title, UiKit.Body, UiKit.Ink, TextAnchor.LowerLeft);
            UiKit.Anchor(name.rectTransform, 0.35f, 0.53f, 0.97f, 0.9f);
            var sub = kit.MakeText(_status, line, UiKit.Label, UiKit.Ink, TextAnchor.UpperLeft);
            UiKit.Anchor(sub.rectTransform, 0.35f, 0.12f, 0.97f, 0.5f);
            if (lineStar)
            {
                var star = kit.MakeImage(_status, kit.Art.uiStarClay, UiKit.StarGold);
                var srt = star.rectTransform;
                srt.anchorMin = srt.anchorMax = new Vector2(0.35f, 0.5f);
                srt.pivot = new Vector2(0f, 1f);
                srt.sizeDelta = new Vector2(46f, 46f);
                srt.anchoredPosition = new Vector2(sub.preferredWidth + 8f, 0f);
            }
            _statusRun = StartCoroutine(StatusLife(seconds));
        }

        // Leaving home ends the status card early; the workshop strip is always intact on return.
        void OnDisable()
        {
            _statusRun = null;
            if (_status != null) { Destroy(_status.gameObject); _status = null; }
            var task = _taskArea != null ? _taskArea.GetComponent<CanvasGroup>() : null;
            if (task != null) { task.alpha = 1f; task.blocksRaycasts = true; }
        }

        IEnumerator StatusLife(float seconds)
        {
            var task = _taskArea.GetComponent<CanvasGroup>();
            if (task == null) task = _taskArea.gameObject.AddComponent<CanvasGroup>();
            var status = _status.gameObject.AddComponent<CanvasGroup>();
            status.blocksRaycasts = false;
            float t = 0f;
            while (t < 1f)   // swap in: ease-out 220ms
            {
                t += Time.unscaledDeltaTime / 0.22f;
                float e = UiKit.EaseOut(Mathf.Clamp01(t));
                status.alpha = e;
                task.alpha = 1f - e;
                yield return null;
            }
            task.blocksRaycasts = false;
            yield return new WaitForSecondsRealtime(seconds);
            task.blocksRaycasts = true;
            t = 0f;
            while (t < 1f)   // back to the workshop task, a touch quicker
            {
                t += Time.unscaledDeltaTime / 0.18f;
                float e = UiKit.EaseOut(Mathf.Clamp01(t));
                status.alpha = 1f - e;
                task.alpha = e;
                yield return null;
            }
            Destroy(_status.gameObject);
            _status = null;
            _statusRun = null;
        }

        void Restore(WorkshopTask task)
        {
            if (!Workshop.TryRestore(task.Id)) return;
            _fx?.Release();
            Haptics.Play(Haptics.Impact.Medium, 1f);
            Refresh();
            StartCoroutine(Pop(_taskArea));
        }

        static IEnumerator Pop(Transform t)
        {
            float k = 0f;
            while (k < 1f)
            {
                k += Time.unscaledDeltaTime / 0.35f;
                float s = 1f + 0.06f * Mathf.Sin(Mathf.Clamp01(k) * Mathf.PI);
                t.localScale = new Vector3(s, s, 1f);
                yield return null;
            }
            t.localScale = Vector3.one;
        }
    }
}
