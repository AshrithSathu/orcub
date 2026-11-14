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
            public string PlayerId { get; set; } = "player";
            public bool Offline, LoseAcknowledgement;
            public long Reject;
            public int Debits, Hints = 3, LifeBalance = 2;
            public JArray ConfirmedAds = new JArray();
            public DateTimeOffset Stamp = DateTimeOffset.UtcNow.AddSeconds(-1900);
            public readonly List<string> Keys = new List<string>();
            readonly Dictionary<string,string> cache = new Dictionary<string,string>();
            readonly HashSet<string> consumed = new HashSet<string>();
            public event Action<StoreTransaction> TransactionUpdated { add {} remove {} }
            public Task PrepareAccount() => Task.CompletedTask;
            public Task<JObject> Request(string path, string method, object body = null)
            {
                if (path == "/v1/wallet/consume")
                {
                    var r = JObject.FromObject(body);
                    Keys.Add((string)r["idempotency_key"]);
                    if (Offline) throw new Exception("offline");
                    if (Reject != 0) throw new StoreHttpException(Reject, Reject==409 ? "insufficient inventory" : null);
                    if (consumed.Add((string)r["idempotency_key"]))
                    {
                        Debits++;
                        if ((string)r["kind"] == "life") LifeBalance--; else Hints--;
                    }
                    if (LoseAcknowledgement) { LoseAcknowledgement = false; throw new Exception("lost response"); }
                    return Task.FromResult(new JObject { ["wallet"] = State });
                }
                if (Offline) throw new Exception("offline");
                if (path.StartsWith("/v1/wallet?")) return Task.FromResult(State);
                return Task.FromResult(new JObject());
            }
            public JObject State => new JObject { ["player_id"] = PlayerId, ["gold"] = 10, ["remove_ads"] = false,
                ["rewarded_nonces"]=ConfirmedAds, ["inventory"] = new JObject { ["hint"] = Hints }, ["lives"] = LifeBalance, ["lives_updated_at"] = Stamp.ToString("o") };
            public void Track(string kind, object data) {}
            public string Read(string key) => cache.TryGetValue(key, out var s) ? s : "";
            public void Write(string key, string value) => cache[key] = value;
            public Task<IReadOnlyList<StoreProduct>> LoadProducts(string[] ids) => Task.FromResult<IReadOnlyList<StoreProduct>>(Array.Empty<StoreProduct>());
            public Task<StoreTransaction> Purchase(string id, string token) => Task.FromResult<StoreTransaction>(null);
            public Task<IReadOnlyList<StoreTransaction>> Unfinished() => Task.FromResult<IReadOnlyList<StoreTransaction>>(Array.Empty<StoreTransaction>());
            public Task<IReadOnlyList<StoreTransaction>> Restore() => Unfinished();
            public Task Finish(string id) => Task.CompletedTask;
        }
        [Test] public async Task FirstTapSpendsImmediatelyAndAckNeverDoubleSubtractsEvenInChangedCallback()
        {
            var f = new Fake(); var store = new StoreService(f,f,f); await store.Wallet.Refresh();
            var c = new WalletConsumption(f,f,store.Wallet);
            Assert.True(c.TrySpend("booster_hint")); Assert.AreEqual(2,c.Count("booster_hint",DateTimeOffset.UtcNow));
            int observed = -1;
            store.Wallet.Changed += () => observed = c.Count("booster_hint",DateTimeOffset.UtcNow);
            await c.Reconcile();
            Assert.AreEqual(2, observed); Assert.AreEqual(2,c.Count("booster_hint",DateTimeOffset.UtcNow));
            Assert.AreEqual(0,JArray.Parse(f.Read("consume.player")).Count);
        }
        [Test] public async Task OfflineSpendSurvivesRestartAndReconcilesWithSameKey()
        {
            var f = new Fake(); var store = new StoreService(f,f,f); await store.Wallet.Refresh();
            var c = new WalletConsumption(f,f,store.Wallet);
            f.Offline = true; Assert.True(c.TrySpend("booster_hint")); await c.Reconcile();
            var item = JArray.Parse(f.Read("consume.player"))[0];
            Assert.AreEqual(c.AttemptReference,(string)item["attempt_ref"]); Assert.AreEqual(1,(int)item["count"]);
            var restarted = new WalletConsumption(f,f,new StoreService(f,f,f).Wallet);
            Assert.AreEqual(2,restarted.Count("booster_hint",DateTimeOffset.UtcNow));
            f.Offline = false; await restarted.Reconcile(); await restarted.Reconcile();
            Assert.AreEqual(1,f.Debits); Assert.AreEqual(2,restarted.Count("booster_hint",DateTimeOffset.UtcNow));
            Assert.That(f.Keys, Is.All.EqualTo((string)item["idempotency_key"]));
        }
        [Test] public async Task LostConsumeAcknowledgementReplaysSameKeyWithoutDoubleDebit()
        {
            var f = new Fake(); var store = new StoreService(f,f,f); await store.Wallet.Refresh();
            var c = new WalletConsumption(f,f,store.Wallet); f.LoseAcknowledgement = true;
            Assert.True(c.TrySpend("booster_hint")); await c.Reconcile(); Assert.AreEqual(1,f.Debits);
            await c.Reconcile(); Assert.AreEqual(1,f.Debits);
            Assert.AreEqual(f.Keys[0],f.Keys[1]); Assert.AreEqual(2,c.Count("booster_hint",DateTimeOffset.UtcNow));
        }
        [Test] public async Task InsufficientBalanceDropsQueuedUseWithoutClawbackOrNegativeCount()
        {
            var f = new Fake(); var store = new StoreService(f,f,f); await store.Wallet.Refresh();
            var c = new WalletConsumption(f,f,store.Wallet); f.Hints = 0; f.Reject = 409;
            Assert.True(c.TrySpend("booster_hint")); await c.Reconcile();
            Assert.AreEqual(0,JArray.Parse(f.Read("consume.player")).Count);
            Assert.AreEqual(0,c.Count("booster_hint",DateTimeOffset.UtcNow)); Assert.False(c.TrySpend("booster_hint"));
        }
        [Test] public async Task ServerFailureAndNonBalanceClientErrorsKeepQueue()
        {
            var f = new Fake(); var store = new StoreService(f,f,f); await store.Wallet.Refresh();
            var c = new WalletConsumption(f,f,store.Wallet); f.Reject = 503;
            Assert.True(c.TrySpend("booster_hint")); await c.Reconcile();
            Assert.AreEqual(1,JArray.Parse(f.Read("consume.player")).Count);
            f.Reject = 400; await c.Reconcile();
            Assert.AreEqual(1,JArray.Parse(f.Read("consume.player")).Count);
        }
        [Test] public async Task FreshOfflinePlayerHasBackendDefaultsAndLifeSpendsRegenerateOffline()
        {
            var f = new Fake { Offline = true }; var store = new StoreService(f,f,f);
            var c = new WalletConsumption(f,f,store.Wallet); var now = DateTimeOffset.UtcNow;
            Assert.AreEqual(5,c.Count("life",now));
            foreach (var kind in new[] { "hint", "undo", "unclamp" }) Assert.AreEqual(0,c.Count("booster_" + kind,now));
            Assert.True(await c.Spend("life")); Assert.AreEqual(4,c.Count("life",DateTimeOffset.UtcNow));
            Assert.That(c.SecondsToNext(DateTimeOffset.UtcNow),Is.InRange(1798,1800));
            Assert.That(c.SecondsToNext(now.AddMinutes(1)),Is.InRange(1738,1741));
            Assert.AreEqual(5,c.Count("life",now.AddMinutes(31)));
            for (int i = 0; i < 4; i++) Assert.True(c.TrySpend("life"));
            Assert.AreEqual(0,c.Count("life",DateTimeOffset.UtcNow)); Assert.False(c.TrySpend("life"));
            Assert.AreEqual(1,c.Count("life",now.AddMinutes(31)));
            await c.Reconcile();
        }
        [Test] public async Task RepeatedOfflineSpendsInSameAttemptStopAtZero()
        {
            var f = new Fake(); var store = new StoreService(f,f,f); await store.Wallet.Refresh(); f.Offline = true;
            var c = new WalletConsumption(f,f,store.Wallet);
            for (int i=0;i<3;i++) Assert.True(c.TrySpend("booster_hint"));
            Assert.False(c.TrySpend("booster_hint")); Assert.AreEqual(0,c.Count("booster_hint",DateTimeOffset.UtcNow));
            await c.Reconcile(); Assert.AreEqual(3,JArray.Parse(f.Read("consume.player")).Count);
        }
        [Test] public async Task OfflineFirstLaunchQueueTransfersOnceToRegisteredPlayerAndAccountsStaySeparate()
        {
            var f = new Fake { PlayerId = null, Offline = true }; var store = new StoreService(f,f,f);
            var c = new WalletConsumption(f,f,store.Wallet);
            Assert.True(c.TrySpend("life")); await c.Reconcile();
            f.PlayerId = "player";
            Assert.AreEqual(4,c.Count("life",DateTimeOffset.UtcNow));
            Assert.AreEqual(1,JArray.Parse(f.Read("consume.player")).Count);
            f.PlayerId = "other"; Assert.AreEqual(5,c.Count("life",DateTimeOffset.UtcNow));
            f.PlayerId = "player"; Assert.AreEqual(4,c.Count("life",DateTimeOffset.UtcNow));
        }
        [Test] public async Task ProvisionalAdSurvivesPreconfirmationRefreshAndReconcilesByNonce()
        {
            var f=new Fake { LifeBalance=0,Stamp=DateTimeOffset.UtcNow }; var store=new StoreService(f,f,f); await store.Wallet.Refresh();
            var c=new WalletConsumption(f,f,store.Wallet); Assert.True(c.ApplyAdRefill("ad-nonce")); Assert.AreEqual(5,c.Count("life",DateTimeOffset.UtcNow));
            f.Stamp=DateTimeOffset.UtcNow; await store.Wallet.Refresh(); Assert.AreEqual(5,c.Count("life",DateTimeOffset.UtcNow));
            f.ConfirmedAds.Add("ad-nonce"); f.LifeBalance=5; await store.Wallet.Refresh();
            Assert.AreEqual(5,c.Count("life",DateTimeOffset.UtcNow)); Assert.False(c.ApplyAdRefill("ad-nonce"));
            f.LifeBalance=3; await store.Wallet.Refresh(); Assert.AreEqual(3,c.Count("life",DateTimeOffset.UtcNow));
        }
        [Test] public void ParseableCorruptWalletAndConsumeCacheCannotBreakCounts()
        {
            var f=new Fake(); f.Write("wallet.player","{player_id:'other',gold:1,remove_ads:false}");
            f.Write("consume.player","[{kind:'life',count:'bad'}]");
            f.Write("provisional.player","{lives:'bad',inventory:[]}");
            var c=new WalletConsumption(f,f,new StoreService(f,f,f).Wallet);
            Assert.DoesNotThrow(()=>c.Count("life",DateTimeOffset.UtcNow));
            Assert.DoesNotThrow(()=>c.Count("booster_hint",DateTimeOffset.UtcNow));
        }
        [Test] public void ProvisionalRefillHonoursDailyCapAcrossReloads()
        {
            var f=new Fake(); var c=new WalletConsumption(f,f,new StoreService(f,f,f).Wallet);
            for(int i=0;i<20;i++) Assert.True(c.ApplyAdRefill(Guid.NewGuid().ToString()));
            c=new WalletConsumption(f,f,new StoreService(f,f,f).Wallet);
            Assert.False(c.ApplyAdRefill(Guid.NewGuid().ToString()));
        }
        [TestCase(401)] [TestCase(429)] public async Task TransientStatusRetainsQueue(int status)
        {
            var f=new Fake(); var store=new StoreService(f,f,f); await store.Wallet.Refresh(); f.Reject=status;
            var c=new WalletConsumption(f,f,store.Wallet); Assert.True(c.TrySpend("booster_hint")); await c.Reconcile();
            Assert.AreEqual(1,JArray.Parse(f.Read("consume.player")).Count);
        }
        [Test] public async Task LinkMigratesPendingAndCorruptCacheRecovers()
        {
            var f=new Fake(); var store=new StoreService(f,f,f); await store.Wallet.Refresh(); f.Offline=true;
            var c=new WalletConsumption(f,f,store.Wallet); c.TrySpend("booster_hint"); await c.Reconcile();
            c.Migrate("player","canonical"); f.PlayerId="canonical";
            Assert.AreEqual(1,JArray.Parse(f.Read("consume.canonical")).Count);
            f.Write("consume.broken","garbage"); f.PlayerId="broken";
            Assert.DoesNotThrow(()=>c.Count("life",DateTimeOffset.UtcNow));
            Assert.AreEqual("garbage",f.Read("quarantine.consume.broken"));
        }
        [Test] public async Task UnlimitedDoesNotQueueAndAdNonceAppliesOnce()
        {
            var f=new Fake(); var store=new StoreService(f,f,f); await store.Wallet.Refresh();
            var snapshot=store.Wallet.Snapshot; snapshot["unlimited_until"]=DateTimeOffset.UtcNow.AddMinutes(30).ToString("o");
            var c=new WalletConsumption(f,f,store.Wallet); c.Count("life",DateTimeOffset.UtcNow);
            // LoadCache re-adopts persisted snapshot; seed that cache too.
            f.Write("wallet.player",snapshot.ToString()); c=new WalletConsumption(f,f,new StoreService(f,f,f).Wallet);
            Assert.True(c.TrySpend("life")); Assert.AreEqual(0,JArray.Parse(f.Read("consume.player")).Count);
            Assert.True(c.ApplyAdRefill("nonce")); Assert.False(c.ApplyAdRefill("nonce"));
            Assert.AreEqual(5,c.Count("life",DateTimeOffset.UtcNow));
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
