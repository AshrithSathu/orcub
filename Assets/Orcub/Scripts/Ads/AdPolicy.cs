using System;

namespace Orcub.Ads
{
    public sealed class AdPolicy
    {
        readonly AdsConfig config;
        readonly string storagePrefix;
        string day;
        int dailyInterstitials, dailyRewarded;
        void RefreshDay()
        {
            var today = DateTime.UtcNow.ToString("yyyyMMdd");
            if (day == today) return;
            day = today;
            dailyInterstitials = UnityEngine.PlayerPrefs.GetInt(storagePrefix + "interstitial." + day, 0);
            dailyRewarded = UnityEngine.PlayerPrefs.GetInt(storagePrefix + "rewarded." + day, 0);
        }
        public bool CanShowRewarded { get { RefreshDay(); return dailyRewarded < config.rewardedDailyCap; } }
        public void RecordRewardedImpression()
        {
            RefreshDay(); dailyRewarded++;
            UnityEngine.PlayerPrefs.SetInt(storagePrefix + "rewarded." + day, dailyRewarded); UnityEngine.PlayerPrefs.Save();
        }
        bool DailyInterstitialAllowed() { RefreshDay(); return dailyInterstitials < config.interstitialDailyCap; }
        double lastInterstitial, lastRewarded = double.NegativeInfinity;
        int wins, winsAtInterstitial;
        public int SessionInterstitials { get; private set; }
        public AdPolicy(AdsConfig config, double sessionStart = 0, string storagePrefix = "orcub.ads.")
        {
            this.config = config; this.storagePrefix = storagePrefix;
            lastInterstitial = sessionStart;
        }
        public void RecordWin() => wins++;
        public void RecordRewarded(double now) => lastRewarded = now;
        public bool CanShowInterstitial(int levelNumber, bool won, double now) =>
            config.adsEnabled && DailyInterstitialAllowed() && won && levelNumber >= config.firstInterstitialLevel &&
            SessionInterstitials < config.maxInterstitialsPerSession &&
            wins - winsAtInterstitial >= config.levelsBetweenInterstitials &&
            now - lastInterstitial >= config.interstitialIntervalSeconds &&
            now - lastRewarded >= config.rewardedCooldownSeconds;
        public void RecordInterstitial(double now)
        {
            RefreshDay(); dailyInterstitials++;
            UnityEngine.PlayerPrefs.SetInt(storagePrefix + "interstitial." + day, dailyInterstitials); UnityEngine.PlayerPrefs.Save();
            SessionInterstitials++;
            lastInterstitial = now;
            winsAtInterstitial = wins;
        }
    }
}
