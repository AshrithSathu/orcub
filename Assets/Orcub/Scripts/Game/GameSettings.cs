using UnityEngine;

namespace Orcub.Game
{
    /// <summary>Player settings (sound, haptics), remembered between sessions.</summary>
    public static class GameSettings
    {
        const string SoundKey = "orcub.sound";
        const string HapticsKey = "orcub.haptics";

        public static bool SoundOn
        {
            get => PlayerPrefs.GetInt(SoundKey, 1) == 1;
            set { PlayerPrefs.SetInt(SoundKey, value ? 1 : 0); PlayerPrefs.Save(); Apply(); ClientGateway.Track("settings_changed", new { sound = SoundOn, haptics = HapticsOn }); }
        }

        public static bool HapticsOn
        {
            get => PlayerPrefs.GetInt(HapticsKey, 1) == 1;
            set { PlayerPrefs.SetInt(HapticsKey, value ? 1 : 0); PlayerPrefs.Save(); Apply(); ClientGateway.Track("settings_changed", new { sound = SoundOn, haptics = HapticsOn }); }
        }

        public static void Apply()
        {
            AudioListener.volume = SoundOn ? 1f : 0f;
            Haptics.Enabled = HapticsOn;
        }
    }
}
