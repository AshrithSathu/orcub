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

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot() => new GameObject("OrcubFeatures").AddComponent<FeatureRuntime>();

        void Awake()
        {
            DontDestroyOnLoad(gameObject);
            ShopGateway.Port = _shop;
            ShopGateway.NotifyPortChanged();
        }

        void Update() => _shop.Poll();

        void OnDestroy()
        {
            if (ShopGateway.Port == _shop) ShopGateway.Port = null;
        }
    }
}
