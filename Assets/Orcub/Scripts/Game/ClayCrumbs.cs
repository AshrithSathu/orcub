using System.Collections.Generic;
using UnityEngine;

namespace Orcub.Game
{
    /// <summary>A few pooled clay crumbs that pop out when a clamp slips off. Cheap, no ParticleSystem setup.</summary>
    public sealed class ClayCrumbs : MonoBehaviour
    {
        struct Crumb
        {
            public SpriteRenderer Sr;
            public Vector2 Vel;
            public float Life, MaxLife, Spin, Size;
        }

        const int PoolSize = 48;
        const int SortingOrder = 40;

        readonly List<Crumb> _live = new List<Crumb>();
        readonly Stack<SpriteRenderer> _free = new Stack<SpriteRenderer>();
        static Sprite _dot;
        Material _material;

        public static ClayCrumbs Create(Transform parent, Material material)
        {
            var go = new GameObject("Crumbs");
            go.transform.SetParent(parent, false);
            var c = go.AddComponent<ClayCrumbs>();
            c._material = material;
            for (int i = 0; i < PoolSize; i++) c._free.Push(c.NewRenderer());
            return c;
        }

        SpriteRenderer NewRenderer()
        {
            var go = new GameObject("crumb");
            go.transform.SetParent(transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Dot();
            sr.sortingOrder = SortingOrder;
            if (_material != null) sr.sharedMaterial = _material;
            go.SetActive(false);
            return sr;
        }

        /// <summary>Soft-edged blob with a little baked top-left highlight so crumbs read as clay.</summary>
        static Sprite Dot()
        {
            if (_dot != null) return _dot;
            const int n = 32;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp01((1f - d) * 6f);
                float shade = Mathf.Lerp(0.78f, 1f, Mathf.Clamp01(0.5f - 0.5f * (dx - dy) * 0.7f));
                px[y * n + x] = new Color(shade, shade, shade, a);
            }
            tex.SetPixels(px);
            tex.Apply();
            _dot = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
            return _dot;
        }

        public void Burst(Vector2 worldPos, Vector2 awayDir, Color colorA, Color colorB, int count = 9, int sortingOrder = SortingOrder, float sizeScale = 1f)
        {
            awayDir = awayDir.sqrMagnitude > 1e-4f ? awayDir.normalized : Vector2.up;
            for (int i = 0; i < count; i++)
            {
                var sr = _free.Count > 0 ? _free.Pop() : NewRenderer();
                float spread = Random.Range(-75f, 75f) * Mathf.Deg2Rad;
                float c = Mathf.Cos(spread), s = Mathf.Sin(spread);
                var dir = new Vector2(awayDir.x * c - awayDir.y * s, awayDir.x * s + awayDir.y * c);
                var crumb = new Crumb
                {
                    Sr = sr,
                    Vel = dir * Random.Range(1.6f, 3.6f),
                    MaxLife = Random.Range(0.35f, 0.6f),
                    Spin = Random.Range(-540f, 540f),
                    Size = Random.Range(0.06f, 0.13f) * sizeScale,
                };
                sr.sortingOrder = sortingOrder;
                sr.color = Random.value < 0.65f ? colorA : colorB;
                sr.transform.position = new Vector3(worldPos.x, worldPos.y, 0f);
                sr.transform.localScale = Vector3.one * crumb.Size;
                sr.gameObject.SetActive(true);
                _live.Add(crumb);
            }
        }

        void Update()
        {
            float dt = Time.deltaTime;
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var c = _live[i];
                c.Life += dt;
                if (c.Life >= c.MaxLife)
                {
                    c.Sr.gameObject.SetActive(false);
                    _free.Push(c.Sr);
                    _live.RemoveAt(i);
                    continue;
                }
                c.Vel *= 1f - 4.5f * dt;          // air drag: crumbs puff out, then settle
                c.Vel += Vector2.down * (3f * dt); // a little gravity
                var t = c.Sr.transform;
                t.position += (Vector3)(c.Vel * dt);
                t.Rotate(0f, 0f, c.Spin * dt);
                float k = c.Life / c.MaxLife;
                t.localScale = Vector3.one * c.Size * (1f - k * k);
                var col = c.Sr.color;
                col.a = 1f - k * k;
                c.Sr.color = col;
                _live[i] = c;
            }
        }
    }
}
