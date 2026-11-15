using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Orcub.Game;
using Orcub.Net;
using Orcub.Store;

namespace Orcub.Features
{
    /// <summary>
    /// IShopPort over StoreRuntime.Service (Store README "UI integration"). Purchase outcomes come from the
    /// service's Purchased / Pending / Failed events for that product; nothing is granted here.
    /// </summary>
    public sealed class StoreShopPort : IShopPort
    {
        StoreService _bound;
        public event Action Changed;

        static StoreService Service => StoreRuntime.Service;

        /// <summary>Follows the service instance (it is created after boot and replaced on reload).</summary>
        public void Poll()
        {
            var s = Service;
            if (s == _bound) return;
            if (_bound != null) { _bound.Changed -= Raise; _bound.Wallet.Changed -= Raise; }
            _bound = s;
            if (s != null) { s.Changed += Raise; s.Wallet.Changed += Raise; }
            Raise();
        }

        void Raise() => Changed?.Invoke();

        public long Gold => Service?.Wallet.Balance ?? 0;
        public bool RemoveAds => Service?.Wallet.RemoveAds == true;

        public string Price(string productId) => Service?.Products?.FirstOrDefault(p => p.Id == productId)?.LocalizedPrice;

        JObject StarterOffer => Service?.Offers?.OfType<JObject>().FirstOrDefault(o => (string)o["id"] == "starter_pack");

        public bool StarterOffered => StarterOffer != null;

        public bool CanPresentStarter(DateTimeOffset now)
        {
            var offer = StarterOffer;
            return offer != null && Service.CanPresentOffer(offer, now);
        }

        public void MarkStarterPresented()
        {
            var offer = StarterOffer;
            if (offer != null) Service.MarkOfferPresented(offer);
        }

        public async Task<PurchaseOutcome> Purchase(string productId)
        {
            var s = Service;
            if (s == null || s.Products == null || !s.Products.Any(p => p.Id == productId)) return PurchaseOutcome.Unavailable;
            PurchaseOutcome? result = null;
            void Ok(string id) { if (id == productId) result = PurchaseOutcome.Purchased; }
            void Wait(string id) { if (id == productId && result == null) result = PurchaseOutcome.Pending; }
            void Cancel(string id) { if (id == productId && result == null) result = PurchaseOutcome.Cancelled; }
            void Bad(string id) { if (id == productId && result == null) result = PurchaseOutcome.Failed; }
            s.Purchased += Ok; s.Pending += Wait; s.Failed += Bad; s.Cancelled += Cancel;
            try { await s.Purchase(productId); }
            catch (Exception) { result ??= PurchaseOutcome.Failed; }
            finally { s.Purchased -= Ok; s.Pending -= Wait; s.Failed -= Bad; s.Cancelled -= Cancel; }
            return result ?? PurchaseOutcome.Failed;
        }

        public async Task<PurchaseOutcome> Restore()
        {
            var s = Service;
            if (s == null) return PurchaseOutcome.Unavailable;
            bool failed = false, pending = false;
            void Bad(string id) { if (id == "restore") failed = true; }
            void Wait(string id) => pending = true;
            s.Failed += Bad; s.Pending += Wait;
            try { await s.Restore(); }
            catch (Exception) { failed = true; }
            finally { s.Failed -= Bad; s.Pending -= Wait; }
            return failed ? PurchaseOutcome.Failed : pending ? PurchaseOutcome.Pending : PurchaseOutcome.Purchased;
        }

        public async Task<SpendResult> SpendContinue(string levelStartEventId, string actionKey)
        {
            var s = Service;
            if (s == null) return new SpendResult(SpendError.Unavailable);
            try
            {
                // the server accepts a continue only for a level_start it has received: upload it first
                if (ClientServices.Instance != null) await ClientServices.Instance.Flush();
                var effect = await s.Wallet.Spend(new SpendSink("continue", levelStartEventId), actionKey);
                return new SpendResult(SpendError.None, (int?)effect?["extra_moves"] ?? 0);
            }
            catch (StoreHttpException e) { return new SpendResult(Map(e)); }
            catch (ArgumentException) { return new SpendResult(SpendError.Unavailable); }
            catch (Exception) { return new SpendResult(SpendError.Offline); }
        }

        public async Task<SpendResult> BuyBooster(string kind, string actionKey)
        {
            var s = Service;
            if (s == null) return new SpendResult(SpendError.Unavailable);
            try
            {
                await s.Wallet.Spend(new SpendSink("booster", actionKey, kind), actionKey);
                return new SpendResult(SpendError.None);
            }
            catch (StoreHttpException e) { return new SpendResult(Map(e)); }
            catch (ArgumentException) { return new SpendResult(SpendError.Unavailable); }
            catch (Exception) { return new SpendResult(SpendError.Offline); }
        }

        static SpendError Map(StoreHttpException e) =>
            e.Status == 409 ? SpendError.InsufficientGold : e.Status >= 500 || e.Status == 0 || e.Status == 429 ? SpendError.Offline : SpendError.Unavailable;
    }
}
