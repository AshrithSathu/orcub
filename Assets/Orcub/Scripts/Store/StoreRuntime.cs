using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Orcub.Net;
using Orcub.Ads;
using Orcub.Core;
using UnityEngine;

namespace Orcub.Store
{
    public sealed class StoreRuntime : MonoBehaviour, IStoreBackend, IStoreCache
    {
        public static WalletConsumption Consumption { get; private set; }
        WalletConsumption consumption;
        string owner;
        double nextReconcile;
        public static StoreService Service { get; private set; }
        public string PlayerId => ClientServices.Instance?.PlayerId;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot() { if (Service == null) new GameObject("OrcubStore").AddComponent<StoreRuntime>(); }
        async void Start()
        {
            instance=this; DontDestroyOnLoad(gameObject);
            var kit = gameObject.AddComponent<NativeStoreKit>();
            Service = new StoreService(kit, this, this);
            Orcub.Game.ClientGateway.ClaimLevelReward=ClaimLevelReward;
            Orcub.Game.ClientGateway.BoosterUnlock=Service.ClaimBoosterUnlock;
            consumption = new WalletConsumption(this, this, Service.Wallet); Consumption = consumption;
            ConsumableGateway.Count = kind => consumption.Count(kind, DateTimeOffset.UtcNow);
            ConsumableGateway.Spend = consumption.TrySpend;
            ConsumableGateway.BeginAttempt = id => consumption.AttemptReference = id;
            ConsumableGateway.SecondsToNext = () => consumption.SecondsToNext(DateTimeOffset.UtcNow);
            ConsumableGateway.Refill = Refill;
            ConsumableGateway.PrepareAdRefill=PrepareAdRefill;
            ConsumableGateway.AdRefillEarned=AdRefillEarned;
            ConsumableGateway.FlushSpends=consumption.Reconcile;
            ConsumableGateway.HasPendingSpends=()=>consumption.HasPending;
            ConsumableGateway.MigrateSpends=consumption.Migrate;
            ConsumableGateway.RefillFromAd = RefillLivesFromAd;
            ConsumableGateway.RefillWithGold = RefillWithGold;
            Service.Wallet.Changed += () => { if (AdService.Instance != null) AdService.Instance.RemoveAds = Service.Wallet.RemoveAds; };
            try { await consumption.Reconcile(); await Service.Initialize(); await consumption.Reconcile(); } catch (Exception) { Debug.LogWarning("Store unavailable; retry on foreground"); }
        }
        bool retryingUnlocks;
        int unlockFailures;
        double nextUnlockRetry;
        async Task RetryUnlocks()
        {
            if(Time.realtimeSinceStartupAsDouble < nextUnlockRetry || retryingUnlocks || Service==null || string.IsNullOrEmpty(PlayerId)) return;
            retryingUnlocks=true;
            try { foreach(var kind in Orcub.Game.BoosterUnlocks.Due(Progress.HighestUnlocked+1,
                kind => Read("unlock-claimed."+PlayerId+"."+kind)=="1"))
                if(!await Service.ClaimBoosterUnlock(kind)) {
                    unlockFailures=Math.Min(unlockFailures+1,8);
                    nextUnlockRetry=Time.realtimeSinceStartupAsDouble+15*Math.Pow(2,unlockFailures);
                    return;
                }
                unlockFailures=0; nextUnlockRetry=0;
            } finally { retryingUnlocks=false; }
        }
        string earnedNonce; bool earnedApplied;
        public static async Task<string> PrepareAdRefill(string placement)
        {
            if (Service==null || ClientServices.Instance==null) return null;
            placement=AdService.NormalizeRewardedPlacement(placement);
            using var response=await ClientServices.Instance.Request("/v1/ads/nonce","POST",new { player_id=ClientServices.Instance.PlayerId,placement });
            if(response.responseCode!=200) return null;
            return (string)JObject.Load(new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(response.downloadHandler.text)) { DateParseHandling=Newtonsoft.Json.DateParseHandling.None })["nonce"];
        }
        public static void AdRefillEarned(string nonce) { if (instance!=null) { instance.earnedNonce=nonce; instance.earnedApplied=false; } }
        static StoreRuntime instance;
        public static Task<bool> RefillLivesFromAd(string placement)
        {
            if(instance==null || string.IsNullOrEmpty(instance.earnedNonce)) return Task.FromResult(false);
            if(instance.earnedApplied) return Task.FromResult(true);
            instance.earnedApplied=Consumption.ApplyAdRefill(instance.earnedNonce);
            return Task.FromResult(instance.earnedApplied);
        }
        void Refill() { _ = RefillLivesFromAd("rewarded_lives"); }
        public static async Task<bool> RefillWithGold()
        {
            if(Service==null) return false;
            string key=instance.Read("refill."+instance.PlayerId);
            if(string.IsNullOrEmpty(key)) { key=Guid.NewGuid().ToString(); instance.Write("refill."+instance.PlayerId,key); }
            try { await Service.Wallet.Spend(new SpendSink("refill_lives",key),key); instance.Write("refill."+instance.PlayerId,""); return Consumption.Count("life",DateTimeOffset.UtcNow)==5; }
            catch(Exception) { return false; }
        }
        void Update()
        {
            if (consumption != null && Time.realtimeSinceStartupAsDouble >= nextReconcile)
            { nextReconcile = Time.realtimeSinceStartupAsDouble + 15; _ = consumption.Reconcile(); _ = RetryUnlocks(); }
            if (owner == PlayerId) return;
            owner = PlayerId; unlockFailures=0; nextUnlockRetry=0;
            if (Service != null) _ = Reload();
        }
        async Task Reload() { try { await consumption.Reconcile(); await Service.Initialize(); await consumption.Reconcile(); } catch (Exception) { } }
        public async Task PrepareAccount()
        {
            var deadline = Time.realtimeSinceStartupAsDouble + 20;
            while (ClientServices.Instance == null || !ClientServices.Instance.Ready || ClientServices.Instance.Account?.User == null)
            { if (Time.realtimeSinceStartupAsDouble > deadline) throw new TimeoutException(); await Task.Yield(); }
            await ClientServices.Instance.Account.Link();
            if (!ClientServices.Instance.Account.Linked) throw new InvalidOperationException("account_not_linked");
        }
        public async Task<JObject> Request(string path, string method, object body = null)
        {
            using var response = await ClientServices.Instance.Request(path, method, body);
            if(response.responseCode==401) {
                var previous=PlayerId; response.Dispose(); await ClientServices.Instance.RenewInstall();
                path=path.Replace(previous,PlayerId);
                if(body!=null) { var renewed=JObject.FromObject(body); if(renewed["player_id"]!=null) renewed["player_id"]=PlayerId; body=renewed; }
                return await RetryRequest(path,method,body);
            }
            if (response.responseCode != 200) { double.TryParse(response.GetResponseHeader("Retry-After"),out var retry); throw new StoreHttpException(response.responseCode, response.downloadHandler.text,retry); }
            var result = JObject.Load(new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(response.downloadHandler.text)) { DateParseHandling=Newtonsoft.Json.DateParseHandling.None });
            if (path.StartsWith("/v1/config") && AdService.Instance != null) AdService.Instance.ApplyBackendPolicy(result["ad_policy"], Service.Wallet.RemoveAds || (bool?)result["player_value"]?["remove_ads"] == true);
            return result;
        }
        async Task<JObject> RetryRequest(string path,string method,object body)
        {
            using var response=await ClientServices.Instance.Request(path,method,body);
            if(response.responseCode!=200) { double.TryParse(response.GetResponseHeader("Retry-After"),out var retry); throw new StoreHttpException(response.responseCode,response.downloadHandler.text,retry); }
            return JObject.Load(new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(response.downloadHandler.text)) { DateParseHandling=Newtonsoft.Json.DateParseHandling.None });
        }
        public void Track(string kind, object data) => ClientServices.Instance.Track(kind, data);
        public string Read(string key) => PlayerPrefs.GetString("orcub.store." + key, "");
        public void Write(string key, string value) { if(PlayerPrefs.GetString("orcub.store."+key, "") == value) return; PlayerPrefs.SetString("orcub.store." + key, value); prefsDirty=true; if(key.StartsWith("consume.") || key.StartsWith("adgrants.") || key.StartsWith("unlock.")) FlushPrefs(); }
        void FlushPrefs() { if(prefsDirty) { PlayerPrefs.Save(); prefsDirty=false; } }
        void OnApplicationQuit() => FlushPrefs();
        bool prefsDirty; double nextPrefsSave;
        void LateUpdate() { if(prefsDirty && Time.realtimeSinceStartupAsDouble>=nextPrefsSave) { PlayerPrefs.Save(); prefsDirty=false; nextPrefsSave=Time.realtimeSinceStartupAsDouble+2; } }
        void OnApplicationPause(bool paused) { if(paused && prefsDirty) { PlayerPrefs.Save(); prefsDirty=false; } }
        public static async Task<int> ClaimLevelReward(int levelIndex, string eventId)
        {
            if(instance==null || !Guid.TryParse(eventId,out _)) throw new InvalidOperationException("reward_not_ready");
            await ClientServices.Instance.Flush();
            return await Service.ClaimLevelReward(eventId);
        }
        double nextForeground;
        async void OnApplicationFocus(bool focused)
        {
            if(!focused) FlushPrefs();
            if (!focused || Service == null || Time.realtimeSinceStartupAsDouble<nextForeground) return;
            nextForeground=Time.realtimeSinceStartupAsDouble+300;
            try { await consumption.Reconcile(); await PrepareAccount(); await Service.Initialize(); } catch (Exception) { }
            await consumption.Reconcile();
        }
        void OnDestroy() { Orcub.Game.ClientGateway.ClaimLevelReward=null; Orcub.Game.ClientGateway.BoosterUnlock=null; Consumption = null; ConsumableGateway.HasPendingSpends=null; ConsumableGateway.PrepareAdRefill=null; ConsumableGateway.AdRefillEarned=null; ConsumableGateway.FlushSpends=null; ConsumableGateway.MigrateSpends=null; ConsumableGateway.RefillFromAd=null; ConsumableGateway.RefillWithGold=null; instance=null; ConsumableGateway.BeginAttempt = null; ConsumableGateway.Count = null; ConsumableGateway.Spend = null; ConsumableGateway.Refill = null; ConsumableGateway.SecondsToNext = null; Service = null; }
    }
}
