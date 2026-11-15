using System;
using System.Collections;
using System.Collections.Generic;
using Orcub.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Orcub.Game
{
    /// <summary>Where a tutorial step points, in world space (board) and screen space (HUD controls).</summary>
    public struct TutorialTarget
    {
        public Vector3 RingCenter;     // world
        public float RingRadius;       // world units
        public float TurnSign;         // rotate: +1 counter-clockwise, -1 clockwise
        public Vector2 PullDirection;  // pull: unit vector, world
        public Rect Booster;           // unlock: the booster button's screen rect
        public Rect Moves;             // moves: the move counter's screen rect
        public Rect[] Others;          // screen rects of the other rings (callouts avoid them)
        public float TrayTop;          // screen y of the booster tray's top edge
        public float GoalBottom;       // screen y of the goal band's bottom edge
    }

    /// <summary>
    /// The approved tutorial_v1 moments drawn over the board: one cream callout, a ghost clay hand and arrow, and a
    /// Skip pill. Only Skip takes touches; everything else lets them through to the board (never a soft lock).
    /// </summary>
    public sealed class TutorialOverlay : MonoBehaviour
    {
        public const int Order = 300;   // above the HUD (100), below cards / sheets (400+)

        UiKit _kit;
        Camera _cam;
        RectTransform _safe, _content, _skip;
        CanvasGroup _group;
        Coroutine _anim;
        public event Action SkipPressed;
        public TutorialStep Showing { get; private set; }

        public static TutorialOverlay Create(Transform parent, UiKit kit, Camera cam)
        {
            var (canvas, safe) = UiKit.Screen("Tutorial", parent, Order);
            var o = canvas.gameObject.AddComponent<TutorialOverlay>();
            o._kit = kit;
            o._cam = cam;
            o._safe = safe;
            o._group = canvas.GetComponent<CanvasGroup>();
            o._group.blocksRaycasts = true;   // only Skip has a raycast target; everything else lets touches through
            return o;
        }

        /// <summary>True when a screen point is on the Skip pill (the board must not treat it as a ring touch).</summary>
        public bool Covers(Vector2 screen) =>
            _skip != null && RectTransformUtility.RectangleContainsScreenPoint(_skip, screen, null);

        public void Hide()
        {
            Showing = TutorialStep.None;
            if (_anim != null) StopCoroutine(_anim);
            _anim = null;
            if (_content != null) Destroy(_content.gameObject);
            _content = null;
            _skip = null;
        }

        public void Show(TutorialStep step, TutorialTarget t)
        {
            Hide();
            if (step == TutorialStep.None) return;
            Showing = step;
            _t = t;
            _content = UiKit.Rect("Step", _safe);
            UiKit.Anchor(_content, 0, 0, 1, 1);
            switch (step)
            {
                case TutorialStep.Rotate: Rotate(t); break;
                case TutorialStep.Release: Released(t); break;
                case TutorialStep.Pull: Pull(t); break;
                case TutorialStep.Moves: Moves(t); break;
                default: Unlock(step, t); break;
            }
            StartCoroutine(UiKit.FadeIn(_group, 0.22f));
        }

        // ------------------------------------------------------------------ coordinates

        Vector2 Local(Vector3 world)
        {
            var screen = RectTransformUtility.WorldToScreenPoint(_cam, world);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_safe, screen, null, out var local);
            return local;
        }

        Vector2 LocalScreen(Vector2 screen)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_safe, screen, null, out var local);
            return local;
        }

        float LocalRadius(TutorialTarget t) => (Local(t.RingCenter + Vector3.right * t.RingRadius) - Local(t.RingCenter)).magnitude;

        float HalfW => _safe.rect.width * 0.5f;
        float HalfH => _safe.rect.height * 0.5f;

        RectTransform At(string name, Vector2 local, Vector2 size)
        {
            var rt = UiKit.Rect(name, _content);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = local;
            return rt;
        }

        // ------------------------------------------------------------------ pieces

        /// <summary>Cream raised callout: one or two lines, sized to its text, optional tail toward a target.</summary>
        RectTransform Callout(string line, string second, Vector2 centre, float? tailX = null, bool tailDown = true, int size = 64)
        {
            var kit = _kit;
            var rt = At("Callout", centre, Vector2.zero);
            var t1 = kit.MakeText(rt, line, size, UiKit.Ink);
            float w = t1.preferredWidth;
            Text t2 = null;
            if (second != null)
            {
                t2 = kit.MakeText(rt, second, UiKit.Body, UiKit.Ink);
                w = Mathf.Max(w, t2.preferredWidth);
            }
            float h = second != null ? size + UiKit.Body + 92f : size + 70f;
            w = Mathf.Min(w + 112f, _safe.rect.width - 40f);
            rt.sizeDelta = new Vector2(w, h);
            // keep the whole callout on screen
            float x = Mathf.Clamp(centre.x, -HalfW + w * 0.5f + 20f, HalfW - w * 0.5f - 20f);
            rt.anchoredPosition = new Vector2(x, centre.y);
            if (tailX.HasValue)
            {
                var tail = kit.MakeImage(rt, TutorialSprites.Tail, UiKit.CardCream);
                var trt = tail.rectTransform;
                trt.anchorMin = trt.anchorMax = new Vector2(0.5f, tailDown ? 0f : 1f);
                trt.pivot = new Vector2(0.5f, 1f);   // the base; the apex hangs away from the callout
                trt.sizeDelta = new Vector2(56f, 40f);
                trt.localScale = new Vector3(1f, tailDown ? 1f : -1f, 1f);
                float tx = Mathf.Clamp(tailX.Value - x, -w * 0.5f + 70f, w * 0.5f - 70f);
                trt.anchoredPosition = new Vector2(tx, tailDown ? 6f : -6f);
            }
            AccountUi.Slab(kit, rt, UiKit.CardCream, h);
            if (t2 == null) UiKit.Anchor(t1.rectTransform, 0f, 0.08f, 1f, 1f);
            else
            {
                UiKit.Anchor(t1.rectTransform, 0f, 0.48f, 1f, 0.96f);
                UiKit.Anchor(t2.rectTransform, 0f, 0.08f, 1f, 0.5f);
            }
            AccountUi.Fit(t1, w - 80f, size, UiKit.Body);
            return rt;
        }

        void SkipPill(Vector2 centre, float width = 330f, float height = 132f)
        {
            var slot = At("SkipSlot", centre, new Vector2(width, height));
            var pill = _kit.Pill(slot, "Skip", UiKit.CardCream, 72, () => SkipPressed?.Invoke());
            UiKit.Anchor(pill, 0, 0, 1, 1);
            _skip = pill;
        }

        Image Hand(Vector2 at, float size = 200f)
        {
            var img = _kit.MakeImage(_content, FeatureArt.Hand, Color.white);
            var rt = img.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.24f, 0.86f);   // the fingertip
            rt.sizeDelta = new Vector2(size, size);
            rt.anchoredPosition = at;
            return img;
        }

        TutorialTarget _t;

        /// <summary>Skip: centred just above the booster tray (approved rotate_B / release_A / pull_A).</summary>
        Vector2 SkipHome => new Vector2(0f, LocalScreen(new Vector2(0f, _t.TrayTop)).y + 30f + 66f);

        /// <summary>Just under the goal band: the calm slot when the target's surroundings are full of rings.</summary>
        float TopSlot(float h) => LocalScreen(new Vector2(0f, _t.GoalBottom)).y - 36f - h * 0.5f;

        bool HitsRings(Vector2 centre, Vector2 size)
        {
            if (_t.Others == null) return false;
            var r = new Rect(centre - size * 0.5f, size);
            foreach (var o in _t.Others)
            {
                var a = LocalScreen(o.min); var b = LocalScreen(o.max);
                if (r.Overlaps(Rect.MinMaxRect(a.x, a.y, b.x, b.y))) return true;
            }
            return false;
        }

        /// <summary>Above the ring with a tail when that space is free, otherwise the top slot without one.</summary>
        void PlaceCallout(string text, Vector2 ring, float r)
        {
            var probe = Callout(text, null, Vector2.zero, null);
            var size = probe.sizeDelta;
            Destroy(probe.gameObject);
            var above = new Vector2(ring.x, ring.y + r + 70f + size.y * 0.5f);
            bool fits = above.y + size.y * 0.5f < TopSlot(0f) + 36f && !HitsRings(above, size);
            if (fits) Callout(text, null, above, ring.x, true);
            else Callout(text, null, new Vector2(0f, TopSlot(size.y)), null);
        }

        // ------------------------------------------------------------------ steps

        void Rotate(TutorialTarget t)
        {
            var c = Local(t.RingCenter);
            float r = LocalRadius(t);
            // the arc arrow swings round the ring's left side, the way it should turn (clear of its clamps)
            var arc = _kit.MakeImage(_content, MechSprites.ArcArrow, new Color(1f, 0.97f, 0.9f, 0.95f));
            var art = arc.rectTransform;
            art.anchorMin = art.anchorMax = new Vector2(0.5f, 0.5f);
            art.pivot = new Vector2(0.5f, 100f / 256f);
            float s = (r + 56f) / 96f * 256f;   // sprite arc radius is 96 px of 256
            art.sizeDelta = new Vector2(s, s);
            art.anchoredPosition = c;
            art.localScale = new Vector3(t.TurnSign >= 0f ? 1f : -1f, 1f, 1f);   // sprite is counter-clockwise
            art.localRotation = Quaternion.Euler(0f, 0f, 90f);
            PlaceCallout("Turn toward the clamp", c, r);
            SkipPill(SkipHome);
            var hand = Hand(c + new Vector2(-r, 0f));   // drawn above Skip
            _anim = StartCoroutine(Orbit(hand.rectTransform, c, r, t.TurnSign >= 0f ? 1f : -1f));
        }

        void Released(TutorialTarget t)
        {
            var c = Local(t.RingCenter);
            PlaceCallout("Released!", c, LocalRadius(t));
            SkipPill(SkipHome);
        }

        void Pull(TutorialTarget t)
        {
            var c = Local(t.RingCenter);
            float r = LocalRadius(t);
            var dir = t.PullDirection.sqrMagnitude > 0.01f ? t.PullDirection.normalized : Vector2.right;
            // straight arrow out of the ring, plus the ghost hand dragging along it
            var from = c + dir * (r * 0.9f);
            var to = c + dir * (r * 0.9f + 260f);
            var shaft = _kit.MakeImage(_content, _kit.Art.uiPill, UiKit.Cream, sliced: true);
            var srt = shaft.rectTransform;
            srt.anchorMin = srt.anchorMax = new Vector2(0.5f, 0.5f);
            srt.pivot = new Vector2(0f, 0.5f);
            srt.sizeDelta = new Vector2(250f, 22f);
            srt.anchoredPosition = from;
            srt.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
            var head = _kit.MakeImage(_content, TutorialSprites.Tail, UiKit.Cream);
            var hrt = head.rectTransform;
            hrt.anchorMin = hrt.anchorMax = new Vector2(0.5f, 0.5f);
            hrt.pivot = new Vector2(0.5f, 0.2f);
            hrt.sizeDelta = new Vector2(76f, 58f);
            hrt.anchoredPosition = to;
            hrt.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg + 90f);
            PlaceCallout("Drag the free ring out", c, r);
            SkipPill(SkipHome);
            var hand = Hand(from);
            _anim = StartCoroutine(Slide(hand.rectTransform, from, from + dir * 230f));
        }

        void Moves(TutorialTarget t)
        {
            var m = LocalScreen(t.Moves.center);
            float below = LocalScreen(new Vector2(t.Moves.center.x, t.Moves.yMin)).y;
            var stars = At("Stars", new Vector2(0f, below - 90f), new Vector2(330f, 110f));
            for (int i = 0; i < 3; i++)
            {
                var st = _kit.MakeImage(stars, _kit.Art.uiStarClay, new Color(0.98f, 0.9f, 0.76f));
                UiKit.Anchor(st.rectTransform, i / 3f + 0.01f, 0f, (i + 1) / 3f - 0.01f, 1f);
            }
            var tick = _kit.MakeImage(_content, TutorialSprites.Tail, UiKit.CardCream);
            var trt = tick.rectTransform;
            trt.anchorMin = trt.anchorMax = new Vector2(0.5f, 0.5f);
            trt.sizeDelta = new Vector2(44f, 32f);
            trt.anchoredPosition = new Vector2(m.x, below - 18f);
            Callout("Fewer moves can earn more stars", null, new Vector2(0f, below - 260f), 0f, false, 52);
            SkipPill(SkipHome);
        }

        void Unlock(TutorialStep step, TutorialTarget t)
        {
            var kind = TutorialFlow.KindOf(step) ?? BoosterKind.Hint;
            string name = kind == BoosterKind.Hint ? "Hint" : kind == BoosterKind.Undo ? "Undo" : "Unclamp";
            var b = LocalScreen(t.Booster.center);
            float top = LocalScreen(new Vector2(t.Booster.center.x, t.Booster.yMax)).y;
            var bubble = Callout($"{name} unlocked", $"Your first {name} is free", new Vector2(b.x + 230f, top + 170f), b.x, true, 66);
            // Skip beside the callout (approved boosters_B), on whichever side has room
            float bx = bubble.anchoredPosition.x, bw = bubble.sizeDelta.x;
            float right = HalfW - (bx + bw * 0.5f), left = (bx - bw * 0.5f) + HalfW;
            bool onRight = right >= left;
            float sw = Mathf.Clamp((onRight ? right : left) - 40f, 200f, 260f);
            float sx = onRight ? bx + bw * 0.5f + 20f + sw * 0.5f : bx - bw * 0.5f - 20f - sw * 0.5f;
            if (Mathf.Abs(sx) + sw * 0.5f > HalfW - 10f)
            {   // no room beside: stack Skip under the callout's opposite edge
                sx = onRight ? HalfW - sw * 0.5f - 24f : -HalfW + sw * 0.5f + 24f;
                SkipPill(new Vector2(sx, top + 170f + bubble.sizeDelta.y * 0.5f + 90f), sw, 110f);
            }
            else SkipPill(new Vector2(sx, bubble.anchoredPosition.y + 10f), sw, 110f);
        }

        // ------------------------------------------------------------------ motion

        /// <summary>The ghost hand drags around the ring the way it should turn, fades, and repeats.</summary>
        IEnumerator Orbit(RectTransform hand, Vector2 c, float r, float sign)
        {
            var img = hand.GetComponent<Image>();
            while (hand != null)
            {
                for (float t = 0f; t < 1f && hand != null; t += Time.unscaledDeltaTime / 1.5f)
                {
                    float a = (180f + sign * 70f * UiKit.EaseOut(Mathf.Clamp01(t * 1.2f))) * Mathf.Deg2Rad;   // + = counter-clockwise
                    hand.anchoredPosition = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
                    img.color = new Color(1, 1, 1, t < 0.12f ? t / 0.12f : t > 0.8f ? (1f - t) / 0.2f : 1f);
                    yield return null;
                }
                yield return new WaitForSecondsRealtime(0.35f);
            }
        }

        IEnumerator Slide(RectTransform hand, Vector2 from, Vector2 to)
        {
            var img = hand.GetComponent<Image>();
            while (hand != null)
            {
                for (float t = 0f; t < 1f && hand != null; t += Time.unscaledDeltaTime / 1.2f)
                {
                    hand.anchoredPosition = Vector2.Lerp(from, to, UiKit.EaseOut(Mathf.Clamp01(t * 1.15f)));
                    img.color = new Color(1, 1, 1, t < 0.12f ? t / 0.12f : t > 0.8f ? (1f - t) / 0.2f : 1f);
                    yield return null;
                }
                yield return new WaitForSecondsRealtime(0.35f);
            }
        }
    }

    /// <summary>Small procedural shapes for callouts (anti-aliased, white, tinted at runtime).</summary>
    public static class TutorialSprites
    {
        static Sprite _tail;

        /// <summary>Rounded downward triangle: a callout tail / arrow head (apex at the bottom centre).</summary>
        public static Sprite Tail
        {
            get
            {
                if (_tail != null) return _tail;
                const int w = 128, h = 96;
                var px = new Color32[w * h];
                Vector2 a = new Vector2(64f, 10f), b = new Vector2(8f, 92f), c = new Vector2(120f, 92f);
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        var p = new Vector2(x + 0.5f, y + 0.5f);
                        float d = Dist(p, a, b, c) - 6f;   // 6 px rounding
                        byte al = (byte)(255f * Mathf.Clamp01(0.5f - d));
                        px[y * w + x] = new Color32(255, 255, 255, al);
                    }
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "TutorialTail" };
                tex.SetPixels32(px);
                tex.Apply();
                _tail = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0f), 100f);
                _tail.name = "TutorialTail";
                return _tail;
            }
        }

        static float Dist(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            // shrink the triangle by the rounding so the rounded shape keeps its size
            float Seg(Vector2 q, Vector2 s0, Vector2 s1)
            {
                var ab = s1 - s0;
                float t = Mathf.Clamp01(Vector2.Dot(q - s0, ab) / ab.sqrMagnitude);
                return (q - (s0 + ab * t)).magnitude;
            }
            float Cross(Vector2 q, Vector2 s0, Vector2 s1) => (q.x - s1.x) * (s0.y - s1.y) - (s0.x - s1.x) * (q.y - s1.y);
            float d = Mathf.Min(Seg(p, a, b), Mathf.Min(Seg(p, b, c), Seg(p, c, a)));
            float d1 = Cross(p, a, b), d2 = Cross(p, b, c), d3 = Cross(p, c, a);
            bool inside = !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
            return inside ? -d : d;
        }
    }
}
