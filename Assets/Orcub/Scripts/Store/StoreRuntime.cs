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
            DontDestroyOnLoad(gameObject);
            var kit = gameObject.AddComponent<NativeStoreKit>();
            Service = new StoreService(kit, this, this);
            consumption = new WalletConsumption(this, this, Service.Wallet); Consumption = consumption;
            ConsumableGateway.Count = kind => consumption.Count(kind, DateTimeOffset.UtcNow);
            ConsumableGateway.Spend = consumption.TrySpend;
            ConsumableGateway.BeginAttempt = id => consumption.AttemptReference = id;
            ConsumableGateway.SecondsToNext = () => consumption.SecondsToNext(DateTimeOffset.UtcNow);
            ConsumableGateway.Refill = Refill;
            Service.Wallet.Changed += () => { if (AdService.Instance != null) AdService.Instance.RemoveAds = Service.Wallet.RemoveAds; };
            try { await Service.Initialize(); } catch (Exception) { Debug.LogWarning("Store unavailable; retry on foreground"); }
        }
        async void Refill()
        {
            string key = Read("refill." + PlayerId);
            if (string.IsNullOrEmpty(key)) { key = Guid.NewGuid().ToString(); Write("refill." + PlayerId, key); }
            try { await Service.Wallet.Spend(new SpendSink("refill_lives", key), key); Write("refill." + PlayerId, ""); }
            catch (Exception) { Debug.LogWarning("Wallet refill unavailable"); }
        }
        void Update()
        {
            if (consumption != null && Time.realtimeSinceStartupAsDouble >= nextReconcile)
            { nextReconcile = Time.realtimeSinceStartupAsDouble + 15; _ = consumption.Reconcile(); }
            if (owner == PlayerId) return;
            owner = PlayerId;
            if (Service != null) _ = Reload();
        }
        async Task Reload() { try { await Service.Initialize(); await consumption.Reconcile(); } catch (Exception) { } }
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
            if (response.responseCode != 200) throw new StoreHttpException(response.responseCode);
            var result = JObject.Parse(response.downloadHandler.text);
            if (path.StartsWith("/v1/config") && AdService.Instance != null) AdService.Instance.ApplyBackendPolicy(result["ad_policy"], Service.Wallet.RemoveAds || (bool?)result["player_value"]?["remove_ads"] == true);
            return result;
        }
        public void Track(string kind, object data) => ClientServices.Instance.Track(kind, data);
        public string Read(string key) => PlayerPrefs.GetString("orcub.store." + key, "");
        public void Write(string key, string value) { PlayerPrefs.SetString("orcub.store." + key, value); PlayerPrefs.Save(); }
        async void OnApplicationFocus(bool focused)
        {
            if (!focused || Service == null) return;
            try { await PrepareAccount(); await Service.Initialize(); } catch (Exception) { }
        }
        void OnDestroy() { Consumption = null; ConsumableGateway.BeginAttempt = null; ConsumableGateway.Count = null; ConsumableGateway.Spend = null; ConsumableGateway.Refill = null; ConsumableGateway.SecondsToNext = null; Service = null; }
    }
}
