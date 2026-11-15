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
            public string PlayerId { get; set; } = Guid.NewGuid().ToString();
            public readonly List<string> Calls = new List<string>();
            readonly Dictionary<string,string> cache = new Dictionary<string,string>();
            public readonly StoreTransaction Transaction = new StoreTransaction { Id = "123", ProductId = StoreService.ProductIds[0], Jws = "signed.jws.transaction" };
            public bool Reject, PendingPurchase, HasUnfinished, MixedRestore, CancelPurchase;
            public string RejectReason;
            public bool DeferredReward;
            public int GrantedReward;
            public readonly List<string[]> RestoreBatches = new List<string[]>();
            public JObject LastGrant;
            public int Debits, Finished;
            public long RejectStatus;
            readonly HashSet<string> spends = new HashSet<string>();
            JObject State => new JObject { ["player_id"] = PlayerId, ["gold"] = 500 - Debits * 60, ["remove_ads"] = true, ["inventory"] = new JObject(), ["lives"] = 5 };
            public Task PrepareAccount() { Calls.Add("link"); return Task.CompletedTask; }
            public Task<JObject> Request(string path, string method, object body = null)
            {
                Calls.Add(path);
                if (Reject) throw new InvalidOperationException("offline");
                if (RejectStatus != 0) throw new StoreHttpException(RejectStatus, RejectReason);
                var request = body == null ? new JObject() : JObject.FromObject(body);
                if(path=="/v1/wallet/grant") { LastGrant=request; return Task.FromResult(new JObject { ["wallet"]=State, ["granted"]=GrantedReward, ["deferred"]=DeferredReward }); }
                if (path == "/v1/iap/apple") { Assert.AreEqual(Transaction.Jws, (string)request["signed_transaction"]); return Task.FromResult(new JObject { ["wallet"] = State, ["transaction_id"] = Transaction.Id }); }
                if (path == "/v1/wallet/spend")
                {
                    if (spends.Add((string)request["idempotency_key"])) Debits++;
                    return Task.FromResult(new JObject { ["wallet"] = State, ["effect"] = new JObject { ["extra_moves"] = 5 } });
                }
                if (path == "/v1/iap/restore")
                {
                    var batch = ((JArray)request["signed_transactions"]).ToObject<string[]>(); RestoreBatches.Add(batch);
                    var results = new JArray();
                    for (int i = 0; i < batch.Length; i++)
                        results.Add(new JObject { ["index"] = i, ["ok"] = batch[i] == Transaction.Jws, ["transaction_id"] = batch[i] == Transaction.Jws ? Transaction.Id : null });
                    return Task.FromResult(new JObject { ["wallet"] = State, ["results"] = results });
                }
                if (path == "/v1/offers/shown") return Task.FromResult(new JObject { ["expires_at"] = DateTimeOffset.UtcNow.AddHours(48).ToString("o"), ["repeat_after"] = DateTimeOffset.UtcNow.AddHours(24).ToString("o") });
                if (path.StartsWith("/v1/wallet?")) return Task.FromResult(State);
                return Task.FromResult(new JObject { ["offers"] = new JArray(), ["economy"] = new JObject() });
            }
            public void Track(string kind, object data) { Calls.Add(kind); }
            public string Read(string key) => cache.TryGetValue(key, out var value) ? value : "";
            public void Write(string key, string value) { cache[key] = value; }
            public Task<IReadOnlyList<StoreProduct>> LoadProducts(string[] ids) => Task.FromResult<IReadOnlyList<StoreProduct>>(new[] { new StoreProduct { Id = Transaction.ProductId, LocalizedPrice = "₹ 199" } });
            public Task<StoreTransaction> Purchase(string id, string appAccountToken) { Assert.AreEqual(PlayerId, appAccountToken); if (CancelPurchase) throw new OperationCanceledException("cancelled"); return Task.FromResult(PendingPurchase ? null : Transaction); }
            public Task<IReadOnlyList<StoreTransaction>> Unfinished() => Task.FromResult<IReadOnlyList<StoreTransaction>>(HasUnfinished ? new[] { Transaction } : Array.Empty<StoreTransaction>());
            public Task<IReadOnlyList<StoreTransaction>> Restore() => Task.FromResult<IReadOnlyList<StoreTransaction>>(MixedRestore ? new[] { Transaction, new StoreTransaction { Id = "456", ProductId = "bad", Jws = "bad.jws" } } : new[] { Transaction });
            public Task Finish(string id) { Calls.Add("finish"); Finished++; return Task.CompletedTask; }
            public void Update() => TransactionUpdated?.Invoke(Transaction);
        }
        [Test] public async Task CancelledPurchaseIsDistinctAndBridgeStaysQuiet()
        {
            var f = new Fake { CancelPurchase = true }; var service = new StoreService(f,f,f);
            await service.Initialize();
            int cancelled=0, failed=0, pending=0;
            service.Cancelled += _ => cancelled++; service.Failed += _ => failed++; service.Pending += _ => pending++;
            var property = typeof(StoreRuntime).GetProperty("Service");
            var previous = property.GetValue(null);
            try
            {
                property.SetValue(null, service);
                Assert.AreEqual(Orcub.Game.PurchaseOutcome.Cancelled, await new Orcub.Features.StoreShopPort().Purchase(f.Transaction.ProductId));
                Assert.AreEqual(1,cancelled); Assert.AreEqual(0,failed); Assert.AreEqual(0,pending);
                Assert.AreEqual(0,f.Finished); Assert.False(f.Calls.Contains("iap_failed"));
            }
            finally { property.SetValue(null, previous); }
        }
        [Test] public async Task NativeCancellationBecomesOperationCancelled()
        {
            var go = new GameObject("StoreCancelTest");
            try
            {
                var kit = go.AddComponent<NativeStoreKit>();
                var calls = (Dictionary<string, TaskCompletionSource<JObject>>)typeof(NativeStoreKit)
                    .GetField("calls",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(kit);
                var source = new TaskCompletionSource<JObject>(); calls.Add("cancel-test",source);
                kit.StoreResult("{\"id\":\"cancel-test\",\"error\":\"cancelled\"}");
                try { await source.Task; Assert.Fail("Expected cancellation"); }
                catch (OperationCanceledException) { }
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }
        [TestCase(409,"reward_cap")] [TestCase(410,"reward_cap")]
        [TestCase(409,"reward_deferred")] [TestCase(410,"deferred")]
        [TestCase(410,"{\"error\":\"reward_cap\"}")]
        public async Task CappedRewardIsDeferred(long status,string reason)
        {
            var f = new Fake { RejectStatus=status, RejectReason=reason };
            Assert.AreEqual(Orcub.Game.RewardClaims.Deferred, await new StoreService(f,f,f).ClaimLevelReward(Guid.NewGuid().ToString()));
        }
        [TestCase(400)] [TestCase(422)] [TestCase(410)]
        public async Task PermanentRewardRefusalReturnsZero(long status)
        {
            var f = new Fake { RejectStatus=status, RejectReason="reward_expired" };
            Assert.AreEqual(0, await new StoreService(f,f,f).ClaimLevelReward(Guid.NewGuid().ToString()));
        }
        [TestCase("reward_pending")] [TestCase("reward_sequence_pending")]
        public async Task PendingRewardRemainsRetryable(string reason)
        {
            var f = new Fake { RejectStatus=409, RejectReason=reason };
            try { await new StoreService(f,f,f).ClaimLevelReward(Guid.NewGuid().ToString()); Assert.Fail("Expected refusal"); }
            catch (StoreHttpException error) { Assert.AreEqual(409,error.Status); }
        }
        [TestCase(true,0,-1)] [TestCase(true,10,-1)] [TestCase(false,5,5)] [TestCase(false,0,0)]
        public async Task SuccessfulRewardHonoursBackendDeferredFlag(bool deferred,int granted,int expected)
        {
            var f = new Fake { DeferredReward=deferred, GrantedReward=granted };
            Assert.AreEqual(expected,await new StoreService(f,f,f).ClaimLevelReward(Guid.NewGuid().ToString()));
        }
        [Test] public async Task UnlockConfirmationBelongsToPlayer()
        {
            var f=new Fake(); var service=new StoreService(f,f,f); var first=f.PlayerId;
            Assert.True(await service.ClaimBoosterUnlock("hint"));
            Assert.AreEqual("1",f.Read("unlock-claimed."+first+".hint"));
            f.PlayerId=Guid.NewGuid().ToString();
            Assert.AreEqual("",f.Read("unlock-claimed."+f.PlayerId+".hint"));
            Assert.True(await service.ClaimBoosterUnlock("hint"));
            Assert.AreEqual("1",f.Read("unlock-claimed."+f.PlayerId+".hint"));
        }
        [Test] public async Task UnlockRetriesSamePersistedKeyAcrossRelaunch()
        {
            var f=new Fake { RejectStatus=503 }; var service=new StoreService(f,f,f);
            Assert.False(await service.ClaimBoosterUnlock("hint"));
            var key=f.Read("unlock."+f.PlayerId+".hint"); Assert.True(Guid.TryParse(key,out _));
            f.RejectStatus=0; service=new StoreService(f,f,f);
            Assert.True(await service.ClaimBoosterUnlock("hint"));
            Assert.AreEqual(key,(string)f.LastGrant["idempotency_key"]);
            Assert.AreEqual("booster_unlock",(string)f.LastGrant["source"]);
            Assert.AreEqual("hint",(string)f.LastGrant["kind"]);
            Assert.AreEqual("",f.Read("unlock."+f.PlayerId+".hint"));
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
        [Test] public async Task MixedRestoreFinishesSuccessAndAlwaysResendsAllEntitlements()
        {
            var f = new Fake { MixedRestore = true }; var s = new StoreService(f,f,f);
            await s.Restore(); Assert.AreEqual(1,f.Finished);
            await s.Restore(); Assert.AreEqual(2,f.Finished);
            Assert.AreEqual(2,f.RestoreBatches[0].Length);
            CollectionAssert.AreEquivalent(new[] { f.Transaction.Jws, "bad.jws" },f.RestoreBatches[1]);
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
            var offer = new JObject { ["id"] = "starter_pack", ["expires_at"] = null, ["repeat_after"] = null };
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
        [Test] public void RemoveAdsSurvivesGlobalRemoteCapOverrides()
        {
            var config = new AdsConfig { levelsBetweenInterstitials = 0 };
            var policy = new AdPolicy(config,0,"orcub.test.rc."+Guid.NewGuid()) { RemoveAds = true };
            config.interstitialDailyCap = 100; config.maxInterstitialsPerSession = 100;
            config.firstInterstitialLevel = 0; config.interstitialIntervalSeconds = 0;
            Assert.False(policy.CanShowInterstitial(20,true,10000)); Assert.True(policy.CanShowRewarded);
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
