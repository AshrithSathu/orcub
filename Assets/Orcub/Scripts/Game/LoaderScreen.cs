using System;
using System.Collections;
using Orcub.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Orcub.Game
{
    public struct BootResult
    {
        public bool Offline;
    }

    /// <summary>
    /// Boot loader (approved account_v2 loader_v2_B): small wordmark, a compact clay tray with a coral gear ring that
    /// fills with mint as real boot steps complete ("Connecting…" / "Getting your save ready"), and the quiet
    /// "You can play offline". Steps: Firebase, anonymous auth, ads consent service, server config, cloud save check.
    /// Never blocks: a hard cap of ~3 s, and no network falls straight through to a playable home.
    /// </summary>
    public sealed class LoaderScreen : MonoBehaviour
    {
        const float MinSeconds = 0.8f, MaxSeconds = 3f;
        const int Steps = 5;

        Image _fill;
        CanvasGroup _group;
        float _shown;

        public static LoaderScreen Show(Transform parent, UiKit kit, Action<BootResult> onDone)
        {
            var s = Build(parent, kit);
            s.StartCoroutine(s.Run(onDone));
            return s;
        }

        /// <summary>Editor captures: a frozen loader at a given progress.</summary>
        public static LoaderScreen ShowStatic(Transform parent, UiKit kit, float progress)
        {
            var s = Build(parent, kit);
            s._fill.fillAmount = progress;
            return s;
        }

        static LoaderScreen Build(Transform parent, UiKit kit)
        {
            var (canvas, safe) = UiKit.Screen("Loader", parent, 300);
            var s = canvas.gameObject.AddComponent<LoaderScreen>();
            s._group = canvas.GetComponent<CanvasGroup>();
            kit.Felt(canvas.transform).transform.SetAsFirstSibling();
            var art = kit.Art;

            var word = kit.MakeText(safe, "orcub", 128, UiKit.Cream);
            UiKit.Anchor(word.rectTransform, 0f, 0.715f, 1f, 0.795f);
            UiKit.Shadow(word, 0.3f);

            // ---- compact boot tray
            var tray = UiKit.Rect("Tray", safe);
            tray.anchorMin = new Vector2(0.1f, 0.335f);
            tray.anchorMax = new Vector2(0.9f, 0.335f);
            tray.pivot = new Vector2(0.5f, 0.5f);
            tray.sizeDelta = new Vector2(0f, 320f);
            AccountUi.Panel(kit, tray, UiKit.CardCream, 150f);

            var ring = UiKit.Rect("Ring", tray);
            ring.anchorMin = ring.anchorMax = new Vector2(0f, 0.5f);
            ring.sizeDelta = new Vector2(236f, 236f);
            ring.anchoredPosition = new Vector2(52f + 118f, 4f);
            var baseRing = kit.MakeImage(ring, art.homeGearRing, UiKit.Coral);
            UiKit.Anchor(baseRing.rectTransform, 0, 0, 1, 1);
            s._fill = kit.MakeImage(ring, art.homeGearRing, UiKit.Mint);
            UiKit.Anchor(s._fill.rectTransform, 0, 0, 1, 1);
            s._fill.type = Image.Type.Filled;
            s._fill.fillMethod = Image.FillMethod.Radial360;
            s._fill.fillOrigin = (int)Image.Origin360.Top;
            s._fill.fillClockwise = true;
            s._fill.fillAmount = 0f;
            if (art.mapNodeCenter != null)
            {
                var bead = kit.MakeImage(ring, art.mapNodeCenter, Color.white);
                UiKit.Anchor(bead.rectTransform, 0.33f, 0.33f, 0.67f, 0.67f);
            }

            var col = UiKit.Rect("Text", tray);
            UiKit.Anchor(col, 0f, 0f, 1f, 1f);
            col.offsetMin = new Vector2(52f + 236f + 40f, 0f);
            col.offsetMax = new Vector2(-40f, 0f);
            var title = kit.MakeText(col, "Connecting…", UiKit.Title, UiKit.Ink, TextAnchor.LowerLeft);
            UiKit.Anchor(title.rectTransform, 0f, 0.5f, 1f, 0.86f);
            var sub = kit.MakeText(col, "Getting your save ready", UiKit.Label, UiKit.Ink, TextAnchor.UpperLeft);
            UiKit.Anchor(sub.rectTransform, 0f, 0.14f, 1f, 0.46f);
            AccountUi.Fit(sub, 864f - 368f, UiKit.Label, UiKit.Caption);

            var foot = kit.MakeText(safe, "You can play offline", UiKit.Body, UiKit.Cream);
            UiKit.Anchor(foot.rectTransform, 0f, 0.19f, 1f, 0.24f);
            return s;
        }

        static int Completed()
        {
            int n = 0;
            if (AccountGateway.Boot.FirebaseReady) n++;
            if (AccountGateway.Boot.AuthDone) n++;
            if (AdsGateway.Service != null) n++;   // consent: UMP is owned and refreshed by the ads service
            if (AccountGateway.Boot.ConfigDone) n++;
            if (AccountGateway.Boot.SaveChecked) n++;
            return n;
        }

        static bool NoNetwork => Application.internetReachability == NetworkReachability.NotReachable || AccountGateway.Boot.NetworkFailed;

        IEnumerator Run(Action<BootResult> onDone)
        {
            float t = 0f;
            // Local warm-up, spread over a couple of frames.
            _ = LevelLibrary.Count;
            yield return null;
            _ = Lives.Count;
            _ = Progress.HighestUnlocked;
            yield return null;

            while (true)
            {
                t += Time.unscaledDeltaTime;
                int done = Completed();
                // the arc only reflects completed work, eased so steps read as calm progress
                _shown = Mathf.MoveTowards(_shown, done / (float)Steps, Time.unscaledDeltaTime * 2.2f);
                _fill.fillAmount = _shown;
                bool settled = Mathf.Approximately(_shown, done / (float)Steps);
                if (t >= MinSeconds && settled && (done == Steps || NoNetwork)) break;
                if (t >= MaxSeconds) break;
                yield return null;
            }
            var result = new BootResult { Offline = !AccountGateway.Boot.SaveChecked && NoNetwork };
            if (!result.Offline)
            {
                float from = _fill.fillAmount;
                yield return AccountUi.Tween(0.15f, k => _fill.fillAmount = Mathf.Lerp(from, 1f, UiKit.EaseOut(k)));
            }
            Haptics.Play(Haptics.Impact.Soft, 0.6f);
            onDone?.Invoke(result);

            // fade out over whatever comes next
            yield return AccountUi.Tween(0.22f, k => _group.alpha = 1f - UiKit.EaseIn(k));
            Destroy(gameObject);
        }
    }
}
