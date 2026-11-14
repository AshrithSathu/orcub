using UnityEngine;

namespace Orcub.Core
{
    /// <summary>The shipped level pack (Resources/Orcub/levels.json), loaded once.</summary>
    public static class LevelLibrary
    {
        static LevelPack _pack;

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

        public static int Count => Pack.levels?.Length ?? 0;

        /// <summary>Level by zero-based index; wraps past the end so the game never runs out.</summary>
        public static LevelData Get(int index) => Get(index, 0);

        public static LevelData Get(int index, int variant)
        {
            if (Count == 0) return null;
            int slot = ((index % Count) + Count) % Count;
            if (variant == 0) return Pack.levels[slot];
            var choices = GetVariants(index);
            if (variant < 0 || variant >= choices.Length) throw new System.ArgumentOutOfRangeException(nameof(variant));
            return choices[variant];
        }

        public static LevelData[] GetVariants(int index)
        {
            if (Count == 0) return new LevelData[0];
            int slot = ((index % Count) + Count) % Count;
            return Pack.variants != null && slot < Pack.variants.Length && Pack.variants[slot]?.choices != null
                ? Pack.variants[slot].choices : new[] { Pack.levels[slot] };
        }

        /// <summary>Drop the cache (editor: after rebuilding the pack).</summary>
        public static void Reload() => _pack = null;
    }
}
