using System;
using System.Collections;
using System.Collections.Generic;
using Orcub.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Orcub.Game
{
    /// <summary>
    /// One chapter's map (approved nav v3, map A): a bead trail of clay gear nodes on the felt, first level at the
    /// bottom. Beaten levels keep their colour and show their stars, the current level glows with a "Play" bubble,
    /// later levels are pale with a padlock. Tapping an open level raises the level-start sheet (approved B).
    /// </summary>
    public sealed class MapScreen : MonoBehaviour
    {
        const float Step = 235f;          // vertical distance between nodes (canvas units)
        const float NodeSize = 190f;
        const float Swing = 255f;         // horizontal wander of the trail
        static readonly Color LockedNode = new Color(0.93f, 0.84f, 0.77f);
        static readonly Color BeadOpen = new Color(0.97f, 0.89f, 0.77f);
        static readonly Color BeadLocked = new Color(0.97f, 0.89f, 0.77f, 0.45f);

        UiKit _kit;
        Action<int> _onPlayLevel;
        Action _onBack;
        CanvasGroup _group;
        RectTransform _canvas, _content, _current;
        ScrollRect _scroll;
        TopBar _bar;
        Text _chapterCaption, _chapterName, _chapterInfo, _footer;
        LevelStartSheet _sheet;
        int _chapter;

        public static MapScreen Create(Transform parent, UiKit kit, Action<int> onPlayLevel, Action onBack)
        {
            var (canvas, safe) = UiKit.Screen("Map", parent, 210);
            var m = canvas.gameObject.AddComponent<MapScreen>();
            m._kit = kit;
            m._onPlayLevel = onPlayLevel;
            m._onBack = onBack;
            m._group = canvas.GetComponent<CanvasGroup>();
            m._canvas = (RectTransform)canvas.transform;
            kit.Felt(canvas.transform).transform.SetAsFirstSibling();
            m.Build(safe);
            canvas.gameObject.SetActive(false);
            return m;
        }

        public int Chapter => _chapter;

        public void Show(int chapter)
        {
            _chapter = Mathf.Clamp(chapter, 0, Chapters.Count - 1);
            gameObject.SetActive(true);
            _sheet.CloseInstant();
            _bar.Refresh();
            Populate();
            StartCoroutine(ScrollToCurrent());
            StartCoroutine(UiKit.FadeIn(_group));
        }

        public void Hide() => gameObject.SetActive(false);

        void Build(RectTransform safe)
        {
            var kit = _kit;
            _bar = TopBar.Create(safe, kit, kit.Art.glyphBack, UiKit.Ink, () => _onBack?.Invoke());
            if (kit.Art.glyphBack == null)
            {
                var arrow = kit.MakeText(_bar.transform.GetChild(0), "‹", UiKit.Display, UiKit.Ink);
                UiKit.Anchor(arrow.rectTransform, 0, 0.06f, 1, 1);
            }

            // chapter header card
            var header = UiKit.Rect("Header", safe);
            UiKit.Anchor(header, 0.12f, 0.775f, 0.88f, 0.895f);
            var hb = kit.Card(header, 0.55f);
            UiKit.Anchor(hb.rectTransform, 0, 0, 1, 1);
            var ink = UiKit.Ink;
            _chapterCaption = kit.MakeText(header, "", UiKit.Caption, new Color(ink.r, ink.g, ink.b, 0.75f));
            UiKit.Anchor(_chapterCaption.rectTransform, 0, 0.7f, 1, 0.92f);
            _chapterName = kit.MakeText(header, "", UiKit.Title, ink);
            UiKit.Anchor(_chapterName.rectTransform, 0, 0.32f, 1, 0.72f);
            _chapterInfo = kit.MakeText(header, "", UiKit.Caption, new Color(ink.r, ink.g, ink.b, 0.75f));
            UiKit.Anchor(_chapterInfo.rectTransform, 0, 0.08f, 1, 0.32f);

            // the trail
            var view = UiKit.Rect("Viewport", safe);
            UiKit.Anchor(view, 0f, 0.06f, 1f, 0.765f);
            view.gameObject.AddComponent<RectMask2D>().softness = new Vector2Int(0, 70);   // fade, not a hard cut
            var hit = view.gameObject.AddComponent<Image>();
            hit.color = new Color(0, 0, 0, 0);
            _content = UiKit.Rect("Content", view);
            _content.anchorMin = new Vector2(0f, 0f);
            _content.anchorMax = new Vector2(1f, 0f);
            _content.pivot = new Vector2(0.5f, 0f);
            _scroll = view.gameObject.AddComponent<ScrollRect>();
            _scroll.content = _content;
            _scroll.viewport = view;
            _scroll.horizontal = false;
            _scroll.movementType = ScrollRect.MovementType.Elastic;
            _scroll.decelerationRate = 0.12f;

            _footer = kit.MakeText(safe, "", UiKit.Label, new Color(UiKit.Cream.r, UiKit.Cream.g, UiKit.Cream.b, 0.7f));
            UiKit.Anchor(_footer.rectTransform, 0f, 0.005f, 1f, 0.055f);

            _sheet = LevelStartSheet.Create(_canvas, kit, index => _onPlayLevel?.Invoke(index));
        }

        static Vector2 NodePos(int k) => new Vector2(Mathf.Sin(k * 1.05f + 0.6f) * Swing, 140f + k * Step);

        void Populate()
        {
            for (int i = _content.childCount - 1; i >= 0; i--) Destroy(_content.GetChild(i).gameObject);
            _current = null;
            int first = Chapters.First(_chapter), last = Chapters.Last(_chapter);
            int n = last - first + 1;
            int frontier = Chapters.Frontier;

            _chapterCaption.text = $"Chapter {_chapter + 1}";
            _chapterName.text = Chapters.Name(_chapter);
            _chapterInfo.text = $"Levels {first + 1}–{last + 1} · {Chapters.Stars(_chapter)}/{n * 3} stars";
            _footer.text = $"Chapter {_chapter + 1} of {Chapters.Count}";
            _content.sizeDelta = new Vector2(0f, NodePos(n - 1).y + NodeSize + 220f);

            for (int k = 1; k < n; k++)
                Beads(NodePos(k - 1), NodePos(k), first + k <= frontier,
                    Progress.BestStars(first + k - 1) > 0, Progress.BestStars(first + k) > 0);
            for (int k = 0; k < n; k++)
            {
                int level = first + k;
                Node(level, NodePos(k), level <= frontier, level == frontier);
            }
        }

        void Beads(Vector2 a, Vector2 b, bool open, bool aStars, bool bStars)
        {
            float dist = Vector2.Distance(a, b);
            float clear = NodeSize * 0.5f + 10f;
            int count = Mathf.Max(1, Mathf.FloorToInt((dist - 2f * clear) / 32f));
            for (int k = 0; k <= count; k++)
            {
                float t = Mathf.Lerp(clear / dist, 1f - clear / dist, count == 0 ? 0.5f : k / (float)count);
                var p = Vector2.Lerp(a, b, t);
                if ((aStars && InStarRow(p, a)) || (bStars && InStarRow(p, b))) continue;   // never draw over a node's stars
                var bead = _kit.MakeImage(_content, _kit.Art.homeBead, open ? BeadOpen : BeadLocked);
                var rt = bead.rectTransform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
                rt.sizeDelta = new Vector2(28f, 28f);
                rt.anchoredPosition = p;
            }
        }

        /// <summary>The strip just under a node where its stars sit.</summary>
        static bool InStarRow(Vector2 p, Vector2 node) =>
            Mathf.Abs(p.x - node.x) < NodeSize * 0.42f && p.y < node.y - NodeSize * 0.3f && p.y > node.y - NodeSize * 0.78f;

        void Node(int index, Vector2 pos, bool unlocked, bool isCurrent)
        {
            var kit = _kit;
            var art = kit.Art;
            var node = UiKit.Rect($"Level {index + 1}", _content);
            node.anchorMin = node.anchorMax = new Vector2(0.5f, 0f);
            node.sizeDelta = new Vector2(NodeSize, NodeSize);
            node.anchoredPosition = pos;

            if (isCurrent && art.nodeGlow != null)
            {
                var glow = kit.MakeImage(node, art.nodeGlow, new Color(UiKit.Mint.r, UiKit.Mint.g, UiKit.Mint.b, 0.75f));
                UiKit.Anchor(glow.rectTransform, -0.3f, -0.3f, 1.3f, 1.3f);
            }

            var tint = isCurrent ? UiKit.Mint : unlocked ? art.RingColor(index) : LockedNode;
            var gear = kit.MakeImage(node, art.mapNode != null ? art.mapNode : art.homeGearRing, tint);
            UiKit.Anchor(gear.rectTransform, 0, 0, 1, 1);
            var centre = kit.MakeImage(node, art.mapNodeCenter != null ? art.mapNodeCenter : art.uiRound,
                art.mapNodeCenter != null ? Color.white : UiKit.CardCream);
            if (art.mapNodeCenter != null) UiKit.Anchor(centre.rectTransform, 0, 0, 1, 1);
            else UiKit.Anchor(centre.rectTransform, 0.24f, 0.24f, 0.76f, 0.76f);
            var number = kit.MakeText(node, (index + 1).ToString(), 64,
                unlocked ? UiKit.Ink : new Color(UiKit.Ink.r, UiKit.Ink.g, UiKit.Ink.b, 0.7f));
            UiKit.Anchor(number.rectTransform, 0, 0.02f, 1, 1f);

            if (!unlocked)
            {
                var lk = kit.MakeImage(node, art.homeLock, Color.white);
                UiKit.Anchor(lk.rectTransform, 0.36f, -0.08f, 0.64f, 0.24f);
                return;
            }

            int stars = Progress.BestStars(index);
            if (stars > 0)
                for (int s = 0; s < 3; s++)
                {
                    var st = kit.MakeImage(node, art.uiStarClay, s < stars ? UiKit.StarGold : new Color(0.95f, 0.9f, 0.84f));
                    float x0 = 0.17f + s * 0.23f;
                    UiKit.Anchor(st.rectTransform, x0, -0.2f, x0 + 0.22f, 0.02f);
                }

            var hit = node.gameObject.AddComponent<Image>();
            hit.color = new Color(0, 0, 0, 0);
            int captured = index;
            UiKit.Tap(node.gameObject, () => _sheet.Open(captured));

            if (isCurrent)
            {
                var bubbleRt = UiKit.Rect("Bubble", node);
                bubbleRt.anchorMin = bubbleRt.anchorMax = new Vector2(0.5f, 1f);
                bubbleRt.pivot = new Vector2(0.5f, 0f);
                var bubbleTint = new Color(0.75f, 0.92f, 0.78f);
                if (art.bubble != null)
                {
                    // the kit's tail sits a quarter of the way in from the left: shift so it points at the node
                    bubbleRt.sizeDelta = new Vector2(238f, 140f);
                    bubbleRt.anchoredPosition = new Vector2(238f * 0.25f, 0f);
                    var bub = kit.MakeImage(bubbleRt, art.bubble, bubbleTint);
                    UiKit.Anchor(bub.rectTransform, 0, 0, 1, 1);
                }
                else
                {
                    bubbleRt.sizeDelta = new Vector2(170f, 96f);
                    bubbleRt.anchoredPosition = new Vector2(0f, 6f);
                    var bub = kit.MakeImage(bubbleRt, art.uiPill, bubbleTint, sliced: true);
                    UiKit.Anchor(bub.rectTransform, 0, 0, 1, 1);
                }
                var label = kit.MakeText(bubbleRt, "Play", UiKit.Label, UiKit.Ink);
                UiKit.Anchor(label.rectTransform, 0, art.bubble != null ? 0.3f : 0.04f, 1, 0.96f);
                node.gameObject.AddComponent<MapNodeBreathe>();
                _current = node;
            }
        }

        IEnumerator ScrollToCurrent()
        {
            yield return null;   // let layout settle
            if (_current == null) { _scroll.verticalNormalizedPosition = 0f; yield break; }
            float contentH = _content.rect.height;
            float viewH = ((RectTransform)_scroll.viewport).rect.height;
            float target = _current.anchoredPosition.y - viewH * 0.4f;
            _scroll.verticalNormalizedPosition = Mathf.Clamp01(target / Mathf.Max(1f, contentH - viewH));
        }
    }

    /// <summary>
    /// Level start (approved nav v3, B): a bottom sheet over the map with the goal, the move targets for each star,
    /// and Play right under the thumb. Slides up with ease-out; tapping the dimmed map or the close button dismisses.
    /// </summary>
    public sealed class LevelStartSheet : MonoBehaviour
    {
        const float SheetFraction = 0.46f;

        UiKit _kit;
        Action<int> _onPlay;
        RectTransform _root, _panel, _goal;
        Image _scrim;
        Text _title, _goalText;
        readonly Text[] _targets = new Text[3];
        int _level;
        Coroutine _anim;

        public static LevelStartSheet Create(RectTransform canvas, UiKit kit, Action<int> onPlay)
        {
            var root = UiKit.Rect("LevelStart", canvas);
            UiKit.Anchor(root, 0, 0, 1, 1);
            var s = root.gameObject.AddComponent<LevelStartSheet>();
            s._kit = kit;
            s._onPlay = onPlay;
            s._root = root;
            s.Build();
            root.gameObject.SetActive(false);
            return s;
        }

        void Build()
        {
            var kit = _kit;
            var art = kit.Art;
            _scrim = kit.MakeImage(_root, null, new Color(0.05f, 0.01f, 0.03f, 0f));
            _scrim.raycastTarget = true;
            UiKit.Anchor(_scrim.rectTransform, 0, 0, 1, 1);
            UiKit.Tap(_scrim.gameObject, Close, 0.3f);

            _panel = UiKit.Rect("Sheet", _root);
            UiKit.Anchor(_panel, 0.02f, 0f, 0.98f, SheetFraction);
            var bg = kit.MakeImage(_panel, art.sheet != null ? art.sheet : art.uiCard, art.sheet != null ? Color.white : UiKit.CardCream);
            bg.type = Image.Type.Sliced;
            bg.raycastTarget = true;   // taps on the sheet never fall through to the scrim
            UiKit.Anchor(bg.rectTransform, 0, 0, 1, 1);
            bg.rectTransform.offsetMin = new Vector2(0f, -60f);   // bleed under the home indicator

            // content lives above the home-indicator inset
            var body = UiKit.Rect("Body", _panel);
            UiKit.Anchor(body, 0.05f, 0.1f, 0.95f, 0.97f);
            var ink = UiKit.Ink;

            _title = kit.MakeText(body, "Level 1", UiKit.Display, ink);
            UiKit.Anchor(_title.rectTransform, 0.12f, 0.84f, 0.88f, 1f);
            var close = kit.SquareButton(body, "Close", art.glyphClose, UiKit.Coral, Close);
            UiKit.Anchor(close, 0.87f, 0.85f, 1f, 1f);
            close.pivot = new Vector2(1f, 0.5f);
            if (art.glyphClose == null)
            {
                var x = kit.MakeText(close, "×", UiKit.Display, UiKit.Coral);
                UiKit.Anchor(x.rectTransform, 0, 0.04f, 1, 1);
            }

            _goalText = kit.MakeText(body, "", UiKit.Body, ink);
            UiKit.Anchor(_goalText.rectTransform, 0, 0.74f, 1, 0.84f);
            _goal = UiKit.Rect("Goal", body);
            UiKit.Anchor(_goal, 0.04f, 0.55f, 0.96f, 0.73f);

            // star targets: moves needed for 1, 2 and 3 stars
            var box = kit.MakeImage(body, art.uiSlab, UiKit.Slab, sliced: true).rectTransform;
            UiKit.Anchor(box, 0.02f, 0.3f, 0.98f, 0.52f);
            var cap = kit.MakeText(box, "Moves to earn stars", UiKit.Caption, new Color(ink.r, ink.g, ink.b, 0.75f));
            UiKit.Anchor(cap.rectTransform, 0, 0.64f, 1, 0.94f);
            for (int i = 0; i < 3; i++)
            {
                var col = UiKit.Rect($"Stars{i + 1}", box);
                UiKit.Anchor(col, i / 3f, 0.06f, (i + 1) / 3f, 0.64f);
                const float starW = 46f, textW = 86f, gap = 8f;
                float groupW = (i + 1) * starW + gap + textW, x0 = -groupW * 0.5f;
                for (int s = 0; s <= i; s++)
                {
                    var st = kit.MakeImage(col, art.uiStarClay, UiKit.StarGold);
                    var srt = st.rectTransform;
                    srt.anchorMin = srt.anchorMax = new Vector2(0.5f, 0.5f);
                    srt.sizeDelta = new Vector2(starW, starW);
                    srt.anchoredPosition = new Vector2(x0 + starW * (s + 0.5f), 0f);
                }
                _targets[i] = kit.MakeText(col, "", UiKit.Body, ink, TextAnchor.MiddleLeft);
                var trt = _targets[i].rectTransform;
                trt.anchorMin = trt.anchorMax = new Vector2(0.5f, 0.5f);
                trt.sizeDelta = new Vector2(textW, 64f);
                trt.anchoredPosition = new Vector2(x0 + (i + 1) * starW + gap + textW * 0.5f, 0f);
                if (i > 0)
                {
                    var d = kit.Divider(box);
                    UiKit.Anchor(d.rectTransform, i / 3f - 0.002f, 0.14f, i / 3f + 0.002f, 0.58f);
                }
            }

            var play = kit.Pill(body, "Play", UiKit.Mint, 84, () =>
            {
                Haptics.Play(Haptics.Impact.Medium, 0.7f);
                int level = _level;
                CloseInstant();
                _onPlay?.Invoke(level);
            });
            UiKit.Anchor(play, 0.08f, 0.09f, 0.92f, 0.27f);

            var life = UiKit.Rect("Life", body);
            UiKit.Anchor(life, 0.3f, -0.02f, 0.7f, 0.08f);
            var heart = kit.MakeImage(life, art.uiHeartClay, UiKit.HeartRed);
            UiKit.Anchor(heart.rectTransform, 0.2f, 0.05f, 0.38f, 0.95f);
            var lt = kit.MakeText(life, "1 life", UiKit.Label, ink, TextAnchor.MiddleLeft);
            UiKit.Anchor(lt.rectTransform, 0.42f, 0, 1, 1);
        }

        public void Open(int level)
        {
            _level = level;
            var data = LevelLibrary.Get(level);
            var def = data != null ? data.ToDef() : Levels.Basic10();
            var rules = LevelRules.For(def, tutorial: level < 3);
            _title.text = $"Level {level + 1}";
            int rings = def.Rings.Count;
            _goalText.text = $"Free all {rings} gears";

            for (int i = _goal.childCount - 1; i >= 0; i--) Destroy(_goal.GetChild(i).gameObject);
            float w = 1f / Mathf.Max(rings, 5);
            float x0 = 0.5f - rings * w * 0.5f;
            for (int i = 0; i < rings; i++)
            {
                var g = _kit.MakeImage(_goal, _kit.Art.homeGearRing, _kit.Art.RingColor(def.Rings[i].Color));
                UiKit.Anchor(g.rectTransform, x0 + i * w + w * 0.06f, 0f, x0 + (i + 1) * w - w * 0.06f, 1f);
            }

            _targets[0].text = $"≤ {rules.MoveLimit}";
            _targets[1].text = $"≤ {rules.TwoStar}";
            _targets[2].text = $"≤ {rules.ThreeStar}";

            _root.gameObject.SetActive(true);
            _root.SetAsLastSibling();
            if (_anim != null) StopCoroutine(_anim);
            _anim = StartCoroutine(Slide(true));
        }

        public void Close()
        {
            if (!_root.gameObject.activeSelf) return;
            if (_anim != null) StopCoroutine(_anim);
            _anim = StartCoroutine(Slide(false));
        }

        public void CloseInstant()
        {
            if (_anim != null) StopCoroutine(_anim);
            _anim = null;
            _root.gameObject.SetActive(false);
        }

        IEnumerator Slide(bool open)
        {
            float h = _root.rect.height * SheetFraction + 80f;
            float seconds = open ? 0.28f : 0.18f;   // enter eases out; exit is quicker and accelerates away
            float t = 0f;
            while (t < 1f)
            {
                t += Time.unscaledDeltaTime / seconds;
                float k = Mathf.Clamp01(t);
                float shown = open ? UiKit.EaseOut(k) : 1f - UiKit.EaseIn(k);
                _panel.anchoredPosition = new Vector2(0f, -h * (1f - shown));
                _scrim.color = new Color(0.05f, 0.01f, 0.03f, 0.55f * shown);
                yield return null;
            }
            if (!open) _root.gameObject.SetActive(false);
            _anim = null;
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
