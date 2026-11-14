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
        public static LevelData Get(int index)
        {
            if (Count == 0) return null;
            return Pack.levels[((index % Count) + Count) % Count];
        }

        /// <summary>Drop the cache (editor: after rebuilding the pack).</summary>
        public static void Reload() => _pack = null;
    }
}
