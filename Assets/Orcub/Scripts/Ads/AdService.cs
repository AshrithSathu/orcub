using System;
using System.Collections;
using System.Collections.Concurrent;
using Orcub.Game;
using GoogleMobileAds.Api;
using GoogleMobileAds.Ump.Api;
using UnityEngine;

namespace Orcub.Ads
{
    public sealed class AdService : MonoBehaviour, IAdsService
    {
        public static AdService Instance { get; private set; }
        AdsConfig config;
        AdPolicy policy;
        RewardedAd rewarded;
        InterstitialAd interstitial;
        readonly ConcurrentQueue<Action> callbacks = new ConcurrentQueue<Action>();
        bool initialized, privacyStarted, showing, pendingWin;
        int levelNumber;
        double Now => Time.realtimeSinceStartupAsDouble;
#if UNITY_IOS && !UNITY_EDITOR
        [System.Runtime.InteropServices.DllImport("__Internal")] static extern int OrcubTrackingStatus();
        [System.Runtime.InteropServices.DllImport("__Internal")] static extern void OrcubRequestTracking();
#endif
        bool Simulated => Application.isEditor || Application.platform != RuntimePlatform.IPhonePlayer;
        public bool RewardedReady => config.adsEnabled && policy.CanShowRewarded && !showing && (Simulated || (initialized && ConsentInformation.CanRequestAds() && rewarded != null && rewarded.CanShowAd()));
        public bool PrivacyOptionsRequired => !Simulated && ConsentInformation.PrivacyOptionsRequirementStatus == PrivacyOptionsRequirementStatus.Required;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Boot()
        {
            if (Instance == null) new GameObject("AdService").AddComponent<AdService>();
        }
        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            config = AdsConfig.Load();
            policy = new AdPolicy(config, Now);
            ClientGateway.AdCaps = (interval, interstitialCap, rewardedCap) =>
            {
                config.interstitialIntervalSeconds = Math.Max(0, interval);
                config.interstitialDailyCap = Math.Max(0, interstitialCap); config.rewardedDailyCap = Math.Max(0, rewardedCap);
            };
            AdsGateway.Service = this;
            // Delay all privacy forms until a completed level, including UMP's optional ATT message.
            if (PlayerPrefs.GetInt("Ads.FirstLevelCompleted", 0) != 0)
            {
#if UNITY_IOS && !UNITY_EDITOR
                // An interrupted first-win prompt must wait for another win, never a cold launch.
                if (OrcubTrackingStatus() != 0) StartCoroutine(BeginPrivacy());
#else
                StartCoroutine(BeginPrivacy());
#endif
            }
        }
        void Update()
        {
            while (callbacks.TryDequeue(out var action)) action();
        }
        public void RecordWin(int completedLevelNumber)
        {
            levelNumber = completedLevelNumber;
            pendingWin = true;
            policy.RecordWin();
            PlayerPrefs.SetInt("Ads.FirstLevelCompleted", 1);
            PlayerPrefs.Save();
            if (!privacyStarted) StartCoroutine(BeginPrivacy());
        }
        IEnumerator BeginPrivacy()
        {
            privacyStarted = true;
            if (Simulated || !config.adsEnabled) yield break;
#if UNITY_IOS && !UNITY_EDITOR
            // ATT is OS-persisted; never request it again once a choice exists.
            while (!Application.isFocused) yield return null;
            yield return new WaitForSecondsRealtime(1.5f);
            if (OrcubTrackingStatus() == 0)
            {
                OrcubRequestTracking();
                while (OrcubTrackingStatus() == 0) yield return null;
            }
#endif
            ConsentInformation.Update(new ConsentRequestParameters(), error => callbacks.Enqueue(() =>
            {
                if (error != null) { Debug.LogWarning("Ads consent update: " + error.Message); InitializeIfAllowed(); return; }
                ConsentForm.LoadAndShowConsentFormIfRequired(formError => callbacks.Enqueue(() =>
                {
                    if (formError != null) Debug.LogWarning("Ads consent form: " + formError.Message);
                    InitializeIfAllowed();
                }));
            }));
        }
        void InitializeIfAllowed()
        {
            if (initialized || !config.adsEnabled || !ConsentInformation.CanRequestAds()) return;
            initialized = true;
            MobileAds.SetRequestConfiguration(new RequestConfiguration { TestDeviceIds = new System.Collections.Generic.List<string>(config.testDeviceIds) });
            MobileAds.Initialize(_ => callbacks.Enqueue(() => { LoadRewarded(); LoadInterstitial(); }));
        }
        void LoadRewarded()
        {
            if (!config.adsEnabled || !ConsentInformation.CanRequestAds()) return;
            RewardedAd.Load(config.rewardedUnitId, new AdRequest(), (ad, error) => callbacks.Enqueue(() =>
            {
                if (error != null || ad == null) { ad?.Destroy(); StartCoroutine(Retry(LoadRewarded)); return; }
                rewarded?.Destroy(); rewarded = ad;
            }));
        }
        void LoadInterstitial()
        {
            if (!config.adsEnabled || !ConsentInformation.CanRequestAds()) return;
            InterstitialAd.Load(config.interstitialUnitId, new AdRequest(), (ad, error) => callbacks.Enqueue(() =>
            {
                if (error != null || ad == null) { ad?.Destroy(); StartCoroutine(Retry(LoadInterstitial)); return; }
                interstitial?.Destroy(); interstitial = ad;
            }));
        }
        IEnumerator Retry(Action load)
        {
            yield return new WaitForSecondsRealtime(config.reloadDelaySeconds);
            load();
        }
        public void ShowRewarded(string placement, Action onReward, Action onClosedWithoutReward)
        {
            placement = "rewarded_" + placement;
            TrackAd("ad_offer", placement);
            if (!RewardedReady) { FailAd(placement, "not_ready"); onClosedWithoutReward?.Invoke(); return; }
            showing = true;
            policy.RecordRewarded(Now);
            if (Simulated) { policy.RecordRewardedImpression(); TrackAd("ad_shown", placement); StartCoroutine(Simulate(() => { TrackAd("ad_reward", placement); onReward?.Invoke(); })); return; }
            var ad = rewarded; rewarded = null;
            ad.OnAdFullScreenContentOpened += () => callbacks.Enqueue(() => { policy.RecordRewardedImpression(); TrackAd("ad_shown", placement); });
            ad.OnAdPaid += value => callbacks.Enqueue(() => RecordPaid(placement, value, config.rewardedUnitId, ad.GetResponseInfo()?.GetMediationAdapterClassName()));
            bool earned = false, closed = false;
            void Finish()
            {
                if (closed) return;
                closed = true; showing = false;
                policy.RecordRewarded(Now);
                ad.Destroy(); LoadRewarded();
                if (!earned) onClosedWithoutReward?.Invoke();
            }
            ad.OnAdFullScreenContentClosed += () => callbacks.Enqueue(Finish);
            ad.OnAdFullScreenContentFailed += _ => callbacks.Enqueue(() => { FailAd(placement, "show_failed"); Finish(); });
            ad.Show(_ => callbacks.Enqueue(() =>
            {
                if (earned || closed) return;
                earned = true;
                TrackAd("ad_reward", placement);
                onReward?.Invoke();
            }));
        }
        IEnumerator Simulate(Action reward, bool isRewarded = true)
        {
            yield return new WaitForSecondsRealtime(config.editorAdDelaySeconds);
            showing = false;
            if (isRewarded) policy.RecordRewarded(Now);
            reward?.Invoke();
        }
        public void MaybeShowInterstitial(string placement) => MaybeShowInterstitial(placement, null);
        public void MaybeShowInterstitial(string placement, Action onFinished)
        {
            bool eligible = pendingWin && policy.CanShowInterstitial(levelNumber, true, Now);
            pendingWin = false;
            if (!eligible || showing || (!Simulated && (!ConsentInformation.CanRequestAds() || interstitial == null || !interstitial.CanShowAd())))
            { onFinished?.Invoke(); return; }
            TrackAd("ad_offer", placement);
            showing = true;
            if (Simulated) { policy.RecordInterstitial(Now); TrackAd("ad_shown", placement); StartCoroutine(Simulate(onFinished, false)); return; }
            var ad = interstitial; interstitial = null;
            bool closed = false;
            void Finish()
            {
                if (closed) return;
                closed = true; showing = false;
                ad.Destroy(); LoadInterstitial(); onFinished?.Invoke();
            }
            ad.OnAdPaid += value => callbacks.Enqueue(() => RecordPaid(placement, value, config.interstitialUnitId, ad.GetResponseInfo()?.GetMediationAdapterClassName()));
            ad.OnAdFullScreenContentOpened += () => callbacks.Enqueue(() => { policy.RecordInterstitial(Now); TrackAd("ad_shown", placement); });
            ad.OnAdFullScreenContentClosed += () => callbacks.Enqueue(Finish);
            ad.OnAdFullScreenContentFailed += _ => callbacks.Enqueue(() => { FailAd(placement, "show_failed"); Finish(); });
            ad.Show();
        }
        static void TrackAd(string kind, string placement) => ClientGateway.Track(kind, new { placement, network = "admob" });
        static void FailAd(string placement, string code) => ClientGateway.Track("ad_failed", new { placement, network = "admob", code });
        static void RecordPaid(string placement, AdValue value, string unit, string network)
        {
            string precision = value.Precision.ToString().ToLowerInvariant();
            if (precision == "publisherprovided") precision = "publisher_provided";
            ClientGateway.Paid?.Invoke(placement, value.Value, value.CurrencyCode, precision, unit, network ?? "admob");
        }
        public void ShowPrivacyOptions()
        {
            if (!PrivacyOptionsRequired || showing) return;
            ConsentForm.ShowPrivacyOptionsForm(error => callbacks.Enqueue(() =>
            {
                rewarded?.Destroy(); rewarded = null;
                interstitial?.Destroy(); interstitial = null;
                if (error != null) Debug.LogWarning("Ads privacy options: " + error.Message);
                if (initialized) { LoadRewarded(); LoadInterstitial(); } else InitializeIfAllowed();
            }));
        }
        void OnDestroy()
        {
            if (Instance != this) return;
            rewarded?.Destroy(); interstitial?.Destroy();
            AdsGateway.Service = null; Instance = null;
        }
    }
}
