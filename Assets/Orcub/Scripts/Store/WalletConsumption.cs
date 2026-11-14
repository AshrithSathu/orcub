using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
namespace Orcub.Store
{
    public sealed class WalletConsumption
    {
        readonly IStoreBackend backend;
        readonly IStoreCache cache;
        readonly Wallet wallet;
        JArray pending;
        JObject provisional;
        Task reconciliation;
        string owner;
        public string AttemptReference { get; set; } = Guid.NewGuid().ToString();
        public WalletConsumption(IStoreBackend backend, IStoreCache cache, Wallet wallet)
        { this.backend = backend; this.cache = cache; this.wallet = wallet; }
        void Load()
        {
            var account = backend.PlayerId ?? "unregistered";
            if (owner == account && pending != null) return;
            owner = account;
            var json = cache.Read("consume." + owner);
            pending = string.IsNullOrEmpty(json) ? new JArray() : JArray.Parse(json);
            // Offline first launch has no server identity yet. Transfer once on registration.
            if (owner != "unregistered")
            {
                var unregistered = cache.Read("consume.unregistered");
                if (!string.IsNullOrEmpty(unregistered))
                {
                    foreach (var item in JArray.Parse(unregistered).OfType<JObject>())
                        if (!pending.OfType<JObject>().Any(x => (string)x["idempotency_key"] == (string)item["idempotency_key"])) pending.Add(item.DeepClone());
                    Save(); cache.Write("consume.unregistered", "");
                }
            }
            // Old acknowledged reservations are already reflected in the cached wallet.
            foreach (var item in pending.OfType<JObject>().Where(x => (bool?)x["ack"] == true).ToArray()) pending.Remove(item);
            Save();
            json = cache.Read("provisional." + owner);
            provisional = string.IsNullOrEmpty(json) ? new JObject {
                ["lives"] = 5, ["lives_updated_at"] = DateTimeOffset.UtcNow.ToString("o"), ["inventory"] = new JObject()
            } : JObject.Parse(json);
            cache.Write("provisional." + owner, provisional.ToString());
            wallet.LoadCache();
        }
        void Save() => cache.Write("consume." + owner, pending.ToString());
        JObject Snapshot => wallet.Snapshot ?? provisional;
        static bool TryTime(JToken token, out DateTimeOffset time)
        {
            if (token?.Type == JTokenType.Date) { time = token.ToObject<DateTimeOffset>(); return true; }
            return DateTimeOffset.TryParse((string)token, out time);
        }
        static void Regenerate(ref int count, ref DateTimeOffset stamp, DateTimeOffset now)
        {
            var ticks = Math.Max(0, Math.Floor((now - stamp).TotalSeconds / 1800));
            count = (int)Math.Min(5, count + ticks);
            stamp = count >= 5 ? now : stamp.AddSeconds(ticks * 1800);
        }
        int LifeCount(DateTimeOffset now, out DateTimeOffset stamp)
        {
            var s = Snapshot;
            stamp = TryTime(s["lives_updated_at"], out var parsed) ? parsed : now;
            if (TryTime(s["unlimited_until"], out var until) && now < until) return 5;
            int count = (int?)s["lives"] ?? 5;
            foreach (var item in pending.OfType<JObject>().Where(x => (string)x["kind"] == "life"))
            {
                var at = TryTime(item["created_at"], out var time) ? time : stamp;
                if (at < stamp) at = stamp;
                Regenerate(ref count, ref stamp, at);
                count = Math.Max(0, count - ((int?)item["count"] ?? 1));
            }
            Regenerate(ref count, ref stamp, now);
            return count;
        }
        public int Count(string kind, DateTimeOffset now)
        {
            Load();
            if (kind == "life") return LifeCount(now, out _);
            int count = (int?)Snapshot["inventory"]?[kind.Substring(8)] ?? 0;
            int reserved = pending.OfType<JObject>().Where(x => (string)x["kind"] == kind).Sum(x => (int?)x["count"] ?? 1);
            return Math.Max(0, count - reserved);
        }
        public int SecondsToNext(DateTimeOffset now)
        {
            Load();
            if (LifeCount(now, out var stamp) >= 5) return 0;
            return 1800 - (int)Math.Max(0, (now - stamp).TotalSeconds) % 1800;
        }
        public bool TrySpend(string kind)
        {
            Load();
            if (Count(kind, DateTimeOffset.UtcNow) <= 0) return false;
            pending.Add(new JObject { ["kind"] = kind, ["count"] = 1, ["attempt_ref"] = AttemptReference,
                ["idempotency_key"] = Guid.NewGuid().ToString(), ["created_at"] = DateTimeOffset.UtcNow.ToString("o") });
            Save(); _ = Reconcile(); return true;
        }
        public Task<bool> Spend(string kind) => Task.FromResult(TrySpend(kind));
        public Task Reconcile()
        {
            if (reconciliation == null || reconciliation.IsCompleted) reconciliation = ReconcileCore();
            return reconciliation;
        }
        async Task ReconcileCore()
        {
            // Yield so TrySpend always exposes the local decrement before any acknowledgement.
            await Task.Yield();
            Load(); var account = backend.PlayerId; var queue = pending; var keyOwner = owner;
            if (string.IsNullOrEmpty(account)) return;
            try
            {
                foreach (var item in queue.OfType<JObject>().ToArray())
                {
                    if (backend.PlayerId != account) return;
                    try
                    {
                        await wallet.Consume(item, account, () => {
                            queue.Remove(item); cache.Write("consume." + keyOwner, queue.ToString());
                        });
                    }
                    catch (StoreHttpException e) when (e.Status >= 400 && e.Status < 500)
                    {
                        if (backend.PlayerId != account) return;
                        // Server wins conflicts. Keep the gameplay benefit; never claw it back.
                        queue.Remove(item); cache.Write("consume." + keyOwner, queue.ToString());
                        Debug.LogWarning("Dropping wallet consume after HTTP " + e.Status);
                        await wallet.Refresh();
                    }
                }
            }
            catch (Exception) { /* Transport/5xx: retain the same durable keys for the next retry. */ }
        }
    }
}
