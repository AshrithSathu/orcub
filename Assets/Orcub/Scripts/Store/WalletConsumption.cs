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
        DateTime retryAt;
        string owner;
        public bool HasPending { get { Load(); return pending.Count>0; } }
        public string AttemptReference { get; set; } = Guid.NewGuid().ToString();
        public WalletConsumption(IStoreBackend backend, IStoreCache cache, Wallet wallet)
        { this.backend = backend; this.cache = cache; this.wallet = wallet; wallet.OldestPending = () => { Load(); return pending.OfType<JObject>().Select(x=>(string)x["applied_since"] ?? "1970-01-01T00:00:00Z").OrderBy(x=>x).FirstOrDefault(); }; }
        void Load()
        {
            var account = backend.PlayerId ?? "unregistered";
            if (owner == account && pending != null) return;
            owner = account;
            var json = cache.Read("consume." + owner);
            pending = string.IsNullOrEmpty(json) ? new JArray() : SafeArray("consume." + owner, json);
            // Offline first launch has no server identity yet. Transfer once on registration.
            if (owner != "unregistered")
            {
                var unregistered = cache.Read("consume.unregistered");
                if (!string.IsNullOrEmpty(unregistered))
                {
                    foreach (var item in SafeArray("consume.unregistered", unregistered).OfType<JObject>())
                        if (!pending.OfType<JObject>().Any(x => (string)x["idempotency_key"] == (string)item["idempotency_key"])) pending.Add(item.DeepClone());
                    Save(); cache.Write("consume.unregistered", "");
                }
            }
            Save();
            json = cache.Read("provisional." + owner);
            provisional = string.IsNullOrEmpty(json) ? new JObject {
                ["lives"] = 5, ["lives_updated_at"] = DateTimeOffset.UtcNow.ToString("o"), ["inventory"] = new JObject()
            } : SafeObject("provisional." + owner, json);
            cache.Write("provisional." + owner, provisional.ToString());
            wallet.LoadCache();
        }
        JArray SafeArray(string key, string json) { try {
            var array=string.IsNullOrEmpty(json) ? new JArray() : JArray.Load(new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(json)) { DateParseHandling=Newtonsoft.Json.DateParseHandling.None });
            if(key.StartsWith("consume.") && array.Any(x=>!(x is JObject item) || item["count"]?.Type!=JTokenType.Integer || (int)item["count"]<1 || (int)item["count"]>100 || !Guid.TryParse((string)item["idempotency_key"],out _) || !Guid.TryParse((string)item["attempt_ref"],out _) || !new[]{"life","booster_hint","booster_undo","booster_unclamp"}.Contains((string)item["kind"]) || !TryTime(item["created_at"],out _))) throw new FormatException("Invalid consume queue");
            if(key.StartsWith("adgrants.") && array.Any(x=>!(x is JObject grant) || grant["nonce"]?.Type!=JTokenType.String || !TryTime(grant["at"],out _))) throw new FormatException("Invalid ad grant cache");
            return array;
        } catch (Exception) { cache.Write("quarantine."+key,json); cache.Write(key,""); return new JArray(); } }
        JObject SafeObject(string key, string json) { try { var value=JObject.Parse(json); if(value["lives"]?.Type!=JTokenType.Integer || !(value["inventory"] is JObject)) throw new FormatException("Invalid provisional wallet"); return value; } catch (Exception) { cache.Write("quarantine."+key,json); cache.Write(key,""); return new JObject { ["lives"]=5, ["lives_updated_at"]=DateTimeOffset.UtcNow.ToString("o"), ["inventory"]=new JObject() }; } }
        public void Migrate(string oldId, string newId)
        {
            if (string.IsNullOrEmpty(oldId) || oldId==newId) return;
            var old=SafeArray("consume."+oldId,cache.Read("consume."+oldId)??"[]");
            var target=SafeArray("consume."+newId,cache.Read("consume."+newId)??"[]");
            foreach (var item in old.OfType<JObject>()) if (!target.OfType<JObject>().Any(x=>(string)x["idempotency_key"]==(string)item["idempotency_key"])) target.Add(item.DeepClone());
            cache.Write("consume."+newId,target.ToString()); cache.Write("consume."+oldId,"");
            var grants=SafeArray("adgrants."+newId,cache.Read("adgrants."+newId));
            foreach(var grant in SafeArray("adgrants."+oldId,cache.Read("adgrants."+oldId)).OfType<JObject>()) if(!grants.OfType<JObject>().Any(x=>(string)x["nonce"]==(string)grant["nonce"])) grants.Add(grant.DeepClone());
            cache.Write("adgrants."+newId,grants.ToString()); cache.Write("adgrants."+oldId,""); owner=null;
        }
        public bool ApplyAdRefill(string nonce)
        {
            lifeFrame=-1;
            Load(); var grants=SafeArray("adgrants."+owner,cache.Read("adgrants."+owner)??"[]");
            if (grants.OfType<JObject>().Any(x=>(string)x["nonce"]==nonce)) return false;
            if (grants.OfType<JObject>().Count(x=>TryTime(x["at"],out var time) && time.UtcDateTime.Date==DateTime.UtcNow.Date)>=20) return false;
            grants.Add(new JObject { ["nonce"]=nonce,["at"]=DateTimeOffset.UtcNow.ToString("o") }); cache.Write("adgrants."+owner,grants.ToString()); return true;
        }
        void Save() => cache.Write("consume." + owner, pending.ToString());
        JObject Snapshot => wallet.Snapshot ?? provisional;
        static bool TryTime(JToken token, out DateTimeOffset time)
        {
            if (token?.Type == JTokenType.Date) { time = token.ToObject<DateTimeOffset>(); return true; }
            time=default; return token?.Type==JTokenType.String && DateTimeOffset.TryParse((string)token, out time);
        }
        static void Regenerate(ref int count, ref DateTimeOffset stamp, DateTimeOffset now)
        {
            var ticks = Math.Max(0, Math.Floor((now - stamp).TotalSeconds / 1800));
            count = (int)Math.Min(5, count + ticks);
            stamp = count >= 5 ? now : stamp.AddSeconds(ticks * 1800);
        }
        int lifeFrame=-1, lifePending=-1, lifeCached;
        long lifeSecond;
        JObject lifeSnapshot;
        DateTimeOffset lifeStamp;
        int LifeCount(DateTimeOffset now, out DateTimeOffset stamp)
        {
            if(lifeFrame==Time.frameCount && lifeSecond==now.ToUnixTimeSeconds() && lifeSnapshot==Snapshot && lifePending==pending.Count) { stamp=lifeStamp; return lifeCached; }
            lifeCached=ComputeLifeCount(now,out lifeStamp); lifeFrame=Time.frameCount; lifeSecond=now.ToUnixTimeSeconds(); lifeSnapshot=Snapshot; lifePending=pending.Count; stamp=lifeStamp; return lifeCached;
        }
        int ComputeLifeCount(DateTimeOffset now, out DateTimeOffset stamp)
        {
            var s = Snapshot;
            var applied=s["applied_keys"] as JArray;
            if (applied!=null) { var keys=new System.Collections.Generic.HashSet<string>(applied.Values<string>().Select(k=>k.Split(':').Last())); var removed=pending.OfType<JObject>().Where(x=>keys.Contains((string)x["idempotency_key"])).ToArray(); foreach(var item in removed) pending.Remove(item); if(removed.Length>0) Save(); }
            stamp = TryTime(s["lives_updated_at"], out var parsed) ? parsed : now;
            if (TryTime(s["unlimited_until"], out var until) && now < until) return 5;
            int count = (int?)s["lives"] ?? 5;
            var confirmed=s["rewarded_nonces"] as JArray ?? new JArray();
            var grants=SafeArray("adgrants."+owner,cache.Read("adgrants."+owner)??"[]");
            var latest=grants.OfType<JObject>().LastOrDefault(x=>TryTime(x["at"],out var at) && (at>now.AddMinutes(-10) || wallet.LastConfirmedRefresh<at.AddMinutes(10)));
            if (latest!=null && !confirmed.Values<string>().Contains((string)latest["nonce"]) && TryTime(latest["at"],out var refillAt)) { count=5; stamp=refillAt; }
            foreach (var item in pending.OfType<JObject>().Where(x => (string)x["kind"] == "life"))
            {
                var at = TryTime(item["created_at"], out var time) ? time : stamp;
                if (latest!=null && TryTime(latest["at"],out var refillBoundary) && at<refillBoundary) continue;
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
            LifeCount(now, out _);
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
            if (kind == "life" && TryTime(Snapshot["unlimited_until"],out var until) && DateTimeOffset.UtcNow<until) return true;
            if (Count(kind, DateTimeOffset.UtcNow) <= 0) return false;
            var grant=SafeArray("adgrants."+owner,cache.Read("adgrants."+owner)).OfType<JObject>().LastOrDefault(x=>TryTime(x["at"],out var at) && at>DateTimeOffset.UtcNow.AddMinutes(-10) && !(Snapshot["rewarded_nonces"] as JArray ?? new JArray()).Values<string>().Contains((string)x["nonce"]));
            pending.Add(new JObject { ["rewarded_nonce"]=kind=="life" && !((string)grant?["nonce"]??"").StartsWith("editor:") ? grant?["nonce"]?.DeepClone() : null, ["kind"] = kind, ["count"] = 1, ["attempt_ref"] = AttemptReference,
                ["applied_since"] = wallet.Snapshot?["applied_since"]?.DeepClone(), ["idempotency_key"] = Guid.NewGuid().ToString(), ["created_at"] = DateTimeOffset.UtcNow.ToString("o") });
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
            Load(); bool refreshAfter=false; var account = backend.PlayerId; var queue = pending; var keyOwner = owner;
            if (string.IsNullOrEmpty(account) || DateTime.UtcNow < retryAt) return;
            try
            {
                var grants=SafeArray("adgrants."+owner,cache.Read("adgrants."+owner));
                if(queue.Count==0 && grants.OfType<JObject>().Any(x=>TryTime(x["at"],out var at) && at>DateTimeOffset.UtcNow.AddMinutes(-10) && !(wallet.Snapshot?["rewarded_nonces"] as JArray ?? new JArray()).Values<string>().Contains((string)x["nonce"]))) {
                    await wallet.Refresh();
                    if(grants.OfType<JObject>().Any(x=>TryTime(x["at"],out var at) && at>DateTimeOffset.UtcNow.AddMinutes(-10) && !(wallet.Snapshot?["rewarded_nonces"] as JArray ?? new JArray()).Values<string>().Contains((string)x["nonce"]))) return;
                }
                foreach (var item in queue.OfType<JObject>().ToArray())
                {
                    if (backend.PlayerId != account) return;
                    try
                    {
                        await wallet.Consume(item, account, () => {
                            queue.Remove(item); cache.Write("consume." + keyOwner, queue.ToString());
                        });
                    }
                    catch (StoreHttpException e) when (e.Status==400 || e.Status==422 || e.Status==409)
                    {
                        if (backend.PlayerId != account) return;
                        // Server wins conflicts. Keep the gameplay benefit; never claw it back.
                        queue.Remove(item); cache.Write("consume." + keyOwner, queue.ToString());
                        backend.Track("error",new { code="consume_rejected",context="consume" });
                        refreshAfter=true;

                    }
                }
            }
            catch (StoreHttpException e) { if(e.Status==429) retryAt = DateTime.UtcNow.AddSeconds(Math.Max(15,e.RetryAfter)); }
            catch (Exception) { /* Runtime schedules the next retry after 15 seconds. */ }
            if(refreshAfter && backend.PlayerId==account) { try { await wallet.Refresh(); } catch(Exception) { } }
        }
    }
}
