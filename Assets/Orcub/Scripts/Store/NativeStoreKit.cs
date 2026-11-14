using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Orcub.Store
{
    public sealed class NativeStoreKit : MonoBehaviour, IStoreKit
    {
        public event Action<StoreTransaction> TransactionUpdated;
        readonly Dictionary<string, TaskCompletionSource<JObject>> calls = new Dictionary<string, TaskCompletionSource<JObject>>();
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void OrcubStoreCall(string receiver, string json);
#endif
        async Task<JObject> Call(string action, object payload = null)
        {
#if UNITY_IOS && !UNITY_EDITOR
            var id = Guid.NewGuid().ToString(); var source = new TaskCompletionSource<JObject>(); calls.Add(id, source);
            try
            {
                OrcubStoreCall(gameObject.name, new JObject { ["id"] = id, ["action"] = action, ["payload"] = payload == null ? new JObject() : JToken.FromObject(payload) }.ToString());
                if (await Task.WhenAny(source.Task, Task.Delay(120000)) != source.Task) throw new TimeoutException("store_timeout");
                return await source.Task;
            }
            finally { calls.Remove(id); }
#else
            await Task.CompletedTask;
            throw new PlatformNotSupportedException("StoreKit requires iOS; inject IStoreKit for EditMode tests");
#endif
        }
        [UnityEngine.Scripting.Preserve]
        public void StoreResult(string json)
        {
            var value = JObject.Parse(json);
            if ((string)value["id"] == "update") { TransactionUpdated?.Invoke(value["transaction"].ToObject<StoreTransaction>()); return; }
            if (!calls.TryGetValue((string)value["id"], out var source)) return;
            if (value["error"] != null) source.TrySetException(new InvalidOperationException((string)value["error"]));
            else source.TrySetResult(value);
        }
        public async Task<IReadOnlyList<StoreProduct>> LoadProducts(string[] ids) => (await Call("products", new { ids }))["products"].ToObject<List<StoreProduct>>();
        public async Task<StoreTransaction> Purchase(string id, string appAccountToken) => (await Call("purchase", new { product = id, account = appAccountToken }))["transaction"]?.ToObject<StoreTransaction>();
        public async Task<IReadOnlyList<StoreTransaction>> Unfinished() => (await Call("unfinished"))["transactions"].ToObject<List<StoreTransaction>>();
        public async Task<IReadOnlyList<StoreTransaction>> Restore() => (await Call("restore"))["transactions"].ToObject<List<StoreTransaction>>();
        public async Task Finish(string transactionId) { await Call("finish", new { transaction = transactionId }); }
    }
}
