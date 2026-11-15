using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Orcub.Store
{
    public sealed class StoreProduct
    {
        public string Id, Title, LocalizedPrice;
    }
    public sealed class StoreTransaction
    {
        public string Id, ProductId, Jws;
    }
    public interface IStoreKit
    {
        event Action<StoreTransaction> TransactionUpdated;
        Task<IReadOnlyList<StoreProduct>> LoadProducts(string[] ids);
        Task<StoreTransaction> Purchase(string id, string appAccountToken);
        Task<IReadOnlyList<StoreTransaction>> Unfinished();
        Task<IReadOnlyList<StoreTransaction>> Restore();
        Task Finish(string transactionId);
    }
    public sealed class StoreHttpException : Exception
    {
        public long Status { get; }
        public double RetryAfter { get; }
        public StoreHttpException(long status, string error = null, double retryAfter = 0) : base(error ?? "store_http_" + status) { Status = status; RetryAfter = retryAfter; }
    }
    public interface IStoreBackend
    {
        string PlayerId { get; }
        Task PrepareAccount();
        Task<JObject> Request(string path, string method, object body = null);
        void Track(string kind, object data);
    }
    public interface IStoreCache
    {
        string Read(string key);
        void Write(string key, string value);
    }
    public sealed class Wallet
    {
        readonly IStoreBackend backend;
        readonly IStoreCache cache;
        readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        public long Balance { get; private set; }
        public bool IsCached { get; private set; } = true;
        public bool RemoveAds { get; private set; }
        public DateTimeOffset LastConfirmedRefresh { get; private set; }
        public JObject Snapshot { get; private set; }
        public event Action Changed;
        internal Wallet(IStoreBackend backend, IStoreCache cache) { this.backend = backend; this.cache = cache; }
        internal void LoadCache()
        {
            if (Snapshot != null && (string)Snapshot["player_id"] != backend.PlayerId)
            { Snapshot = null; Balance = 0; RemoveAds = false; IsCached = true; Changed?.Invoke(); }
            var json = cache.Read("wallet." + backend.PlayerId);
            try { if (!string.IsNullOrEmpty(json)) Apply(JObject.Load(new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(json)) { DateParseHandling=Newtonsoft.Json.DateParseHandling.None }), true); }
            catch (Exception) { Snapshot=null; Balance=0; RemoveAds=false; cache.Write("quarantine.wallet." + backend.PlayerId, json); cache.Write("wallet." + backend.PlayerId, ""); }
            IsCached = true;
        }
        internal void Apply(JObject value, bool cached = false, Action afterPersist = null)
        {
            if ((string)value["player_id"] != backend.PlayerId) throw new InvalidOperationException("wallet_account_mismatch");
            if(value["gold"]?.Type!=JTokenType.Integer || value["remove_ads"]?.Type!=JTokenType.Boolean || (value["inventory"]!=null && !(value["inventory"] is JObject))) throw new InvalidOperationException("wallet_cache_shape");
            foreach(var field in new[]{"applied_keys","rewarded_nonces"}) if(value[field]!=null && (!(value[field] is JArray array) || array.Any(x=>x.Type!=JTokenType.String))) throw new InvalidOperationException("wallet_cache_keys");
            if(value["lives"]!=null && value["lives"].Type!=JTokenType.Integer) throw new InvalidOperationException("wallet_cache_lives");
            if(value["inventory"] is JObject inventory && inventory.Properties().Any(x=>x.Value.Type!=JTokenType.Integer)) throw new InvalidOperationException("wallet_cache_inventory");
            Snapshot = (JObject)value.DeepClone(); Balance = (long)value["gold"]; RemoveAds = (bool)value["remove_ads"];
            if(!cached) { LastConfirmedRefresh=DateTimeOffset.UtcNow; cache.Write("wallet.confirmed."+backend.PlayerId,LastConfirmedRefresh.ToString("o")); }
            else LastConfirmedRefresh=DateTimeOffset.TryParse(cache.Read("wallet.confirmed."+backend.PlayerId),out var confirmed) ? confirmed : DateTimeOffset.MinValue;
            IsCached = cached; cache.Write("wallet." + backend.PlayerId, value.ToString()); afterPersist?.Invoke(); Changed?.Invoke();
        }
        public Func<string> OldestPending { get; set; }
        public async Task Refresh()
        {
            await gate.WaitAsync();
            try { Apply(await backend.Request("/v1/wallet?player_id=" + backend.PlayerId + (string.IsNullOrEmpty(OldestPending?.Invoke()) ? "" : "&applied_since="+Uri.EscapeDataString(OldestPending())), "GET")); }
            finally { gate.Release(); }
        }
        internal async Task<JObject> Submit(string path, object body)
        {
            await gate.WaitAsync();
            try
            {
                var response = await backend.Request(path, "POST", body);
                Apply((JObject)response["wallet"]); return response;
            }
            finally { gate.Release(); }
        }
        internal async Task Consume(JObject item, string owner, Action acknowledged)
        {
            await gate.WaitAsync();
            try
            {
                if (backend.PlayerId != owner) throw new InvalidOperationException("account_changed");
                var response = await backend.Request("/v1/wallet/consume", "POST", new { player_id = owner, kind = (string)item["kind"], count = (int)item["count"], attempt_ref = (string)item["attempt_ref"], idempotency_key = (string)item["idempotency_key"], occurred_at = (string)item["created_at"], rewarded_nonce = (string)item["rewarded_nonce"] });
                Apply((JObject)response["wallet"], afterPersist: acknowledged);
            }
            finally { gate.Release(); }
        }
        // The returned effect is null on replay, including across launches. Persist before exposing it.
        public async Task<JObject> Spend(SpendSink sink, string idempotencyKey)
        {
            if (!Guid.TryParse(idempotencyKey, out _)) throw new ArgumentException("UUID required", nameof(idempotencyKey));
            await gate.WaitAsync();
            try
            {
                var response = await backend.Request("/v1/wallet/spend", "POST", new { player_id = backend.PlayerId,
                    idempotency_key = idempotencyKey, reason = sink.Reason, ref_id = sink.ReferenceId, booster = sink.Booster });
                Apply((JObject)response["wallet"]);
                var key = "spend." + backend.PlayerId + "." + idempotencyKey;
                bool replay = cache.Read(key) == "1";
                cache.Write(key, "1");
                if (!replay) backend.Track("wallet_spend", new { metric_count = 1, metric_success = true });
                return replay ? null : response["effect"] as JObject;
            }
            finally { gate.Release(); }
        }
    }
    public sealed class SpendSink
    {
        public string Reason { get; }
        public string ReferenceId { get; }
        public string Booster { get; }
        public SpendSink(string reason, string referenceId, string booster = null)
        {
            if (reason != "continue" && reason != "refill_lives" && reason != "booster") throw new ArgumentException("Invalid sink");
            if (!Guid.TryParse(referenceId, out _)) throw new ArgumentException("Reference UUID required");
            if (reason == "booster" && booster != "hint" && booster != "undo" && booster != "unclamp") throw new ArgumentException("Invalid booster");
            Reason = reason; ReferenceId = referenceId; Booster = reason == "booster" ? booster : null;
        }
    }
    public sealed class StoreService
    {
        public static readonly string[] ProductIds = { "com.orcub.puzzle.starter_pack", "com.orcub.puzzle.gold_small", "com.orcub.puzzle.gold_medium", "com.orcub.puzzle.gold_large", "com.orcub.puzzle.gold_huge", "com.orcub.puzzle.remove_ads" };
        readonly IStoreKit kit;
        readonly IStoreCache cache;
        readonly HashSet<string> completed = new HashSet<string>();
        readonly IStoreBackend backend;
        readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        public Wallet Wallet { get; }
        public IReadOnlyList<StoreProduct> Products { get; private set; } = Array.Empty<StoreProduct>();
        public JArray Offers { get; private set; } = new JArray();
        public JObject Economy { get; private set; }
        public event Action<string> Purchased, Failed, Pending, Cancelled;
        public event Action Changed;
        public StoreService(IStoreKit kit, IStoreBackend backend, IStoreCache cache)
        {
            this.kit = kit; this.backend = backend; this.cache = cache; Wallet = new Wallet(backend, cache);
            kit.TransactionUpdated += OnTransaction;
        }
        async void OnTransaction(StoreTransaction transaction) { await Process(transaction); }
        public bool CanPresentOffer(JObject offer, DateTimeOffset now)
        {
            if (DateTimeOffset.TryParse((string)offer["expires_at"], out var expiry) && now >= expiry) return false;
            if (DateTimeOffset.TryParse((string)offer["repeat_after"], out var repeat) && now < repeat) return false;
            var key = "offer." + backend.PlayerId + "." + (string)offer["id"] + "." + expiry.ToUnixTimeSeconds();
            return !DateTimeOffset.TryParse(cache.Read(key), out var next) || now >= next;
        }
        public async Task AcknowledgeOfferPresented(JObject offer)
        {
            var response = await backend.Request("/v1/offers/shown", "POST", new { player_id = backend.PlayerId });
            offer["expires_at"] = response["expires_at"]; offer["repeat_after"] = response["repeat_after"];
            CacheOfferPresented(offer);
        }
        public async void MarkOfferPresented(JObject offer)
        {
            CacheOfferPresented(offer);
            try { await AcknowledgeOfferPresented(offer); }
            catch (Exception) { Failed?.Invoke("offer_acknowledgement"); }
        }
        void CacheOfferPresented(JObject offer)
        {
            if (!DateTimeOffset.TryParse((string)offer["expires_at"], out var expiry) ||
                !DateTimeOffset.TryParse((string)offer["repeat_after"], out var next)) return;
            cache.Write("offer." + backend.PlayerId + "." + (string)offer["id"] + "." + expiry.ToUnixTimeSeconds(), next.ToString("o"));
        }
        public async Task Initialize()
        {
            Wallet.LoadCache();
            await backend.PrepareAccount(); Wallet.LoadCache();
            await RetryPending(); await Refresh();
            Products = await kit.LoadProducts(ProductIds); Changed?.Invoke();
        }
        public async Task<bool> ClaimBoosterUnlock(string kind)
        {
            if(kind!="hint" && kind!="undo" && kind!="unclamp") throw new ArgumentException(nameof(kind));
            var player=backend.PlayerId;
            if(string.IsNullOrEmpty(player)) return false;
            var slot="unlock."+player+"."+kind;
            var key=cache.Read(slot);
            if(string.IsNullOrEmpty(key)) { key=Guid.NewGuid().ToString(); cache.Write(slot,key); }
            try {
                await Wallet.Submit("/v1/wallet/grant",new {player_id=player,source="booster_unlock",kind,ref_id=key,idempotency_key=key});
                cache.Write("unlock-claimed."+player+"."+kind,"1"); cache.Write(slot,""); return true;
            } catch(Exception) { return false; }
        }
        public Func<string> OldestPending { get; set; }
        public async Task Refresh()
        {
            await Wallet.Refresh();
            var config = await backend.Request("/v1/config?player_id=" + backend.PlayerId, "GET");
            Economy = config["economy"] as JObject; Offers = config["offers"] as JArray ?? new JArray(); Changed?.Invoke();
        }
        public async Task Purchase(string id)
        {
            try
            {
                if (!Products.Any(p => p.Id == id)) throw new InvalidOperationException("product_unavailable");
                await backend.PrepareAccount();
                var transaction = await kit.Purchase(id, backend.PlayerId);
                if (transaction == null) { Pending?.Invoke(id); return; }
                await Process(transaction);
            }
            catch (OperationCanceledException) { Cancelled?.Invoke(id); }
            catch (Exception) { backend.Track("iap_failed", new { metric_count = 1, metric_success = false }); Failed?.Invoke(id); }
        }
        public async Task<int> ClaimLevelReward(string eventId)
        {
            try
            {
                var response = await Wallet.Submit("/v1/wallet/grant", new { player_id = backend.PlayerId,
                    reason = "level_reward", ref_id = eventId, idempotency_key = eventId });
                int granted = (int?)response["granted"] ?? 0;
                // a capped level comes back as 200 deferred:true; "granted" is everything paid in this call (backlog too)
                return (bool?)response["deferred"] == true ? Orcub.Game.RewardClaims.DeferredWith(granted) : granted;
            }
            catch (StoreHttpException e) when (e.Status == 409)
            {
                string reason = e.Message;
                try { reason = (string)JObject.Parse(reason)["error"] ?? reason; }
                catch (Newtonsoft.Json.JsonException) { }
                if (reason == "reward_pending") throw new Orcub.Game.RewardPendingException();   // evidence not on the server
                throw;
            }
            catch (StoreHttpException e) when (e.Status == 410)
            {
                backend.Track("error", new { code = "level_reward_refused", context = "level_reward" });
                return 0;
            }
            catch (StoreHttpException e) when (e.Status == 400 || e.Status == 422)
            {
                backend.Track("error", new { code = "level_reward_refused", context = "level_reward" });
                return 0;
            }
        }
        public async Task RetryPending()
        {
            foreach (var transaction in await kit.Unfinished()) await Process(transaction);
        }
        async Task Process(StoreTransaction transaction)
        {
            await gate.WaitAsync();
            try
            {
                if (completed.Contains(transaction.Id)) return;
                var response = await Wallet.Submit("/v1/iap/apple", new { player_id = backend.PlayerId, signed_transaction = transaction.Jws });
                if ((string)response["transaction_id"] != transaction.Id) throw new InvalidOperationException("transaction_mismatch");
                await Wallet.Refresh();
                await kit.Finish(transaction.Id); completed.Add(transaction.Id);
                backend.Track("iap_purchase", new { metric_count = 1, metric_success = true }); Purchased?.Invoke(transaction.ProductId);
            }
            catch (StoreHttpException error) when (error.Status == 400 || error.Status == 409)
            { backend.Track("iap_failed", new { metric_count = 1, metric_success = false }); Failed?.Invoke(transaction.ProductId); }
            catch (Exception) { backend.Track("iap_failed", new { metric_count = 1, metric_success = false }); Pending?.Invoke(transaction.ProductId); }
            finally { gate.Release(); }
            try { await Refresh(); } catch (Exception) { /* Retain last confirmed wallet; refresh next foreground. */ }
        }
        /// <summary>Transactions the last Restore found; -1 before one completed.</summary>
        public int LastRestoredCount { get; private set; } = -1;
        public async Task Restore()
        {
            LastRestoredCount = -1;
            await gate.WaitAsync();
            try
            {
                await backend.PrepareAccount();
                var transactions = await kit.Restore();
                LastRestoredCount = transactions.Count;   // 0: nothing to restore (the shop says so, not "restored")
                string retryKey = "restore.retry." + backend.PlayerId;
                if (transactions.Count > 100) throw new InvalidOperationException("restore_limit");
                var response = await Wallet.Submit("/v1/iap/restore", new { player_id = backend.PlayerId, signed_transactions = transactions.Select(t => t.Jws).ToArray() });
                await Wallet.Refresh();
                if (!(response["results"] is JArray results)) throw new InvalidOperationException("restore_results_missing");
                var failed = new JArray();
                foreach (var item in results.OfType<JObject>())
                {
                    int index = (int)item["index"];
                    if (index < 0 || index >= transactions.Count) throw new InvalidOperationException("restore_index");
                    if ((bool)item["ok"] && (string)item["transaction_id"] == transactions[index].Id) await kit.Finish(transactions[index].Id);
                    else { failed.Add(transactions[index].Id); Pending?.Invoke(transactions[index].ProductId); }
                }
                cache.Write(retryKey, failed.Count == 0 ? "" : failed.ToString());
            }
            catch (Exception) { Failed?.Invoke("restore"); }
            finally { gate.Release(); }
            try { await Refresh(); } catch (Exception) { }
        }
    }
}
