using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Orcub.Game;

namespace Orcub.Tests
{
    /// <summary>
    /// Shop presentation state against a fake store. Every fake returns an already-completed task (or one that is
    /// never completed), so no test waits on a continuation posted to the Unity main thread.
    /// </summary>
    public sealed class ShopTests
    {
        sealed class FakeStore : IShopPort
        {
            public long GoldValue = 100;
            public PurchaseOutcome Outcome = PurchaseOutcome.Purchased;
            public Task<PurchaseOutcome> Hold;   // when set: purchases stay in flight
            public readonly Dictionary<string, string> Prices = new Dictionary<string, string>();
            public readonly List<string> Bought = new List<string>();
            public bool Ads;
            public event Action Changed { add { } remove { } }
            public long Gold => GoldValue;
            public bool RemoveAds => Ads;
            public string Price(string id) => Prices.TryGetValue(id, out var p) ? p : null;
            public bool StarterOffered => true;
            public bool CanPresentStarter(DateTimeOffset now) => true;
            public void MarkStarterPresented() { }
            public Task<PurchaseOutcome> Purchase(string id)
            {
                Bought.Add(id);
                if (Hold != null) return Hold;
                if (Outcome == PurchaseOutcome.Purchased) GoldValue += ShopCatalog.Find(id)?.Gold ?? 0;
                return Task.FromResult(Outcome);
            }
            public Task<PurchaseOutcome> Restore() => Task.FromResult(Outcome);
            public Task<SpendResult> SpendContinue(string start, string key) => Task.FromResult(new SpendResult(SpendError.None, 5));
            public Task<SpendResult> BuyBooster(string kind, string key) => Task.FromResult(new SpendResult(SpendError.None));
        }

        [Test]
        public void Purchased_ReportsSuccessWithTheBalanceBefore()
        {
            var store = new FakeStore { GoldValue = 120 };
            var model = new ShopModel(() => store);
            string product = null; long before = -1;
            model.Succeeded += (id, b) => { product = id; before = b; };
            var t = model.Buy(ShopCatalog.Medium);
            Assert.IsTrue(t.IsCompleted);
            Assert.AreEqual(PurchaseOutcome.Purchased, t.Result);
            Assert.AreEqual(ShopCatalog.Medium, product);
            Assert.AreEqual(120, before);
            Assert.AreEqual(720, store.Gold);
            Assert.IsNull(model.Message);
            Assert.IsFalse(model.Busy);
        }

        [Test]
        public void Pending_ShowsWaitingNote_AndGrantsNothing()
        {
            var store = new FakeStore { Outcome = PurchaseOutcome.Pending };
            var model = new ShopModel(() => store);
            bool succeeded = false;
            model.Succeeded += (_, __) => succeeded = true;
            model.Buy(ShopCatalog.Large);
            Assert.IsFalse(succeeded);
            Assert.AreEqual(ShopModel.Tone.Info, model.MessageTone);
            StringAssert.Contains("approval", model.Message);
            Assert.AreEqual(100, store.Gold);
        }

        [Test]
        public void Failed_ShowsRetryableError()
        {
            var model = new ShopModel(() => new FakeStore { Outcome = PurchaseOutcome.Failed });
            model.Buy(ShopCatalog.Small);
            Assert.AreEqual(ShopModel.Tone.Error, model.MessageTone);
            StringAssert.Contains("try again", model.Message);
        }

        [Test]
        public void Cancelled_IsQuiet()
        {
            var model = new ShopModel(() => new FakeStore { Outcome = PurchaseOutcome.Cancelled });
            model.Buy(ShopCatalog.Small);
            Assert.IsNull(model.Message);
        }

        [Test]
        public void NoStore_IsUnavailable()
        {
            var model = new ShopModel(() => null);
            var t = model.Buy(ShopCatalog.Huge);
            Assert.AreEqual(PurchaseOutcome.Unavailable, t.Result);
            StringAssert.Contains("isn’t available", model.Message);
        }

        [Test]
        public void InFlightPurchase_BlocksASecondOne_AndSelection()
        {
            var store = new FakeStore { Hold = new TaskCompletionSource<PurchaseOutcome>().Task };
            var model = new ShopModel(() => store);
            model.Buy(ShopCatalog.Large);
            Assert.IsTrue(model.Busy);
            Assert.AreEqual(ShopCatalog.Large, model.BusyProduct);
            var second = model.Buy(ShopCatalog.Huge);
            Assert.AreEqual(PurchaseOutcome.Failed, second.Result);
            model.Select(ShopCatalog.Small);
            Assert.AreEqual(ShopCatalog.Large, model.Selected);
            Assert.AreEqual(1, store.Bought.Count);
        }

        [Test]
        public void Restore_ReportsEachState()
        {
            var store = new FakeStore();
            var model = new ShopModel(() => store);
            model.Restore();
            Assert.AreEqual(ShopModel.Tone.Success, model.MessageTone);
            store.Outcome = PurchaseOutcome.Pending;
            model.Restore();
            StringAssert.Contains("pending", model.Message);
            store.Outcome = PurchaseOutcome.Failed;
            model.Restore();
            Assert.AreEqual(ShopModel.Tone.Error, model.MessageTone);
            Assert.IsFalse(model.Restoring);
        }

        [Test]
        public void Prices_UseStoreKitWhenLoaded_ElseCatalog()
        {
            var store = new FakeStore();
            Assert.AreEqual("$9.99", ShopCatalog.PriceOf(store, ShopCatalog.Large));
            store.Prices[ShopCatalog.Large] = "9,99 €";
            Assert.AreEqual("9,99 €", ShopCatalog.PriceOf(store, ShopCatalog.Large));
            Assert.AreEqual("$1.99", ShopCatalog.PriceOf(null, ShopCatalog.Starter));
            Assert.AreEqual("$3.99", ShopCatalog.PriceOf(store, ShopCatalog.RemoveAds));
        }

        [Test]
        public void ContinuePrice_EscalatesLikeTheServer()
        {
            Assert.AreEqual(60, ShopCatalog.ContinuePrice(0));
            Assert.AreEqual(90, ShopCatalog.ContinuePrice(1));
            Assert.AreEqual(135, ShopCatalog.ContinuePrice(2));
            Assert.AreEqual(203, ShopCatalog.ContinuePrice(3));
        }

        [Test]
        public void GoldSpend_NotEnoughGold_OpensShopWithoutSpending()
        {
            var spend = new GoldSpend();
            int calls = 0, shop = 0;
            var r = spend.Run(_ => { calls++; return Task.FromResult(new SpendResult(SpendError.None)); }, () => shop++, 30, 40).Result;
            Assert.AreEqual(SpendError.InsufficientGold, r.Error);
            Assert.AreEqual(0, calls);
            Assert.AreEqual(1, shop);
            Assert.IsNull(spend.Error);
        }

        [Test]
        public void GoldSpend_RetryAfterNetworkFailure_ReusesTheSameKey()
        {
            var spend = new GoldSpend();
            var keys = new List<string>();
            var fail = spend.Run(k => { keys.Add(k); return Task.FromResult(new SpendResult(SpendError.Offline)); }, null, 100, 60).Result;
            Assert.AreEqual(SpendError.Offline, fail.Error);
            Assert.IsNotNull(spend.Error);
            var ok = spend.Run(k => { keys.Add(k); return Task.FromResult(new SpendResult(SpendError.None, 5)); }, null, 100, 60).Result;
            Assert.IsTrue(ok.Ok);
            Assert.AreEqual(5, ok.ExtraMoves);
            Assert.AreEqual(keys[0], keys[1]);
            spend.Run(k => { keys.Add(k); return Task.FromResult(new SpendResult(SpendError.None)); }, null, 100, 60);
            Assert.AreNotEqual(keys[1], keys[2], "a new intentional purchase gets a new key");
        }

        [Test]
        public void GoldSpend_ServerSaysInsufficient_OpensShop()
        {
            var spend = new GoldSpend();
            int shop = 0;
            var r = spend.Run(_ => Task.FromResult(new SpendResult(SpendError.InsufficientGold)), () => shop++, 100, 60).Result;
            Assert.AreEqual(SpendError.InsufficientGold, r.Error);
            Assert.AreEqual(1, shop);
            Assert.IsFalse(spend.Busy);
        }
    }
}
