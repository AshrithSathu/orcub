using System;

namespace Orcub.Ads
{
    public sealed class AdPolicy
    {
        readonly AdsConfig config;
        double lastInterstitial, lastRewarded = double.NegativeInfinity;
        int wins, winsAtInterstitial;
        public int SessionInterstitials { get; private set; }
        public AdPolicy(AdsConfig config, double sessionStart = 0)
        {
            this.config = config;
            lastInterstitial = sessionStart;
        }
        public void RecordWin() => wins++;
        public void RecordRewarded(double now) => lastRewarded = now;
        public bool CanShowInterstitial(int levelNumber, bool won, double now) =>
            config.adsEnabled && won && levelNumber >= config.firstInterstitialLevel &&
            SessionInterstitials < config.maxInterstitialsPerSession &&
            wins - winsAtInterstitial >= config.levelsBetweenInterstitials &&
            now - lastInterstitial >= config.interstitialIntervalSeconds &&
            now - lastRewarded >= config.rewardedCooldownSeconds;
        public void RecordInterstitial(double now)
        {
            SessionInterstitials++;
            lastInterstitial = now;
            winsAtInterstitial = wins;
        }
    }
}
