using System;

namespace Orcub.Game
{
    // SDK-free boundary: the Ads assembly installs its provider at boot.
    public interface IAdsService
    {
        bool RewardedReady { get; }
        /// <summary>ad_offer telemetry: call when a rewarded button becomes visible, once per sheet opening.</summary>
        void RewardedOfferShown(string placement);
        void ShowRewarded(string placement, Action onReward, Action onClosedWithoutReward);
        void RecordWin(int levelNumber);
        void MaybeShowInterstitial(string placement, Action onFinished);
        bool PrivacyOptionsRequired { get; }
        void ShowPrivacyOptions();
    }

    public static class AdsGateway
    {
        public static IAdsService Service { get; set; }
    }
}
