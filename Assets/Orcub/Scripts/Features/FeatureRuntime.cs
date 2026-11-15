using Orcub.Game;
using UnityEngine;

namespace Orcub.Features
{
    /// <summary>
    /// Composition root for the feature UI ports (Game cannot reference Store / Daily): binds ShopGateway.Port
    /// (and the daily port) to the live services and keeps following their instances.
    /// </summary>
    public sealed class FeatureRuntime : MonoBehaviour
    {
        readonly StoreShopPort _shop = new StoreShopPort();
        readonly DailyPort _daily = new DailyPort();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot() => new GameObject("OrcubFeatures").AddComponent<FeatureRuntime>();

        void Awake()
        {
            DontDestroyOnLoad(gameObject);
            ShopGateway.Port = _shop;
            ShopGateway.NotifyPortChanged();
            DailyGateway.Port = _daily;
        }

        void Update() { _shop.Poll(); _daily.Poll(); }

        void OnDestroy()
        {
            if (ShopGateway.Port == _shop) ShopGateway.Port = null;
            if (DailyGateway.Port == _daily) DailyGateway.Port = null;
        }
    }
}
