using System;
using System.Collections;
using Orcub.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Orcub.Game
{
    /// <summary>
    /// Level map (approved): a vertical, scrolling path of clay gear nodes on the felt, level 1 at the bottom.
    /// Finished levels show their best stars, the current level is larger and breathes, locked levels are dim with a
    /// padlock. A chapter banner every 20 levels. Tapping an unlocked level plays it (replays cost nothing extra).
    /// </summary>
    public sealed class MapScreen : MonoBehaviour
    {
        const float Spacing = 250f;         // vertical distance between nodes (canvas units)
        const float NodeSize = 210f;
        const float CurrentScale = 1.22f;
        const int ChapterSize = 20;

        static readonly string[] Chapters =
        {
            "The Workshop", "Clockwork Lane", "Spring Meadow", "The Gearworks", "Copper Coast",
            "Tinker Town", "Ratchet Ridge", "Cog Canyon", "Brass Bay", "The Grand Mechanism",
        };

        UiKit _kit;
        Action<int> _onPlayLevel;
        Action _onBack;
        RectTransform _content;
        ScrollRect _scroll;
        RectTransform _current;

        public static MapScreen Create(Transform parent, UiKit kit, Action<int> onPlayLevel, Action onBack)
        {
            var (canvas, safe) = UiKit.Screen("Map", parent, 210);
            var m = canvas.gameObject.AddComponent<MapScreen>();
            m._kit = kit;
            m._onPlayLevel = onPlayLevel;
            m._onBack = onBack;
            kit.Felt(canvas.transform).transform.SetAsFirstSibling();
            m.Build(safe);
            canvas.gameObject.SetActive(false);
            return m;
        }

        public void Show()
        {
            gameObject.SetActive(true);
            Populate();
            StartCoroutine(ScrollToCurrent());
        }

        public void Hide() => gameObject.SetActive(false);

        void Build(RectTransform safe)
        {
            var kit = _kit;
            // scroll view fills the area under the header
            var view = UiKit.Rect("Viewport", safe);
            UiKit.Anchor(view, 0f, 0f, 1f, 0.9f);
            view.gameObject.AddComponent<RectMask2D>();
            var hit = view.gameObject.AddComponent<Image>();
            hit.color = new Color(0, 0, 0, 0);
            _content = UiKit.Rect("Content", view);
            _content.anchorMin = new Vector2(0f, 0f);
            _content.anchorMax = new Vector2(1f, 0f);
            _content.pivot = new Vector2(0.5f, 0f);
            _scroll = view.gameObject.AddComponent<ScrollRect>();
            _scroll.content = _content;
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Elastic;
            _scroll.decelerationRate = 0.12f;
            _scroll.viewport = view;

            // header: back button + chapter title on a slab
            var back = kit.Pill(safe, "‹", UiKit.Coral, UiKit.Title, () => _onBack?.Invoke());
            UiKit.Anchor(back, 0.04f, 0.915f, 0.2f, 0.975f);
            var title = kit.MakeImage(safe, kit.Art.uiSlab, UiKit.Slab, sliced: true).rectTransform;
            UiKit.Anchor(title, 0.23f, 0.915f, 0.96f, 0.975f);
            var t = kit.MakeText(title, "All levels", UiKit.Body, UiKit.Ink);
            UiKit.Anchor(t.rectTransform, 0, 0, 1, 1);
        }

        void Populate()
        {
            for (int i = _content.childCount - 1; i >= 0; i--) Destroy(_content.GetChild(i).gameObject);
            var kit = _kit;
            int count = Mathf.Max(1, LevelLibrary.Count);
            int unlocked = Mathf.Min(count - 1, Progress.HighestUnlocked);
            int current = unlocked;   // the frontier: next level not yet beaten
            // only build up to a little past the frontier: keeps the map light and the future a mystery
            int shown = Mathf.Min(count, unlocked + 12);
            float banners = (shown - 1) / ChapterSize + 1;
            float height = shown * Spacing + banners * 140f + 300f;
            _content.sizeDelta = new Vector2(0f, height);

            float y = 160f;
            Vector2 prev = Vector2.zero;
            for (int i = 0; i < shown; i++)
            {
                if (i % ChapterSize == 0)
                {
                    var banner = kit.MakeImage(_content, kit.Art.uiSlab, UiKit.Slab, sliced: true).rectTransform;
                    banner.anchorMin = banner.anchorMax = new Vector2(0.5f, 0f);
                    banner.sizeDelta = new Vector2(760f, 110f);
                    banner.anchoredPosition = new Vector2(0f, y);
                    var bt = kit.MakeText(banner, $"Chapter {i / ChapterSize + 1} · {Chapters[(i / ChapterSize) % Chapters.Length]}", UiKit.Label, UiKit.Ink);
                    UiKit.Anchor(bt.rectTransform, 0, 0, 1, 1);
                    y += 160f;
                }
                float x = Mathf.Sin(i * 0.9f) * 230f;   // gentle zig-zag
                var pos = new Vector2(x, y + NodeSize * 0.5f);
                if (i > 0 && i % ChapterSize != 0) Beads(prev, pos);
                Node(i, pos, i <= unlocked, i == current);
                prev = pos;
                y += Spacing;
            }
        }

        void Beads(Vector2 a, Vector2 b)
        {
            const int n = 3;
            for (int k = 1; k <= n; k++)
            {
                var p = Vector2.Lerp(a, b, k / (float)(n + 1));
                var bead = _kit.MakeImage(_content, _kit.Art.homeBead, new Color(0.85f, 0.68f, 0.52f));
                var rt = bead.rectTransform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
                rt.sizeDelta = new Vector2(34f, 34f);
                rt.anchoredPosition = p;
                rt.SetAsFirstSibling();
            }
        }

        void Node(int index, Vector2 pos, bool unlocked, bool isCurrent)
        {
            var kit = _kit;
            var node = UiKit.Rect($"Level {index + 1}", _content);
            node.anchorMin = node.anchorMax = new Vector2(0.5f, 0f);
            node.sizeDelta = new Vector2(NodeSize, NodeSize);
            node.anchoredPosition = pos;

            var colour = unlocked ? kit.Art.RingColor(index) : UiKit.Dim;
            var ring = kit.MakeImage(node, kit.Art.homeGearRing, colour);
            UiKit.Anchor(ring.rectTransform, 0, 0, 1, 1);
            var number = kit.MakeText(node, (index + 1).ToString(), UiKit.Title, unlocked ? UiKit.Cream : new Color(1f, 1f, 1f, 0.45f));
            UiKit.Anchor(number.rectTransform, 0, 0.32f, 1, 0.8f);
            UiKit.Shadow(number, 0.35f);

            if (!unlocked)
            {
                var lk = kit.MakeImage(node, kit.Art.homeLock, new Color(0.9f, 0.82f, 0.72f, 0.8f));
                UiKit.Anchor(lk.rectTransform, 0.39f, 0.16f, 0.61f, 0.38f);
                return;
            }

            int stars = Progress.BestStars(index);
            if (stars > 0)
                for (int s = 0; s < 3; s++)
                {
                    var st = kit.MakeImage(node, kit.Art.uiStarClay, s < stars ? UiKit.StarGold : new Color(0.4f, 0.3f, 0.35f, 0.6f));
                    float x0 = 0.24f + s * 0.18f;
                    UiKit.Anchor(st.rectTransform, x0, 0.16f, x0 + 0.17f, 0.33f);
                }

            var btn = node.gameObject.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            var hit = node.gameObject.AddComponent<Image>();
            hit.color = new Color(0, 0, 0, 0);
            btn.targetGraphic = hit;
            int captured = index;
            btn.onClick.AddListener(() => { Haptics.Play(Haptics.Impact.Light, 0.6f); _onPlayLevel?.Invoke(captured); });

            if (isCurrent)
            {
                node.localScale = Vector3.one * CurrentScale;
                node.gameObject.AddComponent<MapNodeBreathe>();   // breathes instead of squashing
                _current = node;
            }
            else node.gameObject.AddComponent<PressSquash>();
        }

        IEnumerator ScrollToCurrent()
        {
            yield return null;   // let layout settle
            if (_current == null) yield break;
            float contentH = _content.rect.height;
            float viewH = ((RectTransform)_scroll.viewport).rect.height;
            float target = _current.anchoredPosition.y - viewH * 0.4f;
            _scroll.verticalNormalizedPosition = Mathf.Clamp01(target / Mathf.Max(1f, contentH - viewH));
        }
    }

    /// <summary>Gentle scale pulse for the current level node on the map.</summary>
    public sealed class MapNodeBreathe : MonoBehaviour
    {
        Vector3 _base;
        void Start() => _base = transform.localScale;
        void Update()
        {
            float s = 1f + 0.04f * Mathf.Sin(Time.unscaledTime * 2.6f);
            transform.localScale = _base * s;
        }
    }
}
