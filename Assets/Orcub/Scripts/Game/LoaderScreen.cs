using System;
using System.Collections;
using Orcub.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Orcub.Game
{
    /// <summary>
    /// Loader (approved option 1): a coral clay gear-ring that fills with mint as the game warms up, "Loading…",
    /// and one gameplay tip. Real work happens while it shows (level pack, lives, progress); it never shows for
    /// less than a beat so the hand-off from the splash doesn't flicker.
    /// </summary>
    public sealed class LoaderScreen : MonoBehaviour
    {
        const float MinSeconds = 0.9f;

        static readonly string[] Tips =
        {
            "Tip: a ring that shakes is still holding another one",
            "Tip: turn a ring until its gap reaches the clamp",
            "Tip: free rings can be flicked off the board",
            "Tip: fewer moves earn more stars",
        };

        Image _fill;
        CanvasGroup _group;

        public static LoaderScreen Show(Transform parent, UiKit kit, Action onDone)
        {
            var (canvas, safe) = UiKit.Screen("Loader", parent, 300);
            var s = canvas.gameObject.AddComponent<LoaderScreen>();
            s._group = canvas.GetComponent<CanvasGroup>();
            kit.Felt(canvas.transform).transform.SetAsFirstSibling();

            var ring = UiKit.Rect("Ring", safe);
            UiKit.Place(ring, 0.5f, 0.56f, 440, 440);
            var baseRing = kit.MakeImage(ring, kit.Art.homeGearRing, UiKit.Coral);
            UiKit.Anchor(baseRing.rectTransform, 0, 0, 1, 1);
            s._fill = kit.MakeImage(ring, kit.Art.homeGearRing, UiKit.Mint);
            UiKit.Anchor(s._fill.rectTransform, 0, 0, 1, 1);
            s._fill.type = Image.Type.Filled;
            s._fill.fillMethod = Image.FillMethod.Radial360;
            s._fill.fillOrigin = (int)Image.Origin360.Top;
            s._fill.fillClockwise = true;
            s._fill.fillAmount = 0f;

            var loading = kit.MakeText(safe, "Loading…", UiKit.Title, UiKit.Cream);
            UiKit.Place(loading.rectTransform, 0.5f, 0.36f, 800, 90);
            UiKit.Shadow(loading);
            var tip = kit.MakeText(safe, Tips[UnityEngine.Random.Range(0, Tips.Length)], UiKit.Caption,
                new Color(1f, 0.95f, 0.88f, 0.75f));
            UiKit.Place(tip.rectTransform, 0.5f, 0.29f, 860, 120);
            tip.horizontalOverflow = HorizontalWrapMode.Wrap;

            s.StartCoroutine(s.Run(onDone));
            return s;
        }

        IEnumerator Run(Action onDone)
        {
            float t = 0f;
            // Warm-up work, spread over a couple of frames.
            _ = LevelLibrary.Count;
            yield return null;
            _ = Lives.Count;
            _ = Progress.HighestUnlocked;
            yield return null;

            while (t < MinSeconds)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / MinSeconds);
                _fill.fillAmount = 1f - Mathf.Pow(1f - k, 2.2f);   // eases in like a real load
                yield return null;
            }
            _fill.fillAmount = 1f;
            Haptics.Play(Haptics.Impact.Soft, 0.6f);
            onDone?.Invoke();

            // fade out over whatever comes next
            float f = 0f;
            while (f < 1f)
            {
                f += Time.unscaledDeltaTime / 0.25f;
                _group.alpha = 1f - f;
                yield return null;
            }
            Destroy(gameObject);
        }
    }
}
