using System;
using UnityEngine;

namespace Orcub.Ads
{
    [Serializable]
    public sealed class AdsConfig
    {
        public string iosAppId;
        public string liveAppId, liveRewardedUnitId, liveInterstitialUnitId;
        public string[] testDeviceIds = new string[0];
        public int interstitialDailyCap = 6, rewardedDailyCap = 20;
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
        public static AdsConfig Load()
        {
            var asset = Resources.Load<TextAsset>("AdsConfig");
            if (asset == null) { Debug.LogError("AdsConfig missing; ads disabled"); return new AdsConfig { adsEnabled = false }; }
            var value = JsonUtility.FromJson<AdsConfig>(asset.text);
#if ORCUB_RELEASE && !DEVELOPMENT_BUILD && !UNITY_EDITOR
            value.iosAppId = value.liveAppId; value.rewardedUnitId = value.liveRewardedUnitId; value.interstitialUnitId = value.liveInterstitialUnitId;
#endif
            return value;
        }
    }
}
