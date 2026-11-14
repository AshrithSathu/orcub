using System;
using UnityEngine;

namespace Orcub.Ads
{
    [Serializable]
    public sealed class AdsConfig
    {
        public string iosAppId;
        public string rewardedUnitId;
        public string interstitialUnitId;
        public string trackingUsageDescription;
        public bool adsEnabled = true;
        public int firstInterstitialLevel = 8;
        public double interstitialIntervalSeconds = 150;
        public int levelsBetweenInterstitials = 3;
        public int maxInterstitialsPerSession = 6;
        public double rewardedCooldownSeconds = 60;
        public float reloadDelaySeconds = 30;
        public float editorAdDelaySeconds = 1.4f;
        public static AdsConfig Load() => JsonUtility.FromJson<AdsConfig>(Resources.Load<TextAsset>("AdsConfig").text);
    }
}
