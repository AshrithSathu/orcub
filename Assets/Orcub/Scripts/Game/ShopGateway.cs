using System;
using System.Threading.Tasks;

namespace Orcub.Game
{
    /// <summary>How a StoreKit purchase / restore ended, as the shop UI needs to present it.</summary>
    public enum PurchaseOutcome { Purchased, Pending, Failed, Cancelled, Unavailable }

    /// <summary>Why a gold spend (continue, booster) did not go through.</summary>
    public enum SpendError { None, InsufficientGold, Offline, Unavailable }

    public readonly struct SpendResult
    {
        public readonly SpendError Error;
        public readonly int ExtraMoves;   // continue only: the server's effect (0 on a replayed key)
        public SpendResult(SpendError error, int extraMoves = 0) { Error = error; ExtraMoves = extraMoves; }
        public bool Ok => Error == SpendError.None;
    }

    /// <summary>
    /// SDK-free shop boundary (Game cannot reference Store). Bound by Orcub.Features (StoreShopPort) to
    /// StoreRuntime.Service; null while the store hasn't booted. Captures / tests use a fake.
    /// </summary>
    public interface IShopPort
    {
        long Gold { get; }
        bool RemoveAds { get; }
        /// <summary>StoreKit's localized display price, or null when the product isn't loaded.</summary>
        string Price(string productId);
        /// <summary>The server's starter offer is in the fresh /v1/config (never invented client-side).</summary>
        bool StarterOffered { get; }
        bool CanPresentStarter(DateTimeOffset now);
        void MarkStarterPresented();
        event Action Changed;
        Task<PurchaseOutcome> Purchase(string productId);
        Task<PurchaseOutcome> Restore();
        /// <summary>+5 moves; <paramref name="levelStartEventId"/> is the acknowledged level_start of this attempt.</summary>
        Task<SpendResult> SpendContinue(string levelStartEventId, string actionKey);
        Task<SpendResult> BuyBooster(string kind, string actionKey);
    }

    public static class ShopGateway
    {
        public static IShopPort Port;
        /// <summary>Capture tools: a demo port that wins over the real one.</summary>
        public static IShopPort Fake;
        public static IShopPort Current => Fake ?? Port;
        public static event Action PortChanged;
        public static void NotifyPortChanged() => PortChanged?.Invoke();
    }

    /// <summary>One purchasable product as the shop lists it. Display prices come from StoreKit when loaded.</summary>
    public sealed class ShopItem
    {
        public readonly string Id, FallbackPrice, Badge;
        public readonly int Gold;
        public ShopItem(string id, int gold, string fallbackPrice, string badge = null)
        { Id = id; Gold = gold; FallbackPrice = fallbackPrice; Badge = badge; }
    }

    /// <summary>The approved shop_v1 catalog (economy-v1). Prices here are only the fallback labels.</summary>
    public static class ShopCatalog
    {
        public const string Starter = "com.orcub.puzzle.starter_pack";
        public const string Small = "com.orcub.puzzle.gold_small";
        public const string Medium = "com.orcub.puzzle.gold_medium";
        public const string Large = "com.orcub.puzzle.gold_large";
        public const string Huge = "com.orcub.puzzle.gold_huge";
        public const string RemoveAds = "com.orcub.puzzle.remove_ads";

        public const int StarterGold = 500, StarterBoosters = 3, RefillGold = 90, BoosterGold = 40;
        public const int ContinueGold = 60, ContinueMoves = 5;

        public static readonly ShopItem[] Packs =
        {
            new ShopItem(Small, 100, "$0.99"),
            new ShopItem(Medium, 600, "$4.99"),
            new ShopItem(Large, 1400, "$9.99", "Most popular"),
            new ShopItem(Huge, 3200, "$19.99", "Best value"),
        };
        public static readonly ShopItem StarterItem = new ShopItem(Starter, StarterGold, "$1.99");
        public static readonly ShopItem RemoveAdsItem = new ShopItem(RemoveAds, 0, "$3.99");

        public static ShopItem Find(string id)
        {
            if (id == Starter) return StarterItem;
            if (id == RemoveAds) return RemoveAdsItem;
            foreach (var p in Packs) if (p.Id == id) return p;
            return null;
        }

        /// <summary>StoreKit display price when loaded, otherwise the catalog's.</summary>
        public static string PriceOf(IShopPort port, string id)
        {
            var live = port?.Price(id);
            return !string.IsNullOrEmpty(live) ? live : Find(id)?.FallbackPrice ?? "";
        }

        /// <summary>Server formula (backend continue_price): ceil(60 * 1.5^previous). Display only; the server charges.</summary>
        public static int ContinuePrice(int previousGoldContinues) =>
            (int)Math.Ceiling(ContinueGold * Math.Pow(1.5, Math.Max(0, previousGoldContinues)) - 1e-9);

        public static string Gold(long amount) => amount.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The shop's presentation state, independent of Unity UI so it can be tested with a fake store:
    /// which pack is selected, what is busy, and the one message line under the Buy button.
    /// Never grants anything itself: Purchased only reports what the store confirmed.
    /// </summary>
    public sealed class ShopModel
    {
        public enum Tone { Quiet, Info, Error, Success }

        readonly Func<IShopPort> _port;
        public string Selected = ShopCatalog.Large;
        public string BusyProduct { get; private set; }
        public bool Restoring { get; private set; }
        public string Message { get; private set; }
        public Tone MessageTone { get; private set; }
        public bool Busy => BusyProduct != null || Restoring;
        public event Action Changed;
        /// <summary>A purchase the store confirmed: product id and the balance before it (for the counter animation).</summary>
        public event Action<string, long> Succeeded;

        public ShopModel(Func<IShopPort> port) { _port = port; }

        public IShopPort Port => _port();

        public string Price(string id) => ShopCatalog.PriceOf(Port, id);

        /// <summary>A fresh screen never shows the previous screen's note.</summary>
        public void ClearMessage()
        {
            if (Busy || Message == null) return;
            SetMessage(null, Tone.Quiet);
        }

        public void Select(string id)
        {
            if (Busy || Selected == id) return;
            Selected = id;
            Message = null;
            Changed?.Invoke();
        }

        public async Task<PurchaseOutcome> Buy(string id)
        {
            if (Busy) return PurchaseOutcome.Failed;
            var port = Port;
            BusyProduct = id;
            SetMessage(null, Tone.Quiet);
            long before = port?.Gold ?? 0;
            PurchaseOutcome outcome;
            if (port == null) outcome = PurchaseOutcome.Unavailable;
            else
            {
                try { outcome = await port.Purchase(id); }
                catch (Exception) { outcome = PurchaseOutcome.Failed; }
            }
            BusyProduct = null;
            switch (outcome)
            {
                case PurchaseOutcome.Purchased: SetMessage(null, Tone.Quiet); Succeeded?.Invoke(id, before); break;
                case PurchaseOutcome.Pending: SetMessage("Waiting for approval. We’ll add it once it goes through.", Tone.Info); break;
                case PurchaseOutcome.Cancelled: SetMessage(null, Tone.Quiet); break;
                case PurchaseOutcome.Unavailable: SetMessage("The store isn’t available right now. Try again later.", Tone.Error); break;
                default: SetMessage("Purchase didn’t complete. Please try again.", Tone.Error); break;
            }
            return outcome;
        }

        public async Task<PurchaseOutcome> Restore()
        {
            if (Busy) return PurchaseOutcome.Failed;
            var port = Port;
            Restoring = true;
            SetMessage(null, Tone.Quiet);
            PurchaseOutcome outcome;
            if (port == null) outcome = PurchaseOutcome.Unavailable;
            else
            {
                try { outcome = await port.Restore(); }
                catch (Exception) { outcome = PurchaseOutcome.Failed; }
            }
            Restoring = false;
            switch (outcome)
            {
                case PurchaseOutcome.Purchased: SetMessage("Purchases restored.", Tone.Success); break;
                case PurchaseOutcome.Pending: SetMessage("Some purchases are still pending. We’ll add them soon.", Tone.Info); break;
                case PurchaseOutcome.Cancelled: SetMessage(null, Tone.Quiet); break;
                case PurchaseOutcome.Unavailable: SetMessage("The store isn’t available right now. Try again later.", Tone.Error); break;
                default: SetMessage("Couldn’t restore. Check your connection and try again.", Tone.Error); break;
            }
            return outcome;
        }

        void SetMessage(string text, Tone tone)
        {
            Message = text;
            MessageTone = tone;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// A gold spend button's state (out-of-moves continue, booster purchase): idle, busy, or a failure line.
    /// Insufficient gold never shows an error: it asks the caller to open the shop.
    /// </summary>
    public sealed class GoldSpend
    {
        public bool Busy { get; private set; }
        public string Error { get; private set; }
        string _key;

        /// <summary>
        /// Runs one spend. The idempotency key is kept across a failed attempt (a retry of the same intent
        /// must reuse it) and dropped after success or a definite refusal.
        /// </summary>
        public async Task<SpendResult> Run(Func<string, Task<SpendResult>> spend, Action openShop, long gold, int price)
        {
            if (Busy) return new SpendResult(SpendError.Unavailable);
            Error = null;
            if (gold < price) { openShop?.Invoke(); return new SpendResult(SpendError.InsufficientGold); }
            _key ??= Guid.NewGuid().ToString();
            Busy = true;
            SpendResult r;
            try { r = await spend(_key); }
            catch (Exception) { r = new SpendResult(SpendError.Offline); }
            Busy = false;
            switch (r.Error)
            {
                case SpendError.None: _key = null; break;
                case SpendError.InsufficientGold: _key = null; openShop?.Invoke(); break;
                case SpendError.Unavailable: _key = null; Error = "Not available right now"; break;
                default: Error = "Couldn’t connect. Try again"; break;
            }
            return r;
        }
    }
}
