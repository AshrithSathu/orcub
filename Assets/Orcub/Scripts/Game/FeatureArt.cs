using UnityEngine;

namespace Orcub.Game
{
    /// <summary>
    /// Daily-loop and tutorial sprites rendered by ArtSource/blender/build_feature_kit.py into
    /// Art/Features/Resources/OrcubFeatures (import settings: Editor/FeatureArtImporter.cs). Loaded on demand.
    /// </summary>
    public static class FeatureArt
    {
        static Sprite _calendar, _calendarCheck, _gift, _bell, _hand;

        static Sprite Load(ref Sprite cache, string name)
        {
            if (cache == null) cache = Resources.Load<Sprite>("OrcubFeatures/" + name);
            return cache;
        }

        public static Sprite Calendar => Load(ref _calendar, "calendar");
        public static Sprite CalendarCheck => Load(ref _calendarCheck, "calendar_check");
        public static Sprite GiftOpen => Load(ref _gift, "gift_open");
        public static Sprite Bell => Load(ref _bell, "bell");
        public static Sprite Hand => Load(ref _hand, "hand");
    }
}
