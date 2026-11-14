using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Orcub.Store;
using Orcub.Core;
using Orcub.Ads;
using UnityEngine;

namespace Orcub.Tests
{
    public class StoreTests
    {
        sealed class Fake : IStoreKit, IStoreBackend, IStoreCache
        {
            public event Action<StoreTransaction> TransactionUpdated;
            public string PlayerId { get; } = Guid.NewGuid().ToString();
            public readonly List<string> Calls = new List<string>();
            readonly Dictionary<string,string> cache = new Dictionary<string,string>();
            public readonly StoreTransaction Transaction = new StoreTransaction { Id = "123", ProductId = StoreService.ProductIds[0], Jws = "signed.jws.transaction" };
            public bool Reject, PendingPurchase, HasUnfinished;
            public int Debits, Finished;
            public long RejectStatus;
            readonly HashSet<string> spends = new HashSet<string>();
            JObject State => new JObject { ["player_id"] = PlayerId, ["gold"] = 500 - Debits * 60, ["remove_ads"] = true, ["inventory"] = new JObject(), ["lives"] = 5 };
            public Task PrepareAccount() { Calls.Add("link"); return Task.CompletedTask; }
            public Task<JObject> Request(string path, string method, object body = null)
            {
                Calls.Add(path);
                if (Reject) throw new InvalidOperationException("offline");
                if (RejectStatus != 0) throw new StoreHttpException(RejectStatus);
                var request = body == null ? new JObject() : JObject.FromObject(body);
                if (path == "/v1/iap/apple") { Assert.AreEqual(Transaction.Jws, (string)request["signed_transaction"]); return Task.FromResult(new JObject { ["wallet"] = State, ["transaction_id"] = Transaction.Id }); }
                if (path == "/v1/wallet/spend")
                {
                    if (spends.Add((string)request["idempotency_key"])) Debits++;
                    return Task.FromResult(new JObject { ["wallet"] = State, ["effect"] = new JObject { ["extra_moves"] = 5 } });
                }
                if (path == "/v1/iap/restore") return Task.FromResult(new JObject { ["wallet"] = State, ["processed"] = 1 });
                if (path.StartsWith("/v1/wallet?")) return Task.FromResult(State);
                return Task.FromResult(new JObject { ["offers"] = new JArray(), ["economy"] = new JObject() });
            }
            public void Track(string kind, object data) { Calls.Add(kind); }
            public string Read(string key) => cache.TryGetValue(key, out var value) ? value : "";
            public void Write(string key, string value) { cache[key] = value; }
            public Task<IReadOnlyList<StoreProduct>> LoadProducts(string[] ids) => Task.FromResult<IReadOnlyList<StoreProduct>>(new[] { new StoreProduct { Id = Transaction.ProductId, LocalizedPrice = "₹ 199" } });
            public Task<StoreTransaction> Purchase(string id, string appAccountToken) { Assert.AreEqual(PlayerId, appAccountToken); return Task.FromResult(PendingPurchase ? null : Transaction); }
            public Task<IReadOnlyList<StoreTransaction>> Unfinished() => Task.FromResult<IReadOnlyList<StoreTransaction>>(HasUnfinished ? new[] { Transaction } : Array.Empty<StoreTransaction>());
            public Task<IReadOnlyList<StoreTransaction>> Restore() => Task.FromResult<IReadOnlyList<StoreTransaction>>(new[] { Transaction });
            public Task Finish(string id) { Calls.Add("finish"); Finished++; return Task.CompletedTask; }
            public void Update() => TransactionUpdated?.Invoke(Transaction);
        }
        [Test]
        public async Task PurchaseAcknowledgesRefreshesThenFinishes()
        {
            var f = new Fake(); var s = new StoreService(f,f,f); await s.Initialize(); f.Calls.Clear();
            await s.Purchase(f.Transaction.ProductId);
            Assert.Less(f.Calls.IndexOf("/v1/iap/apple"), f.Calls.IndexOf("finish"));
            Assert.Less(f.Calls.FindIndex(x => x.StartsWith("/v1/wallet?")), f.Calls.IndexOf("finish"));
            Assert.AreEqual("₹ 199", s.Products[0].LocalizedPrice); Assert.AreEqual(500, s.Wallet.Balance);
        }
        [Test]
        public async Task OfflinePurchaseRemainsUnfinishedThenLaunchRetries()
        {
            var f = new Fake(); var s = new StoreService(f,f,f); await s.Initialize(); int before = f.Finished;
            f.Reject = true; int pending = 0; s.Pending += _ => pending++;
            await s.Purchase(f.Transaction.ProductId); Assert.AreEqual(before, f.Finished); Assert.AreEqual(1, pending);
            f.Reject = false; f.HasUnfinished = true; var nextLaunch = new StoreService(f,f,f); await nextLaunch.Initialize(); Assert.Greater(f.Finished, before);
        }
        [Test]
        public async Task PendingStorePurchaseDoesNotGrantOrFinish()
        {
            var f = new Fake(); var s = new StoreService(f,f,f); await s.Initialize(); int before = f.Finished;
            f.PendingPurchase = true; await s.Purchase(f.Transaction.ProductId); Assert.AreEqual(before, f.Finished);
        }
        [Test]
        public async Task SpendSameKeyDebitsAndReturnsEffectOnlyOnceAcrossLaunches()
        {
            var f = new Fake(); var s = new StoreService(f,f,f); var sink = new SpendSink("continue", Guid.NewGuid().ToString()); var key = Guid.NewGuid().ToString();
            Assert.AreEqual(5, (int)(await s.Wallet.Spend(sink,key))["extra_moves"]);
            var next = new StoreService(f,f,f); Assert.IsNull(await next.Wallet.Spend(sink,key)); Assert.AreEqual(1, f.Debits);
        }
        [Test]
        public async Task RestoreUsesServerBeforeFinishing()
        {
            var f = new Fake(); var s = new StoreService(f,f,f); await s.Restore();
            Assert.Less(f.Calls.IndexOf("/v1/iap/restore"), f.Calls.IndexOf("finish")); Assert.IsTrue(s.Wallet.RemoveAds);
        }
        [Test]
        public async Task ServerRejectNeverGrantsOrFinishes()
        {
            var f = new Fake(); var s = new StoreService(f,f,f); await s.Initialize();
            int failed = 0; s.Failed += _ => failed++; f.RejectStatus = 400;
            await s.Purchase(f.Transaction.ProductId);
            Assert.AreEqual(0, f.Finished); Assert.AreEqual(1,failed); Assert.AreEqual(500,s.Wallet.Balance);
            await s.Restore(); Assert.AreEqual(0,f.Finished);
        }
        [Test]
        public void OfferPresentationHonoursExpiryAndPersistentCooldown()
        {
            var f = new Fake(); var s = new StoreService(f,f,f); var now = DateTimeOffset.UtcNow;
            var offer = new JObject { ["id"] = "starter_pack", ["expires_at"] = now.AddHours(48).ToString("o"), ["repeat_after"] = now.AddHours(24).ToString("o") };
            Assert.IsTrue(s.CanPresentOffer(offer,now)); s.MarkOfferPresented(offer);
            Assert.IsFalse(new StoreService(f,f,f).CanPresentOffer(offer,now));
            Assert.IsTrue(s.CanPresentOffer(offer,now.AddHours(25))); Assert.IsFalse(s.CanPresentOffer(offer,now.AddHours(49)));
        }
        [TestCase(1,0)]
        [TestCase(0,1)]
        public void AssistedWinPreservesRatingStreakAndModelEstimate(int continues, int boosters)
        {
            var prefix = "orcub.test.store." + Guid.NewGuid() + ".";
            var skill = new PlayerSkill(prefix); var streak = new FailureStreak(prefix);
            var log = new AttemptLog(persist: false);
            var adaptive = new Adaptive(skill,streak,log);
            streak.Record(5,AttemptOutcome.Lose); streak.Record(5,AttemptOutcome.Lose);
            var estimate = SkillModel.Estimate(skill,log,new Tuning());
            adaptive.BeginAttempt(5,LevelGenerator.Tutorial(1).ToDef());
            for (int i = 0; i < 5; i++) adaptive.RecordMove(true,1);
            adaptive.EndAttempt(new AttemptResult(AttemptOutcome.Win,3,3,boosters,continues));
            Assert.AreEqual(0,skill.Games); Assert.AreEqual(0,skill.Recent.Count);
            Assert.AreEqual(1000,skill.Rating); Assert.AreEqual(2,streak.Count(5));
            Assert.AreEqual(estimate.Rating,SkillModel.Estimate(skill,log,new Tuning()).Rating);
            PlayerPrefs.DeleteKey(prefix+"state"); PlayerPrefs.DeleteKey(prefix+"5");
        }
        [Test]
        public void AdCapsPersistAcrossPolicyInstancesAndRemoveAdsKeepsRewarded()
        {
            var prefix = "orcub.test.ads." + Guid.NewGuid(); var config = new AdsConfig { interstitialDailyCap = 1, rewardedDailyCap = 1, levelsBetweenInterstitials = 0 };
            var first = new AdPolicy(config,0,prefix); first.RecordInterstitial(1000); first.RecordRewardedImpression();
            var next = new AdPolicy(config,0,prefix); Assert.IsFalse(next.CanShowRewarded); Assert.IsFalse(next.CanShowInterstitial(20,true,2000));
            var entitlement = new AdPolicy(new AdsConfig { levelsBetweenInterstitials = 0 },0,prefix+"other") { RemoveAds = true };
            Assert.IsFalse(entitlement.CanShowInterstitial(20,true,2000)); Assert.IsTrue(entitlement.CanShowRewarded);
        }
    }
}
