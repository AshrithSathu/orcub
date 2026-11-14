using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
namespace Orcub.Store
{
    public sealed class WalletConsumption
    {
        readonly IStoreBackend backend;
        readonly IStoreCache cache;
        readonly Wallet wallet;
        JArray pending;
        bool running;
        Task reconciliation;
        string owner;
        public string AttemptReference { get; set; } = Guid.NewGuid().ToString();
        public WalletConsumption(IStoreBackend backend, IStoreCache cache, Wallet wallet)
        { this.backend = backend; this.cache = cache; this.wallet = wallet; }
        void Load()
        {
            if (owner == backend.PlayerId && pending != null) return;
            owner = backend.PlayerId;
            var json = cache.Read("consume." + owner);
            pending = string.IsNullOrEmpty(json) ? new JArray() : JArray.Parse(json);
        }
        void Save() => cache.Write("consume." + owner, pending.ToString());
        public int Count(string kind, DateTimeOffset now)
        {
            Load(); var s = wallet.Snapshot;
            if (s == null) return 0;
            int count;
            if (kind == "life")
            {
                if (DateTimeOffset.TryParse((string)s["unlimited_until"], out var until) && now < until) return 5;
                count = (int?)s["lives"] ?? 0;
                if (DateTimeOffset.TryParse((string)s["lives_updated_at"], out var stamp))
                    count = Math.Min(5, count + (int)Math.Max(0, Math.Floor((now - stamp).TotalSeconds / 1800)));
            }
            else count = (int?)s["inventory"]?[kind.Substring(8)] ?? 0;
            int reserved = pending.OfType<JObject>().Count(x => (string)x["kind"] == kind && !(bool)x["ack"]);
            int authorised = pending.OfType<JObject>().Count(x => (string)x["kind"] == kind && (bool)x["ack"] && (string)x["attempt_ref"] == AttemptReference);
            return Math.Max(0, count - reserved + authorised);
        }
        public int SecondsToNext(DateTimeOffset now)
        {
            if (Count("life", now) >= 5) return 0;
            if (!DateTimeOffset.TryParse((string)wallet.Snapshot?["lives_updated_at"], out var stamp)) return 1800;
            return 1800 - (int)Math.Max(0, (now - stamp).TotalSeconds) % 1800;
        }
        // The synchronous board API cannot await authorisation. Reserve once, return false,
        // then allow a repeated action only after acknowledgement. Offline play is never authorised.
        public bool TrySpend(string kind)
        {
            Load();
            var ready = pending.OfType<JObject>().FirstOrDefault(x => (string)x["kind"] == kind && (string)x["attempt_ref"] == AttemptReference && (bool)x["ack"]);
            if (ready != null) { pending.Remove(ready); Save(); return true; }
            if (pending.OfType<JObject>().Any(x => (string)x["kind"] == kind && (string)x["attempt_ref"] == AttemptReference)) { _ = Reconcile(); return false; }
            if (Count(kind, DateTimeOffset.UtcNow) <= 0) return false;
            pending.Add(new JObject { ["kind"] = kind, ["count"] = 1, ["attempt_ref"] = AttemptReference, ["idempotency_key"] = Guid.NewGuid().ToString(), ["ack"] = false });
            Save(); _ = Reconcile(); return false;
        }
        public async Task<bool> Spend(string kind)
        {
            if (TrySpend(kind)) return true;
            await Reconcile();
            Load();
            var ready = pending.OfType<JObject>().FirstOrDefault(x => (string)x["kind"] == kind && (string)x["attempt_ref"] == AttemptReference && (bool)x["ack"]);
            if (ready == null) return false;
            pending.Remove(ready); Save(); return true;
        }
        public Task Reconcile()
        {
            if (reconciliation == null || reconciliation.IsCompleted) reconciliation = ReconcileCore();
            return reconciliation;
        }
        async Task ReconcileCore()
        {
            if (running) return;
            Load(); running = true; var account = owner;
            try
            {
                foreach (var item in pending.OfType<JObject>().ToArray())
                {
                    if ((bool)item["ack"]) continue;
                    try
                    {
                        await wallet.Consume(item, account);
                        if (backend.PlayerId != account) return;
                        item["ack"] = true; Save();
                    }
                    catch (StoreHttpException e) when (e.Status == 409 || e.Status == 400) { pending.Remove(item); Save(); await wallet.Refresh(); }
                    catch (Exception) { return; }
                }
            }
            finally { running = false; }
        }
    }
}
