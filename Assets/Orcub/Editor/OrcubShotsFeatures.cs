using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Orcub.Core;
using Orcub.Game;
using UnityEditor;
using UnityEngine;

namespace Orcub.EditorTools
{
    /// <summary>Capture menus for the shop (shop_v1), tutorial (tutorial_v1) and daily loop (features_v1).</summary>
    public static partial class OrcubShots
    {
        /// <summary>Editor stand-in for the store: deterministic balances and outcomes, never a real purchase.</summary>
        public sealed class DemoShopPort : IShopPort
        {
            public long GoldValue = 120;
            public bool Ads, Offered = true;
            public PurchaseOutcome Next = PurchaseOutcome.Purchased;
            public float Delay = 0.2f;
            public bool Hang;
            public event Action Changed;
            public long Gold => GoldValue;
            public bool RemoveAds => Ads;
            public string Price(string id) => null;   // catalog fallback, as before StoreKit loads
            public bool StarterOffered => Offered;
            public bool CanPresentStarter(DateTimeOffset now) => Offered;
            public void MarkStarterPresented() { }
            public async Task<PurchaseOutcome> Purchase(string id)
            {
                await Wait();
                if (Next == PurchaseOutcome.Purchased) { GoldValue += ShopCatalog.Find(id)?.Gold ?? 0; Changed?.Invoke(); }
                return Next;
            }
            public async Task<PurchaseOutcome> Restore() { await Wait(); return Next; }
            public async Task<SpendResult> SpendContinue(string start, string key) { await Wait(); return new SpendResult(SpendError.None, 5); }
            public async Task<SpendResult> BuyBooster(string kind, string key) { await Wait(); return new SpendResult(SpendError.None); }
            async Task Wait()
            {
                double end = EditorApplication.timeSinceStartup + Delay;
                while (Hang || EditorApplication.timeSinceStartup < end) await Task.Yield();
            }
        }

        static string ShotsDir => Path.GetFullPath("ArtSource/.automation/shots");

        static void Begin(string prefix)
        {
            if (!EditorApplication.isPlaying) { Debug.LogWarning("[Orcub] enter play mode first"); return; }
            _dir = ShotsDir;
            Directory.CreateDirectory(_dir);
            foreach (var f in Directory.GetFiles(_dir, prefix + "*.png")) File.Delete(f);
            Application.runInBackground = true;
            Steps.Clear();
        }

        static BoardView Board => UnityEngine.Object.FindFirstObjectByType<BoardView>();
        static void CallBoard(string method, params object[] args)
        {
            var bv = Board;
            bv.GetType().GetMethod(method, Flags).Invoke(bv, args);
        }
        static LevelHud Hud => UnityEngine.Object.FindFirstObjectByType<LevelHud>();

        /// <summary>Removes every overlay a feature step may have left.</summary>
        static void ClearOverlays()
        {
            foreach (var o in UnityEngine.Object.FindObjectsByType<AccountOverlay>(FindObjectsSortMode.None)) UnityEngine.Object.Destroy(o.gameObject);
            foreach (var r in UnityEngine.Object.FindObjectsByType<ShopReceipt>(FindObjectsSortMode.None)) UnityEngine.Object.Destroy(r.gameObject);
            var flow = ShopFlow.Instance;
            if (flow != null)
                foreach (var name in new[] { "_shop", "_starter", "_booster", "_receipt" })
                    flow.GetType().GetField(name, Flags)?.SetValue(flow, null);
            Hud?.ClosePopup();
        }

        // ------------------------------------------------------------------ shop

        [MenuItem("Orcub/Capture Shop")]
        public static void CaptureShop()
        {
            Begin("s");
            var demo = new DemoShopPort();
            Add(0f, () => { ShopGateway.Fake = demo; ClearOverlays(); CallBoard("ShowHome"); }, null);
            Add(0.9f, null, "s01_home_currency");
            Add(0f, () => ShopFlow.OpenShop(), null);
            Add(0.7f, null, "s02_shop");
            Add(0f, () => { demo.Hang = true; _ = ShopFlow.Instance.Model.Buy(ShopCatalog.Large); }, null);
            Add(0.4f, null, "s03_shop_purchasing");
            Add(0f, () => { demo.Hang = false; demo.Next = PurchaseOutcome.Pending; }, null);
            Add(0.6f, null, "s04_shop_pending");
            Add(0f, () => { demo.Next = PurchaseOutcome.Failed; _ = ShopFlow.Instance.Model.Buy(ShopCatalog.Medium); }, null);
            Add(0.7f, null, "s05_shop_failed");
            Add(0f, () => { demo.Next = PurchaseOutcome.Purchased; _ = ShopFlow.Instance.Model.Restore(); }, null);
            Add(0.7f, null, "s06_shop_restored");
            Add(0f, () => { demo.Ads = true; demo.Offered = false; ClearOverlays(); ShopFlow.OpenShop(); }, null);
            Add(0.7f, null, "s07_shop_no_starter_ads_owned");
            Add(0f, () => { demo.Ads = false; demo.Offered = true; ClearOverlays(); ShopFlow.Instance.ShowStarter(); }, null);
            Add(0.7f, null, "s08_starter_offer");
            Add(0f, () => { ClearOverlays(); ShopFlow.OpenShop(); }, null);
            Add(0.5f, () => { demo.Next = PurchaseOutcome.Purchased; demo.GoldValue = 120; _ = ShopFlow.Instance.Model.Buy(ShopCatalog.Medium); }, null);
            Add(0.75f, null, "s09_purchase_success");
            Add(0f, () => { ClearOverlays(); demo.GoldValue = 30; ShopFlow.OfferBooster(BoosterKind.Hint, null); }, null);
            Add(0.7f, null, "s10_booster_buy");
            Add(0f, () => { ClearOverlays(); demo.GoldValue = 120; CallBoard("PlayLevel", Chapters.Frontier); }, null);
            Add(2.4f, () => OpenOutOfMoves(0), null);
            Add(1.0f, null, "s11_out_of_moves");
            Add(0f, () => OpenOutOfMoves(1), null);
            Add(1.0f, null, "s12_out_of_moves_second");
            Add(0f, () => { Hud.ClosePopup(); Hud.ShowOutOfLives(() => { }, () => Task.FromResult(false), () => { }, () => { }); }, null);
            Add(1.0f, null, "s13_out_of_lives");
            Add(0f, () => { ClearOverlays(); CallBoard("ShowMap", Chapters.Of(Chapters.Frontier)); }, null);
            Add(0.8f, null, "s14_map_topbar");
            Add(0f, () => { ClearOverlays(); ShopGateway.Fake = null; CallBoard("ShowHome"); }, null);
            Start();
        }

        /// <summary>The out-of-moves sheet as the board builds it, with this attempt's n-th gold continue priced.</summary>
        static void OpenOutOfMoves(int goldContinues)
        {
            var bv = Board;
            Hud.ClosePopup();
            bv.GetType().GetField("_goldContinues", Flags).SetValue(bv, goldContinues);
            var offer = new LevelHud.ContinueOffer { Moves = 5, CanWatch = goldContinues == 0, OnWatch = () => { } };
            bv.GetType().GetMethod("AddGoldContinue", Flags).Invoke(bv, new object[] { offer });
            Hud.ShowOutOfMoves(offer, () => { }, () => { });
        }

        // ------------------------------------------------------------------ tutorial

        static TutorialFlow Flow => (TutorialFlow)Board.GetType().GetField("_tutorial", Flags).GetValue(Board);

        static void ResetTutorialPrefs()
        {
            foreach (TutorialStep s in Enum.GetValues(typeof(TutorialStep))) PlayerPrefs.DeleteKey("orcub.tutorial." + (int)s);
        }

        [MenuItem("Orcub/Capture Tutorial")]
        public static void CaptureTutorial()
        {
            Begin("t");
            Add(0f, () => { ClearOverlays(); ResetTutorialPrefs(); CallBoard("PlayLevel", 0); }, null);
            Add(2.6f, () => Flow.Begin(1, 1), null);
            Add(1.1f, null, "t01_rotate");
            Add(0f, () =>
            {   // a real tap move on the clamped ring: the clamp releases and the lesson advances by itself
                var bv = Board;
                int ring = (int)bv.GetType().GetField("_tutRing", Flags).GetValue(bv);
                bv.GetType().GetMethod("CountMove", Flags).Invoke(bv, null);
                bv.GetType().GetMethod("StartSmartSpin", Flags).Invoke(bv, new object[] { ring, new GestureTracker(bv.Model, ring) });
            }, null);
            AddUntil(() => Flow.Current == TutorialStep.Release);
            Add(0.5f, null, "t02_released");
            AddUntil(() => Flow.Current == TutorialStep.Pull);
            Add(0.9f, null, "t03_pull");
            Add(0f, () => CallBoard("PlayLevel", 1), null);
            Add(2.8f, () => Flow.Begin(2, 2), null);
            Add(0.8f, null, "t04_moves");
            foreach (var (level, step, shot) in new[] { (5, TutorialStep.UnlockHint, "t05_unlock_hint"), (8, TutorialStep.UnlockUndo, "t06_unlock_undo"), (12, TutorialStep.UnlockUnclamp, "t07_unlock_unclamp") })
            {
                int l = level;
                Add(0f, () => CallBoard("PlayLevel", l - 1), null);
                Add(2.8f, () =>
                {
                    foreach (var u in TutorialFlow.Unlocks) Hud.SetBoosterLocked(u.kind, u.level > l);   // as a player first reaching it
                    Flow.Begin(l, l);
                }, null);
                Add(0.8f, null, shot);
            }
            Add(0f, () => { Flow.End(); Hud.SetBoosterLocked(BoosterKind.Unclamp, true); CallBoard("UseBooster", BoosterKind.Unclamp); }, null);
            Add(0.35f, null, "t08_locked_toast");
            Add(0f, () => { ResetTutorialPrefs(); CallBoard("ShowHome"); }, null);
            Start();
        }
    }
}
