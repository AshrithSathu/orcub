using UnityEngine;

namespace Orcub.Core
{
    /// <summary>The shipped level pack (Resources/Orcub/levels.json), loaded once.</summary>
    public static class LevelLibrary
    {
        static LevelPack _pack;
        static InfiniteLevelCache infinite;
        public const int CuratedCount = 200;
        public static bool IsInfinite => true;
        static InfiniteLevelCache Infinite => infinite ?? (infinite = new InfiniteLevelCache(Application.persistentDataPath, LoadCurve()));
        static InfiniteCurve LoadCurve()
        {
            var asset = Resources.Load<TextAsset>("Orcub/tuning");
            return asset == null ? new InfiniteCurve() : Tuning.FromJson(asset.text).Infinite;
        }
        public static int ChapterOf(int index) => System.Math.Max(0, index) / 20;
        public static InfiniteChapter ChapterInfo(int chapter) => new InfiniteChapter(chapter);
        public static void Prefetch(int index) => Infinite.Prefetch(index);
        public static System.Threading.Tasks.Task<LevelData[]> PrepareAsync(int index) => index < CuratedCount
            ? System.Threading.Tasks.Task.FromResult(GetVariants(index)) : Infinite.PrepareAsync(index);
        public static bool TryGetVariants(int index, out LevelData[] levels)
        {
            if (index < CuratedCount) { levels = GetVariants(index); return true; }
            return Infinite.TryGet(index, out levels);
        }
        public static void Configure(Tuning tuning) => Infinite.Configure(tuning.Infinite);
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void WarmInfinite() => Prefetch(System.Math.Max(195, Progress.CurrentLevel));

        public static LevelPack Pack
        {
            get
            {
                if (_pack != null) return _pack;
                var asset = Resources.Load<TextAsset>("Orcub/levels");
                _pack = asset != null ? LevelPack.FromJson(asset.text) : new LevelPack { levels = new LevelData[0] };
                return _pack;
            }
        }

        /// <summary>Size of the shipped pack only. Never use as a progression limit.</summary>
        public static int Count => CuratedCount;

        /// <summary>Zero-based saga index. For a cold generated index, await PrepareAsync before this synchronous API.</summary>
        public static LevelData Get(int index) => Get(index, 0);

        public static LevelData Get(int index, int variant)
        {
            if (variant < 0 || variant > 4) throw new System.ArgumentOutOfRangeException(nameof(variant));
            if (index >= CuratedCount) return GetVariants(index)[variant];
            if (Pack.levels == null || Pack.levels.Length == 0) return null;
            int slot = ((index % Count) + Count) % Count;
            if (variant == 0) return Pack.levels[slot];
            var choices = GetVariants(index);
            if (variant < 0 || variant >= choices.Length) throw new System.ArgumentOutOfRangeException(nameof(variant));
            return choices[variant];
        }

        public static LevelData[] GetVariants(int index)
        {
            if (index >= CuratedCount) return Infinite.PrepareAsync(index).GetAwaiter().GetResult();
            if (Pack.levels == null || Pack.levels.Length == 0) return new LevelData[0];
            int slot = ((index % Count) + Count) % Count;
            return Pack.variants != null && slot < Pack.variants.Length && Pack.variants[slot]?.choices != null
                ? Pack.variants[slot].choices : new[] { Pack.levels[slot] };
        }

        /// <summary>Drop the cache (editor: after rebuilding the pack).</summary>
        public static void Reload() => _pack = null;
    }
}
