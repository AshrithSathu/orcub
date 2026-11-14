using System;
using UnityEngine;
using UnityEngine.UI;

namespace Orcub.Game
{
    /// <summary>
    /// Shared builder for Orcub's clay UI (screens built in code, no prefabs).
    ///
    /// Type scale (canvas units at a 1080-wide reference; ~2.7 units per iPhone point):
    ///   Display 84 (~31pt)  card / screen titles
    ///   Title   62 (~23pt)  primary buttons, hero numbers
    ///   Body    46 (~17pt)  labels, task names
    ///   Label   40 (~15pt)  secondary buttons, links
    ///   Caption 34 (~13pt)  small captions; never smaller in shipping UI
    /// Touch targets are at least 118 units (44pt).
    /// </summary>
    public sealed class UiKit
    {
        public const int Display = 84, Title = 62, Body = 46, Label = 40, Caption = 34;
        public const float MinTouch = 118f;

        public static readonly Color Ink = new Color(0.23f, 0.09f, 0.15f);
        public static readonly Color Cream = new Color(1f, 0.95f, 0.88f);
        public static readonly Color Mint = new Color(0.49f, 0.80f, 0.56f);
        public static readonly Color Coral = new Color(0.95f, 0.51f, 0.42f);
        public static readonly Color StarGold = new Color(0.97f, 0.78f, 0.30f);
        public static readonly Color StarTan = new Color(0.74f, 0.62f, 0.52f);
        public static readonly Color Slab = new Color(0.98f, 0.92f, 0.82f);
        public static readonly Color CardCream = new Color(0.99f, 0.94f, 0.85f);
        public static readonly Color HeartRed = new Color(0.95f, 0.43f, 0.37f);
        public static readonly Color Brass = new Color(0.89f, 0.70f, 0.38f);
        public static readonly Color Dim = new Color(0.55f, 0.45f, 0.50f);

        public readonly OrcubArt Art;
        public readonly Font Font;

        public UiKit(OrcubArt art)
        {
            Art = art;
            Font = art.uiFont != null ? art.uiFont : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }

        public static RectTransform Rect(string name, Transform parent)
        {
            var rt = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            return rt;
        }

        public static void Anchor(RectTransform rt, float x0, float y0, float x1, float y1)
        {
            rt.anchorMin = new Vector2(x0, y0);
            rt.anchorMax = new Vector2(x1, y1);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        /// <summary>Fixed-size rect centred on an anchor point (canvas units).</summary>
        public static void Place(RectTransform rt, float ax, float ay, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(ax, ay);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = Vector2.zero;
        }

        public Text MakeText(Transform parent, string text, int size, Color color, TextAnchor anchor = TextAnchor.MiddleCenter)
        {
            var t = Rect("Text", parent).gameObject.AddComponent<Text>();
            t.font = Font;
            t.fontSize = size;
            t.color = color;
            t.alignment = anchor;
            t.text = text;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        public Image MakeImage(Transform parent, Sprite sprite, Color color, bool sliced = false)
        {
            var img = Rect(sprite != null ? sprite.name : "Image", parent).gameObject.AddComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            img.raycastTarget = false;
            if (sliced)
            {
                img.type = UnityEngine.UI.Image.Type.Sliced;
                img.gameObject.AddComponent<RoundCaps>();
            }
            else img.preserveAspect = true;
            return img;
        }

        /// <summary>Cream clay card panel (sliced on all four sides).</summary>
        public Image Card(Transform parent)
        {
            var img = Rect("Card", parent).gameObject.AddComponent<Image>();
            img.sprite = Art.uiCard;
            img.type = UnityEngine.UI.Image.Type.Sliced;
            img.pixelsPerUnitMultiplier = 1.5f;
            img.color = CardCream;
            img.raycastTarget = false;
            return img;
        }

        /// <summary>Plum felt backdrop that always covers the screen.</summary>
        public Image Felt(Transform parent)
        {
            var img = Rect("Felt", parent).gameObject.AddComponent<Image>();
            img.sprite = Art.backgroundSprite;
            img.color = Color.white;
            img.raycastTarget = true;   // screens block touches to the board behind them
            var fit = img.gameObject.AddComponent<AspectRatioFitter>();
            fit.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
            if (Art.backgroundSprite != null)
                fit.aspectRatio = Art.backgroundSprite.rect.width / Art.backgroundSprite.rect.height;
            return img;
        }

        static void Tap(GameObject go, Action onClick, float haptic = 0.6f)
        {
            var btn = go.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(() => { Haptics.Play(Haptics.Impact.Light, haptic); onClick(); });
            go.AddComponent<PressSquash>();
        }

        /// <summary>Puffy clay pill button with an optional glyph on the left.</summary>
        public RectTransform Pill(Transform parent, string label, Color tint, int fontSize, Action onClick,
            Sprite glyph = null, Color? glyphTint = null)
        {
            var rt = Rect(label, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = Art.uiPill;
            img.type = UnityEngine.UI.Image.Type.Sliced;
            img.color = tint;
            rt.gameObject.AddComponent<RoundCaps>();
            Tap(rt.gameObject, onClick);
            var text = MakeText(rt, label, fontSize, Ink);
            if (glyph != null)
            {
                Anchor(text.rectTransform, 0.28f, 0.06f, 0.95f, 0.94f);
                var g = MakeImage(rt, glyph, glyphTint ?? Ink);
                Anchor(g.rectTransform, 0.08f, 0.22f, 0.28f, 0.80f);
            }
            else Anchor(text.rectTransform, 0f, 0.06f, 1f, 0.94f);
            return rt;
        }

        /// <summary>Round puffy clay button with a glyph; touch area never under 44pt.</summary>
        public RectTransform RoundButton(Transform parent, string name, Sprite glyph, Color tint, Color glyphTint, Action onClick)
        {
            var rt = Rect(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = Art.uiRound;
            img.color = tint;
            img.preserveAspect = true;
            Tap(rt.gameObject, onClick);
            var g = MakeImage(rt, glyph, glyphTint);
            Anchor(g.rectTransform, 0.25f, 0.25f, 0.75f, 0.75f);
            return rt;
        }

        /// <summary>Small underlined text action with a full-height touch area.</summary>
        public RectTransform Link(Transform parent, string label, Color color, Action onClick)
        {
            var rt = Rect(label, parent);
            var hit = rt.gameObject.AddComponent<Image>();
            hit.color = new Color(0, 0, 0, 0);
            Tap(rt.gameObject, onClick, 0.5f);
            var t = MakeText(rt, label, Label, color);
            Anchor(t.rectTransform, 0f, 0.2f, 1f, 1f);
            var line = MakeImage(rt, null, new Color(color.r, color.g, color.b, 0.7f));
            Anchor(line.rectTransform, 0.3f, 0.12f, 0.7f, 0.16f);
            return rt;
        }

        public static void Shadow(Graphic g, float alpha = 0.4f)
        {
            var s = g.gameObject.AddComponent<UnityEngine.UI.Shadow>();
            s.effectColor = new Color(0f, 0f, 0f, alpha);
            s.effectDistance = new Vector2(0f, -5f);
        }

        /// <summary>Full-screen overlay canvas inside the device safe area.</summary>
        public static (Canvas canvas, RectTransform safe) Screen(string name, Transform parent, int sortingOrder)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0f;
            go.AddComponent<GraphicRaycaster>();
            go.AddComponent<CanvasGroup>();
            var safe = Rect("SafeArea", go.transform);
            go.AddComponent<SafeAreaFit>().Target = safe;
            return (canvas, safe);
        }
    }

    /// <summary>Keeps a RectTransform matched to the device safe area.</summary>
    public sealed class SafeAreaFit : MonoBehaviour
    {
        public RectTransform Target;
        Rect _last;

        void Update()
        {
            var sa = UnityEngine.Screen.safeArea;
            if (Target == null || sa == _last || UnityEngine.Screen.width <= 0) return;
            _last = sa;
            Target.anchorMin = new Vector2(sa.xMin / UnityEngine.Screen.width, sa.yMin / UnityEngine.Screen.height);
            Target.anchorMax = new Vector2(sa.xMax / UnityEngine.Screen.width, sa.yMax / UnityEngine.Screen.height);
            Target.offsetMin = Target.offsetMax = Vector2.zero;
        }
    }
}
