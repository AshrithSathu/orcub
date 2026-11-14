using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace Orcub.Game
{
    /// <summary>
    /// Building blocks for the approved account screens (account_v1 / account_v2), on top of <see cref="UiKit"/>.
    /// Layouts are vertical stacks measured in canvas units (1080-wide reference) so every state of a card keeps
    /// identical geometry: spinners and errors swap inside reserved rects and nothing else moves.
    /// </summary>
    public static class AccountUi
    {
        /// <summary>Same side margin as the top bar (TopBar anchors 0.04..0.96).</summary>
        public const float SideMargin = 0.04f;
        public const float Inset = 46f;   // card padding

        public static readonly Color Scrim = new Color(0.05f, 0.01f, 0.03f, 1f);
        public static readonly Color InkSoft = new Color(UiKit.Ink.r, UiKit.Ink.g, UiKit.Ink.b, 0.82f);
        public static readonly Color DeleteRed = new Color(0.86f, 0.33f, 0.25f);
        public static readonly Color TileTan = new Color(0.72f, 0.62f, 0.54f);
        public static readonly Color MintTile = new Color(0.62f, 0.86f, 0.64f);
        public static readonly Color MintStrip = new Color(0.78f, 0.91f, 0.74f);
        public static readonly Color GoogleInk = new Color(0.12f, 0.12f, 0.12f);
        public static readonly Color SpinnerGrey = new Color(0.56f, 0.56f, 0.58f);

        static Font _sans;
        /// <summary>System sans for the Google button label (branding guidance: never the game font).</summary>
        public static Font Sans
        {
            get
            {
                if (_sans == null)
                {
                    try { _sans = Font.CreateDynamicFontFromOSFont(new[] { "Roboto-Medium", "Roboto", ".SFUI-Medium", "SF Pro Text", "Helvetica Neue", "Helvetica", "Arial" }, 46); }
                    catch (Exception) { _sans = null; }
                    if (_sans == null) _sans = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                }
                return _sans;
            }
        }

        // ------------------------------------------------------------------ layout

        /// <summary>Top-anchored child: <paramref name="top"/> units down from the parent's top edge.</summary>
        public static RectTransform Row(RectTransform parent, string name, float top, float height, float left, float right)
        {
            var rt = UiKit.Rect(name, parent);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(left, -(top + height));
            rt.offsetMax = new Vector2(-right, -top);
            return rt;
        }

        /// <summary>Text whose rect is sized to its wrapped height at a given width.</summary>
        public static float WrappedHeight(Text t, float width)
        {
            var settings = t.GetGenerationSettings(new Vector2(width, 0f));
            return t.cachedTextGeneratorForLayout.GetPreferredHeight(t.text, settings) / t.pixelsPerUnit;
        }

        public static float Width(Text t)
        {
            var settings = t.GetGenerationSettings(Vector2.zero);
            return t.cachedTextGeneratorForLayout.GetPreferredWidth(t.text, settings) / t.pixelsPerUnit;
        }

        /// <summary>Largest size in [min, max] at which a single line fits <paramref name="width"/>.</summary>
        public static void Fit(Text t, float width, int max, int min)
        {
            t.fontSize = max;
            while (t.fontSize > min && Width(t) > width) t.fontSize -= 2;
        }

        public static Text Wrapped(UiKit kit, RectTransform parent, string text, int size, Color color, TextAnchor anchor)
        {
            var t = kit.MakeText(parent, text, size, color, anchor);
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.lineSpacing = 0.92f;
            UiKit.Anchor(t.rectTransform, 0, 0, 1, 1);
            return t;
        }

        // ------------------------------------------------------------------ surfaces

        /// <summary>
        /// Cream clay card whose visible face matches the rect. ui_card has ~4% transparent shadow margin and a
        /// 179px slice; <paramref name="corner"/> is the face corner radius in canvas units.
        /// </summary>
        public static Image Panel(UiKit kit, RectTransform parent, Color color, float corner = 96f)
        {
            var img = kit.Card(parent);
            float mult = 179f / Mathf.Max(8f, corner);
            img.pixelsPerUnitMultiplier = mult;
            img.color = color;
            float pad = 35f / mult;
            UiKit.Anchor(img.rectTransform, 0, 0, 1, 1);
            img.rectTransform.offsetMin = new Vector2(-pad, -pad * 1.15f);
            img.rectTransform.offsetMax = new Vector2(pad, pad * 0.85f);
            img.transform.SetAsFirstSibling();
            return img;
        }

        /// <summary>Raised clay slab (rounded ends) whose visible face matches the rect.</summary>
        public static Image Slab(UiKit kit, RectTransform parent, Color color, float height)
        {
            var img = kit.MakeImage(parent, kit.Art.uiSlab, color, sliced: true);
            float full = height / 0.755f;   // slab face covers ~75% of the sprite height (shadow + lip)
            UiKit.Anchor(img.rectTransform, 0, 0, 1, 1);
            img.rectTransform.offsetMin = new Vector2(-full * 0.12f, -full * 0.13f);
            img.rectTransform.offsetMax = new Vector2(full * 0.12f, full * 0.115f);
            img.transform.SetAsFirstSibling();
            return img;
        }

        /// <summary>Thin vertical ink rule.</summary>
        public static Image VRule(UiKit kit, RectTransform parent, float x, float y0 = 0.2f, float y1 = 0.8f)
        {
            var d = kit.Divider(parent);
            var rt = d.rectTransform;
            rt.anchorMin = new Vector2(x, y0);
            rt.anchorMax = new Vector2(x, y1);
            rt.sizeDelta = new Vector2(4f, 0f);
            rt.anchoredPosition = Vector2.zero;
            return d;
        }

        /// <summary>Icon of fixed size centred at (ax, ay) of the parent.</summary>
        public static Image Icon(UiKit kit, RectTransform parent, Sprite sprite, Color tint, float ax, float ay, float w, float h)
        {
            var img = kit.MakeImage(parent, sprite, tint);
            UiKit.Place(img.rectTransform, ax, ay, w, h);
            return img;
        }

        // ------------------------------------------------------------------ controls

        /// <summary>Full-width raised row with a label and an optional chevron (Privacy policy, Manage ad consent…).</summary>
        public static RectTransform LinkRow(UiKit kit, RectTransform parent, string name, float top, float height,
            string label, Color labelColor, Color? chevron, Action onClick)
        {
            var row = Row(parent, name, top, height, Inset, Inset);
            var hit = row.gameObject.AddComponent<Image>();
            hit.color = new Color(0, 0, 0, 0);
            UiKit.Tap(row.gameObject, onClick, 0.5f);
            Slab(kit, row, UiKit.Slab, height);
            var face = UiKit.Rect("Face", row);
            UiKit.Anchor(face, 0f, 0.04f, 1f, 1f);   // optical centre: the lip sits under the face
            var t = kit.MakeText(face, label, UiKit.Body, labelColor, TextAnchor.MiddleLeft);
            UiKit.Anchor(t.rectTransform, 0f, 0f, 1f, 1f);
            t.rectTransform.offsetMin = new Vector2(52f, 0f);
            t.rectTransform.offsetMax = new Vector2(-110f, 0f);
            if (chevron.HasValue && kit.Art.glyphChevron != null)
            {
                var c = kit.MakeImage(face, kit.Art.glyphChevron, chevron.Value);
                var crt = c.rectTransform;
                crt.anchorMin = crt.anchorMax = new Vector2(1f, 0.5f);
                crt.pivot = new Vector2(1f, 0.5f);
                crt.sizeDelta = new Vector2(64f, 64f);
                crt.anchoredPosition = new Vector2(-38f, 0f);
            }
            return row;
        }

        /// <summary>Plain bold text action (e.g. "Not now") with a 44pt touch area and no underline.</summary>
        public static RectTransform TextButton(UiKit kit, RectTransform parent, string label, Color color, Action onClick)
        {
            var rt = UiKit.Rect(label, parent);
            var hit = rt.gameObject.AddComponent<Image>();
            hit.color = new Color(0, 0, 0, 0);
            UiKit.Tap(rt.gameObject, onClick, 0.5f);
            var t = kit.MakeText(rt, label, UiKit.Title, color);
            UiKit.Anchor(t.rectTransform, 0, 0, 1, 1);
            return rt;
        }

        /// <summary>Round cream clay button with the coral close glyph (card corner).</summary>
        public static RectTransform CloseButton(UiKit kit, RectTransform parent, Action onClick)
        {
            var rt = kit.RoundButton(parent, "Close", kit.Art.glyphClose, UiKit.CardCream, UiKit.Coral, onClick);
            return rt;
        }

        /// <summary>Glyph + label centred as one group inside <paramref name="parent"/>.</summary>
        public static (Image icon, Text label) IconLabel(UiKit kit, RectTransform parent, Sprite icon, Color iconTint,
            float iconW, float iconH, string text, int size, Color color, float gap, Font font = null)
        {
            var t = kit.MakeText(parent, text, size, color);
            if (font != null) t.font = font;
            float tw = Width(t);
            float total = iconW + gap + tw, x0 = -total * 0.5f;
            var trt = t.rectTransform;
            trt.anchorMin = new Vector2(0.5f, 0f);
            trt.anchorMax = new Vector2(0.5f, 1f);
            trt.sizeDelta = new Vector2(tw + 8f, 0f);
            trt.anchoredPosition = new Vector2(x0 + iconW + gap + tw * 0.5f, 0f);
            Image img = null;
            if (icon != null)
            {
                img = kit.MakeImage(parent, icon, iconTint);
                var irt = img.rectTransform;
                irt.anchorMin = irt.anchorMax = new Vector2(0.5f, 0.5f);
                irt.sizeDelta = new Vector2(iconW, iconH);
                irt.anchoredPosition = new Vector2(x0 + iconW * 0.5f, 0f);
            }
            return (img, t);
        }

        // ------------------------------------------------------------------ motion

        public static IEnumerator Tween(float seconds, Action<float> step)
        {
            float t = 0f;
            while (t < 1f)
            {
                t += Time.unscaledDeltaTime / Mathf.Max(0.01f, seconds);
                step(Mathf.Clamp01(t));
                yield return null;
            }
        }
    }

    /// <summary>
    /// A full-screen overlay canvas: dim scrim (or the felt), a panel that enters with fade + rise
    /// (ease-out, ~220ms) and exits quicker (ease-in, ~130ms).
    /// </summary>
    public sealed class AccountOverlay : MonoBehaviour
    {
        public RectTransform Safe { get; private set; }
        public RectTransform Panel { get; private set; }
        public Image Scrim { get; private set; }
        public bool Closing { get; private set; }
        CanvasGroup _panelGroup;
        float _scrimAlpha, _rise;
        Action _onScrimTap;

        public static AccountOverlay Create(Transform parent, UiKit kit, string name, int order, float scrimAlpha,
            bool felt = false, Action onScrimTap = null)
        {
            var (canvas, safe) = UiKit.Screen(name, parent, order);
            var o = canvas.gameObject.AddComponent<AccountOverlay>();
            o.Safe = safe;
            o._scrimAlpha = scrimAlpha;
            o._onScrimTap = onScrimTap;
            if (felt) kit.Felt(canvas.transform).transform.SetAsFirstSibling();
            o.Scrim = kit.MakeImage(canvas.transform, null, new Color(AccountUi.Scrim.r, AccountUi.Scrim.g, AccountUi.Scrim.b, 0f));
            o.Scrim.raycastTarget = true;
            UiKit.Anchor(o.Scrim.rectTransform, 0, 0, 1, 1);
            o.Scrim.transform.SetSiblingIndex(felt ? 1 : 0);
            if (onScrimTap != null)
            {
                var b = o.Scrim.gameObject.AddComponent<Button>();
                b.transition = Selectable.Transition.None;
                b.onClick.AddListener(() => { if (!o.Closing) o._onScrimTap?.Invoke(); });
            }
            o.Panel = UiKit.Rect("Panel", safe);
            o._panelGroup = o.Panel.gameObject.AddComponent<CanvasGroup>();
            o._panelGroup.alpha = 0f;
            return o;
        }

        /// <summary>Bottom-anchored floating card with the top bar's side margins.</summary>
        public void BottomCard(float height, float bottom = 46f)
        {
            Panel.anchorMin = new Vector2(AccountUi.SideMargin, 0f);
            Panel.anchorMax = new Vector2(1f - AccountUi.SideMargin, 0f);
            Panel.pivot = new Vector2(0.5f, 0f);
            Panel.sizeDelta = new Vector2(0f, height);
            Panel.anchoredPosition = new Vector2(0f, bottom);
            _rise = 56f;
        }

        /// <summary>Centred dialog card of a fixed width share.</summary>
        public void CenterCard(float widthShare, float height, float y = 0.5f)
        {
            float m = (1f - widthShare) * 0.5f;
            Panel.anchorMin = new Vector2(m, y);
            Panel.anchorMax = new Vector2(1f - m, y);
            Panel.pivot = new Vector2(0.5f, 0.5f);
            Panel.sizeDelta = new Vector2(0f, height);
            Panel.anchoredPosition = Vector2.zero;
            _rise = 0f;
        }

        public void Enter(bool animate = true)
        {
            var baseY = Panel.anchoredPosition.y;
            if (!animate) { _panelGroup.alpha = 1f; SetScrim(1f); return; }
            StartCoroutine(AccountUi.Tween(0.22f, k =>
            {
                float e = UiKit.EaseOut(k);
                _panelGroup.alpha = e;
                SetScrim(e);
                Panel.anchoredPosition = new Vector2(0f, baseY - _rise * (1f - e));
                float s = _rise > 0f ? 1f : Mathf.Lerp(0.96f, 1f, e);
                Panel.localScale = new Vector3(s, s, 1f);
            }));
        }

        void SetScrim(float k)
        {
            var c = Scrim.color;
            c.a = _scrimAlpha * k;
            Scrim.color = c;
        }

        public void Close(Action after = null)
        {
            if (Closing) return;
            Closing = true;
            _panelGroup.blocksRaycasts = false;
            var baseY = Panel.anchoredPosition.y;
            StartCoroutine(CloseRoutine(baseY, after));
        }

        IEnumerator CloseRoutine(float baseY, Action after)
        {
            yield return AccountUi.Tween(0.13f, k =>
            {
                float e = UiKit.EaseIn(k);
                _panelGroup.alpha = 1f - e;
                SetScrim(1f - e);
                Panel.anchoredPosition = new Vector2(0f, baseY - _rise * 0.5f * e);
            });
            Destroy(gameObject);
            after?.Invoke();
        }
    }

    /// <summary>System-style activity indicator: a baked 12-spoke sprite rotated in 30 degree steps.</summary>
    public sealed class Spinner : MonoBehaviour
    {
        float _t;
        int _step;

        void OnEnable() { _t = 0f; }

        void Update()
        {
            _t += Time.unscaledDeltaTime;
            if (_t < 1f / 12f) return;
            _t = 0f;
            _step = (_step + 1) % 12;
            transform.localRotation = Quaternion.Euler(0f, 0f, -30f * _step);
        }
    }

    /// <summary>
    /// "Sign in with Google": flat white button, grey stroke, four-colour G and a system sans label
    /// (Google branding guidelines). Busy state swaps the content for a spinner + "Signing in…" in the same bounds.
    /// </summary>
    public sealed class GoogleButton : MonoBehaviour
    {
        RectTransform _idle, _busy;
        Button _button;
        public bool Busy { get; private set; }

        public static GoogleButton Create(UiKit kit, RectTransform rt, Action onClick)
        {
            var bg = rt.gameObject.AddComponent<Image>();
            bg.sprite = AccountArt.GoogleButton;
            bg.type = Image.Type.Sliced;
            bg.pixelsPerUnitMultiplier = 1.15f;
            bg.color = Color.white;
            var g = rt.gameObject.AddComponent<GoogleButton>();
            g._button = rt.gameObject.AddComponent<Button>();
            g._button.transition = Selectable.Transition.None;
            g._button.onClick.AddListener(() => { if (g.Busy) return; Haptics.Play(Haptics.Impact.Light, 0.6f); onClick(); });
            rt.gameObject.AddComponent<PressSquash>();

            float h = rt.rect.height > 1f ? rt.rect.height : 142f;
            g._idle = UiKit.Rect("Idle", rt);
            UiKit.Anchor(g._idle, 0, 0, 1, 1);
            float logo = h * 0.42f;
            AccountUi.IconLabel(kit, g._idle, AccountArt.GoogleG, Color.white, logo, logo, "Sign in with Google",
                UiKit.Body, AccountUi.GoogleInk, logo * 0.62f, AccountUi.Sans);

            g._busy = UiKit.Rect("Busy", rt);
            UiKit.Anchor(g._busy, 0, 0, 1, 1);
            var (spin, _) = AccountUi.IconLabel(kit, g._busy, AccountArt.Spinner, AccountUi.SpinnerGrey, logo, logo,
                "Signing in…", UiKit.Body, AccountUi.GoogleInk, logo * 0.62f, AccountUi.Sans);
            spin.gameObject.AddComponent<Spinner>();
            g._busy.gameObject.SetActive(false);
            return g;
        }

        public void SetBusy(bool busy)
        {
            Busy = busy;
            _idle.gameObject.SetActive(!busy);
            _busy.gameObject.SetActive(busy);
        }
    }

    /// <summary>Clay switch from the approved settings tiles: mint (ON) or dim (OFF) pill, cream knob, label inside.</summary>
    public sealed class ClayToggle : MonoBehaviour
    {
        Image _track;
        RectTransform _knob;
        Text _label;
        bool _on;
        Coroutine _anim;

        public static ClayToggle Create(UiKit kit, RectTransform rt, bool on, Action<bool> changed)
        {
            var t = rt.gameObject.AddComponent<ClayToggle>();
            t._track = rt.gameObject.AddComponent<Image>();
            t._track.sprite = kit.Art.uiPill;
            t._track.type = Image.Type.Sliced;
            rt.gameObject.AddComponent<RoundCaps>();
            t._label = kit.MakeText(rt, "ON", UiKit.Label, UiKit.Ink);
            t._knob = UiKit.Rect("Knob", rt);
            var k = t._knob.gameObject.AddComponent<Image>();
            k.sprite = kit.Art.uiRound;
            k.color = new Color(1f, 0.96f, 0.9f);
            k.preserveAspect = true;
            k.raycastTarget = false;
            t._on = on;
            t.Apply(1f);
            var b = rt.gameObject.AddComponent<Button>();
            b.transition = Selectable.Transition.None;
            b.onClick.AddListener(() =>
            {
                t._on = !t._on;
                Haptics.Play(Haptics.Impact.Light, 0.6f);
                changed(t._on);
                if (t._anim != null) t.StopCoroutine(t._anim);
                t._anim = t.StartCoroutine(AccountUi.Tween(0.16f, x => t.Apply(UiKit.EaseOut(x))));
            });
            return t;
        }

        float _x = -1f;

        void Apply(float k)
        {
            float target = _on ? 1f : 0f;
            _x = _x < 0f ? target : Mathf.Lerp(_x, target, k);
            _track.color = Color.Lerp(UiKit.Dim, UiKit.Mint, _x);
            // knob: a circle the track's face height, sliding between the two caps
            float pad = 0.11f;
            _knob.anchorMin = new Vector2(Mathf.Lerp(0.03f, 0.97f, _x), pad + 0.02f);
            _knob.anchorMax = new Vector2(Mathf.Lerp(0.03f, 0.97f, _x), 1f - pad);
            _knob.pivot = new Vector2(_x, 0.5f);
            _knob.offsetMin = _knob.offsetMax = Vector2.zero;
            var rt = (RectTransform)transform;
            float h = rt.rect.height;
            _knob.sizeDelta = new Vector2(h * (1f - 2f * pad), 0f);
            _label.text = _on ? "ON" : "OFF";
            _label.color = _on ? UiKit.Ink : UiKit.Cream;
            // label sits centred in the space the knob leaves free
            UiKit.Anchor(_label.rectTransform, _on ? 0.06f : 0.42f, 0.08f, _on ? 0.6f : 0.94f, 0.98f);
        }

        void OnRectTransformDimensionsChange()
        {
            if (_knob != null) Apply(1f);
        }
    }
}
