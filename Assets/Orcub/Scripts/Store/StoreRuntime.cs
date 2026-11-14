using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Orcub.Net;
using Orcub.Ads;
using UnityEngine;

namespace Orcub.Store
{
    public sealed class StoreRuntime : MonoBehaviour, IStoreBackend, IStoreCache
    {
        public static StoreService Service { get; private set; }
        public string PlayerId => ClientServices.Instance?.PlayerId;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot() { if (Service == null) new GameObject("OrcubStore").AddComponent<StoreRuntime>(); }
        async void Start()
        {
            DontDestroyOnLoad(gameObject);
            var kit = gameObject.AddComponent<NativeStoreKit>();
            Service = new StoreService(kit, this, this);
            Service.Wallet.Changed += () => { if (AdService.Instance != null) AdService.Instance.RemoveAds = Service.Wallet.RemoveAds; };
            try { await Service.Initialize(); } catch (Exception) { Debug.LogWarning("Store unavailable; retry on foreground"); }
        }
        public async Task PrepareAccount()
        {
            var deadline = Time.realtimeSinceStartupAsDouble + 20;
            while (ClientServices.Instance == null || !ClientServices.Instance.Ready)
            { if (Time.realtimeSinceStartupAsDouble > deadline) throw new TimeoutException(); await Task.Yield(); }
            await ClientServices.Instance.Account.Link();
        }
        public async Task<JObject> Request(string path, string method, object body = null)
        {
            using var response = await ClientServices.Instance.Request(path, method, body);
            if (response.responseCode != 200) throw new StoreHttpException(response.responseCode);
            return JObject.Parse(response.downloadHandler.text);
        }
        public void Track(string kind, object data) => ClientServices.Instance.Track(kind, data);
        public string Read(string key) => PlayerPrefs.GetString("orcub.store." + key, "");
        public void Write(string key, string value) { PlayerPrefs.SetString("orcub.store." + key, value); PlayerPrefs.Save(); }
        async void OnApplicationFocus(bool focused)
        {
            if (!focused || Service == null) return;
            try { await PrepareAccount(); await Service.Initialize(); } catch (Exception) { }
        }
        void OnDestroy() { Service = null; }
    }
}
