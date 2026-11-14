using System;
using System.Collections;
using System.Collections.Concurrent;
using Orcub.Game;
using Orcub.Net;
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
        bool Simulated => Application.isEditor;
        bool Supported => Application.isEditor || Application.platform == RuntimePlatform.IPhonePlayer;
        Newtonsoft.Json.Linq.JToken deferredPolicy; bool deferredEntitlement, inLevel;
        Action deferredCaps;
        public void BeginLevel() { inLevel=true; }
        public void EndLevel() { inLevel=false; deferredCaps?.Invoke(); deferredCaps=null; if(deferredPolicy!=null) { var p=deferredPolicy; deferredPolicy=null; ApplyBackendPolicy(p,deferredEntitlement); } }
        public void ApplyBackendPolicy(Newtonsoft.Json.Linq.JToken p, bool entitlement)
        {
            policy.RemoveAds = entitlement;
            if(inLevel) { deferredPolicy=p?.DeepClone(); deferredEntitlement=entitlement; return; }
            config.firstInterstitialLevel = (int?)p?["interstitial_min_level"] ?? 8;
            config.levelsBetweenInterstitials = (int?)p?["interstitial_min_levels_apart"] ?? 3;
            config.maxInterstitialsPerSession = (bool?)p?["interstitial_enabled"] == false ? 0 : ((int?)p?["interstitial_session_cap"] ?? 6);
        }
        public bool RemoveAds { get => policy.RemoveAds; set => policy.RemoveAds = value; }
        public bool RewardedReady => Supported && config.adsEnabled && policy.CanShowRewarded && !showing && (Simulated || (initialized && ConsentInformation.CanRequestAds() && rewarded != null && rewarded.CanShowAd()));
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
                void Apply() { config.interstitialIntervalSeconds = Math.Max(0, interval);
                config.interstitialDailyCap = Math.Max(0, interstitialCap); config.rewardedDailyCap = Math.Max(0, rewardedCap); }
                if(inLevel) deferredCaps=Apply; else Apply();
            };
            AdsGateway.Service = this;
            Orcub.Core.ConsumableGateway.LevelStarted=BeginLevel;
            Orcub.Core.ConsumableGateway.LevelEnded=EndLevel;
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
            EndLevel();
            levelNumber = completedLevelNumber;
            pendingWin = true;
            policy.RecordWin();
            PlayerPrefs.SetInt("Ads.FirstLevelCompleted", 1);
            PlayerPrefs.Save();
            if (!privacyStarted) StartCoroutine(BeginPrivacy());
        }
        void OnApplicationFocus(bool focused)
        {
            if (focused && !privacyStarted && PlayerPrefs.GetInt("Ads.FirstLevelCompleted", 0) != 0)
                StartCoroutine(BeginPrivacy());
        }
        IEnumerator BeginPrivacy()
        {
            if (privacyStarted) yield break;
            privacyStarted = true;
            if (Simulated || !Supported || !config.adsEnabled) yield break;
            // UMP (including the EEA/UK form) must complete before requesting ATT.
            bool complete = false, consentOk = false;
            int generation = ++privacyGeneration;
            ConsentInformation.Update(new ConsentRequestParameters(), error => callbacks.Enqueue(() =>
            {
                if (generation != privacyGeneration) return;
                if (error != null) { complete = true; return; }
                ConsentForm.LoadAndShowConsentFormIfRequired(formError => callbacks.Enqueue(() =>
                {
                    if (generation != privacyGeneration) return;
                    consentOk = formError == null; complete = true;
                }));
            }));
            double deadline = Now + 30;
            while (!complete && Now < deadline) yield return null;
            if (!complete || !consentOk) { privacyGeneration++; privacyStarted = false; InitializeIfAllowed(); yield break; }
#if UNITY_IOS && !UNITY_EDITOR
            deadline = Now + 10;
            while (!Application.isFocused && Now < deadline) yield return null;
            if (Application.isFocused && OrcubTrackingStatus() == 0)
            {
                OrcubRequestTracking();
                while (Application.isFocused && OrcubTrackingStatus() == 0 && Now < deadline) yield return null;
            }
            // ATT may remain undetermined after an interruption. Ads may run without IDFA;
            // the next foreground retries the privacy flow instead of waiting indefinitely.
            if (OrcubTrackingStatus() == 0) privacyStarted = false;
#endif
            UpdateAnalyticsConsent();
            InitializeIfAllowed();
        }
        int privacyGeneration;
        void UpdateAnalyticsConsent()
        {
            string purposes = PlayerPrefs.GetString("IABTCF_PurposeConsents", "");
            bool granted = ConsentInformation.ConsentStatus == ConsentStatus.NotRequired ||
                (ConsentInformation.ConsentStatus == ConsentStatus.Obtained && purposes.Length >= 10 && purposes[0] == '1' && purposes[6] == '1' && purposes[8] == '1' && purposes[9] == '1');
            AnalyticsConsent.Resolve(granted);
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
        public static string NormalizeRewardedPlacement(string placement) => placement.Contains("lives") ? "rewarded_lives" : placement.StartsWith("rewarded_") ? placement : "rewarded_" + placement;
        public void RewardedOfferShown(string placement) => TrackAd("ad_offer", placement.StartsWith("rewarded_") ? placement : "rewarded_" + placement);
        public async void ShowRewarded(string placement, Action onReward, Action onClosedWithoutReward)
        {
            placement = NormalizeRewardedPlacement(placement);
            if (!RewardedReady) { FailAd(placement, "not_ready"); onClosedWithoutReward?.Invoke(); return; }
            showing = true;

            if (Simulated) { policy.RecordRewarded(Now); policy.RecordRewardedImpression(); TrackAd("ad_shown", placement); StartCoroutine(Simulate(() => { if(placement.Contains("lives")) { Orcub.Core.ConsumableGateway.AdRefillEarned?.Invoke("editor:"+Guid.NewGuid().ToString()); _ = Orcub.Core.Lives.RefillFromAd(placement); } TrackAd("ad_reward", placement); onReward?.Invoke(); })); return; }
            string nonce=null;
            if(placement.Contains("lives")) {
                try { nonce=await Orcub.Core.ConsumableGateway.PrepareAdRefill.Invoke(placement); } catch(Exception) { }
                if(string.IsNullOrEmpty(nonce)) { showing=false; onClosedWithoutReward?.Invoke(); return; }
            }
            policy.RecordRewarded(Now);
            var ad = rewarded; rewarded = null;
            if(nonce!=null) ad.SetServerSideVerificationOptions(new ServerSideVerificationOptions { UserId=ClientServices.Instance.PlayerId, CustomData=nonce });
            ad.OnAdFullScreenContentOpened += () => callbacks.Enqueue(() => { policy.RecordRewardedImpression(); TrackAd("ad_shown", placement); });
            ad.OnAdPaid += value => callbacks.Enqueue(() => RecordPaid(placement, value, config.rewardedUnitId, ad.GetResponseInfo()?.GetMediationAdapterClassName()));
            bool earned = false, closed = false, finalized = false;
            void Finish()
            {
                if (closed) return;
                closed = true;
                StartCoroutine(SettleReward());
            }
            IEnumerator SettleReward()
            {
                yield return new WaitForSecondsRealtime(.5f);
                finalized = true; showing = false; ad.Destroy(); LoadRewarded();
                if (!earned) onClosedWithoutReward?.Invoke();
            }
            ad.OnAdFullScreenContentClosed += () => callbacks.Enqueue(Finish);
            ad.OnAdFullScreenContentFailed += _ => callbacks.Enqueue(() => { FailAd(placement, "show_failed"); Finish(); });
            ad.Show(reward => callbacks.Enqueue(() =>
            {
                if (earned || finalized) return;
                earned = true;
                TrackAd("ad_reward", placement);
                if(nonce!=null) { Orcub.Core.ConsumableGateway.AdRefillEarned.Invoke(nonce); _ = Orcub.Core.Lives.RefillFromAd(placement); }
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
            bool eligible = Supported && pendingWin && policy.CanShowInterstitial(levelNumber, true, Now);
            pendingWin = false;
            if (!eligible || showing || (!Simulated && (!ConsentInformation.CanRequestAds() || interstitial == null || !interstitial.CanShowAd())))
            { onFinished?.Invoke(); return; }
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
                UpdateAnalyticsConsent();
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
            AdsGateway.Service = null; Orcub.Core.ConsumableGateway.LevelStarted=null; Orcub.Core.ConsumableGateway.LevelEnded=null; Instance = null;
        }
    }
}
