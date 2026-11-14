using UnityEngine;

namespace Orcub.Game
{
    /// <summary>
    /// Account / privacy sprites rendered by ArtSource/blender/build_account_kit.py into
    /// Art/Account/Resources/OrcubAccount (import settings: Editor/AccountArtImporter.cs).
    /// Loaded on demand so the shared OrcubArt asset is untouched.
    /// </summary>
    public static class AccountArt
    {
        static Sprite _cloud, _cloudCheck, _phone, _trash, _info, _gLogo, _gButton, _spinner, _infoRing, _check;

        static Sprite Load(ref Sprite cache, string name)
        {
            if (cache == null) cache = Resources.Load<Sprite>("OrcubAccount/" + name);
            return cache;
        }

        public static Sprite Cloud => Load(ref _cloud, "cloud");
        public static Sprite CloudCheck => Load(ref _cloudCheck, "cloud_check");
        public static Sprite Phone => Load(ref _phone, "phone");
        public static Sprite Trash => Load(ref _trash, "trash");
        public static Sprite Info => Load(ref _info, "info");
        public static Sprite GoogleG => Load(ref _gLogo, "g_logo");
        public static Sprite GoogleButton => Load(ref _gButton, "g_button");
        public static Sprite Spinner => Load(ref _spinner, "spinner");
        public static Sprite InfoRing => Load(ref _infoRing, "info_ring");
        public static Sprite Check => Load(ref _check, "check");
    }
}
