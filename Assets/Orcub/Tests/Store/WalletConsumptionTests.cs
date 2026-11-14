using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Orcub.Store;
namespace Orcub.Tests
{
    public class WalletConsumptionTests
    {
        sealed class Fake : IStoreBackend, IStoreCache, IStoreKit
        {
            public string PlayerId => "player";
            public bool Offline, LoseAcknowledgement;
            public int Debits;
            readonly Dictionary<string,string> cache = new Dictionary<string,string>();
            readonly HashSet<string> consumed = new HashSet<string>();
            public event Action<StoreTransaction> TransactionUpdated { add {} remove {} }
            public Task PrepareAccount() => Task.CompletedTask;
            public Task<JObject> Request(string path, string method, object body = null)
            {
                if (Offline) throw new Exception("offline");
                if (path == "/v1/wallet/consume")
                {
                    var r = JObject.FromObject(body);
                    if (consumed.Add((string)r["idempotency_key"])) Debits++;
                    if (LoseAcknowledgement) { LoseAcknowledgement = false; throw new Exception("lost response"); }
                    return Task.FromResult(new JObject { ["wallet"] = State });
                }
                if (path.StartsWith("/v1/wallet?")) return Task.FromResult(State);
                return Task.FromResult(new JObject());
            }
            public JObject State => new JObject { ["player_id"] = PlayerId, ["gold"] = 10, ["remove_ads"] = false, ["inventory"] = new JObject { ["hint"] = 3 - Debits }, ["lives"] = 2, ["lives_updated_at"] = DateTimeOffset.UtcNow.AddSeconds(-1900).ToString("o") };
            public void Track(string kind, object data) {}
            public string Read(string key) => cache.TryGetValue(key, out var s) ? s : "";
            public void Write(string key, string value) => cache[key] = value;
            public Task<IReadOnlyList<StoreProduct>> LoadProducts(string[] ids) => Task.FromResult<IReadOnlyList<StoreProduct>>(Array.Empty<StoreProduct>());
            public Task<StoreTransaction> Purchase(string id, string token) => Task.FromResult<StoreTransaction>(null);
            public Task<IReadOnlyList<StoreTransaction>> Unfinished() => Task.FromResult<IReadOnlyList<StoreTransaction>>(Array.Empty<StoreTransaction>());
            public Task<IReadOnlyList<StoreTransaction>> Restore() => Unfinished();
            public Task Finish(string id) => Task.CompletedTask;
        }
        [Test] public async Task OfflineReservationSurvivesRestartAndOnlyAcknowledgementAuthorisesUse()
        {
            var f = new Fake(); var store = new StoreService(f,f,f); await store.Wallet.Refresh();
            var c = new WalletConsumption(f,f,store.Wallet); var attempt = c.AttemptReference;
            f.Offline = true; Assert.False(c.TrySpend("booster_hint")); Assert.AreEqual(2,c.Count("booster_hint",DateTimeOffset.UtcNow));
            var restarted = new WalletConsumption(f,f,store.Wallet) { AttemptReference = attempt };
            Assert.False(restarted.TrySpend("booster_hint")); Assert.AreEqual(0,f.Debits);
            f.Offline = false; await restarted.Reconcile(); await restarted.Reconcile();
            Assert.AreEqual(1,f.Debits); Assert.True(restarted.TrySpend("booster_hint")); Assert.AreEqual(2,restarted.Count("booster_hint",DateTimeOffset.UtcNow));
        }
        [Test] public async Task LostConsumeAcknowledgementReplaysSameKeyWithoutDoubleDebit()
        {
            var f = new Fake(); var store = new StoreService(f,f,f); await store.Wallet.Refresh();
            var c = new WalletConsumption(f,f,store.Wallet); f.LoseAcknowledgement = true;
            Assert.False(c.TrySpend("booster_hint")); Assert.AreEqual(1,f.Debits);
            await c.Reconcile(); Assert.AreEqual(1,f.Debits);
            Assert.True(c.TrySpend("booster_hint")); Assert.AreEqual(2,c.Count("booster_hint",DateTimeOffset.UtcNow));
        }
        [Test] public async Task LivesDisplayMirrorsThirtyMinuteRegeneration()
        {
            var f = new Fake(); var store = new StoreService(f,f,f); await store.Wallet.Refresh();
            var c = new WalletConsumption(f,f,store.Wallet);
            Assert.AreEqual(3,c.Count("life",DateTimeOffset.UtcNow)); Assert.That(c.SecondsToNext(DateTimeOffset.UtcNow), Is.InRange(1698,1701));
            Assert.AreEqual(5,c.Count("life",DateTimeOffset.UtcNow.AddHours(4)));
        }
    }
}
