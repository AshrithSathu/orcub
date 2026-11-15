using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Orcub.Core;
using Orcub.Game;
using UnityEngine;
using UnityEngine.UI;

namespace Orcub.Tests
{
    /// <summary>
    /// Review m (UI pass) and review n notes: H1 overlays lock the board, H2 the reward queue never stalls, M1 streak
    /// bonuses wait for their booster's unlock, M2 unlock cards show once, M3 / L6 the continue key and its replay,
    /// M4 the booster sheet can't close mid-purchase, L2 an empty restore. Fakes complete synchronously and Drain runs
    /// with attempts: 1, so no test waits on a continuation.
    /// </summary>
    public sealed class ReviewMTests
    {
        string _savedQueue;
        Func<int, string, Task<int>> _savedClaim;
        Func<string> _savedPlayer;
        Func<DateTimeOffset> _savedClock;
        IShopPort _savedFake;
        DateTimeOffset _now;
        string _player;
        readonly List<GameObject> _made = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            _savedQueue = PlayerPrefs.HasKey(RewardClaims.PendingKey) ? PlayerPrefs.GetString(RewardClaims.PendingKey) : null;
            _savedClaim = ClientGateway.ClaimLevelReward;
            _savedPlayer = ClientGateway.PlayerId;
            _savedClock = RewardClaims.Clock;
            _savedFake = ShopGateway.Fake;
            PlayerPrefs.DeleteKey(RewardClaims.PendingKey);
            _now = new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
            RewardClaims.Clock = () => _now;
            _player = "player-a";
            ClientGateway.PlayerId = () => _player;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _made) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _made.Clear();
            if (_savedQueue == null) PlayerPrefs.DeleteKey(RewardClaims.PendingKey); else PlayerPrefs.SetString(RewardClaims.PendingKey, _savedQueue);
            ClientGateway.ClaimLevelReward = _savedClaim;
            ClientGateway.PlayerId = _savedPlayer;
            RewardClaims.Clock = _savedClock;
            ShopGateway.Fake = _savedFake;
        }

        UiKit Kit() => new UiKit(ScriptableObject.CreateInstance<OrcubArt>());

        GameObject Root()
        {
            var go = new GameObject("ReviewMTestRoot");
            _made.Add(go);
            return go;
        }

        // ------------------------------------------------------------------ H1

        [Test]
        public void AnyOpenOverlay_BlocksTheBoard_UntilItIsGone()
        {
            int before = AccountOverlay.OpenCount;
            if (before == 0) Assert.IsFalse(BoardView.BoardInputBlocked(false, false), "nothing open: the board takes input");
            var root = Root();
            var kit = Kit();
            var shop = AccountOverlay.Create(root.transform, kit, "Shop", ShopFlow.ShopOrder, 0f, felt: false);
            var settings = AccountOverlay.Create(root.transform, kit, "Settings", 450, 0.55f);
            Assert.AreEqual(before + 2, AccountOverlay.OpenCount);
            Assert.IsTrue(BoardView.BoardInputBlocked(false, false), "an open shop / sheet blocks board input");

            UnityEngine.Object.DestroyImmediate(shop.gameObject);
            Assert.IsTrue(BoardView.BoardInputBlocked(false, false), "settings is still open");
            UnityEngine.Object.DestroyImmediate(settings.gameObject);
            Assert.AreEqual(before, AccountOverlay.OpenCount, "a destroyed overlay no longer counts");
            if (before == 0) Assert.IsFalse(BoardView.BoardInputBlocked(false, false));
            Assert.IsTrue(BoardView.BoardInputBlocked(true, false));
            Assert.IsTrue(BoardView.BoardInputBlocked(false, true));
        }

        // ------------------------------------------------------------------ H2 (+ review n, 3)

        [Test]
        public void RefusedHead_AgesOut_AfterADayAndThreeRefusals()
        {
            RewardClaims.Add(30, "lost");
            RewardClaims.Add(31, "fine");
            ClientGateway.ClaimLevelReward = (i, id) => i == 30 ? throw new RewardPendingException() : Task.FromResult(5);
            var granted = new List<int>();

            RewardClaims.Drain((i, g) => granted.Add(i), attempts: 1);
            CollectionAssert.AreEqual(new[] { 31 }, granted, "the later claim is paid at once");
            var head = RewardClaims.Pending()[0];
            Assert.AreEqual(30, head.Index);
            Assert.AreEqual(1, head.Refusals);
            Assert.AreEqual(_now.ToUnixTimeSeconds(), head.FirstRefusedUtc);

            RewardClaims.Drain(null, attempts: 1);
            RewardClaims.Drain(null, attempts: 1);
            Assert.AreEqual(1, RewardClaims.Pending().Count, "three refusals within a day: still kept");

            _now = _now.AddHours(25);
            RewardClaims.Drain(null, attempts: 1);
            Assert.AreEqual(0, RewardClaims.Pending().Count, "refused for more than a day: aged out, the queue never stalls");
        }

        [Test]
        public void FreshEvidence_RestartsTheRefusalClock()
        {
            RewardClaims.Add(7, "old");
            ClientGateway.ClaimLevelReward = (i, id) => throw new RewardPendingException();
            RewardClaims.Drain(null, attempts: 1);
            RewardClaims.Add(7, "new");
            var c = RewardClaims.Pending()[0];
            Assert.AreEqual("new", c.EventId);
            Assert.AreEqual(0, c.Refusals);
            Assert.AreEqual(0, c.FirstRefusedUtc);
        }

        [Test]
        public void AccountSwitch_DropsTheOldPlayersRefusedClaims_AndKeepsTheirPaidOnes()
        {
            RewardClaims.Add(10, "mine-before");    // won as player-a
            RewardClaims.Add(11, "merged");         // won as player-a, carried by the merge
            _player = "player-b";                   // signed out / switched account
            RewardClaims.Add(12, "theirs");
            var seen = new List<int>();
            ClientGateway.ClaimLevelReward = (i, id) =>
            {
                seen.Add(i);
                if (i == 10) throw new RewardPendingException();   // its event didn't follow the new player
                return Task.FromResult(5);
            };
            var granted = new List<int>();
            RewardClaims.Drain((i, g) => granted.Add(i), attempts: 1);
            CollectionAssert.AreEqual(new[] { 10, 11, 12 }, seen, "a foreign claim gets one try under the new player");
            CollectionAssert.AreEqual(new[] { 11, 12 }, granted);
            Assert.AreEqual(0, RewardClaims.Pending().Count, "the refused foreign claim is dropped at once, never blocking");
        }

        [Test]
        public void OneTransientFailure_SkipsOnlyThatClaim_TwoInARowEndThePass()
        {
            RewardClaims.Add(1, "a");
            RewardClaims.Add(2, "b");
            RewardClaims.Add(3, "c");
            RewardClaims.Add(4, "d");
            var seen = new List<int>();
            ClientGateway.ClaimLevelReward = (i, id) =>
            {
                seen.Add(i);
                if (i == 1) throw new InvalidOperationException("timeout");
                return Task.FromResult(5);
            };
            RewardClaims.Drain(null, attempts: 1);
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, seen, "one claim's failure never blocks the next");
            CollectionAssert.AreEqual(new[] { 1 }, RewardClaims.Pending().ConvertAll(c => c.Index));
            Assert.AreEqual(0, RewardClaims.Pending()[0].Refusals, "a transport failure never ages a claim out");

            RewardClaims.Add(5, "e");
            RewardClaims.Add(6, "f");
            seen.Clear();
            ClientGateway.ClaimLevelReward = (i, id) => { seen.Add(i); throw new InvalidOperationException("offline"); };
            RewardClaims.Drain(null, attempts: 1);
            CollectionAssert.AreEqual(new[] { 1, 5 }, seen, "offline: the pass stops after two failures in a row");
            Assert.AreEqual(3, RewardClaims.Pending().Count);
        }

        [Test]
        public void DeferredResponse_StillReportsTheGoldItPaid()
        {
            Assert.AreEqual(RewardClaims.Deferred, RewardClaims.DeferredWith(0));
            Assert.IsTrue(RewardClaims.IsDeferred(RewardClaims.DeferredWith(30)));
            Assert.AreEqual(30, RewardClaims.GoldOf(RewardClaims.DeferredWith(30)));
            Assert.AreEqual(5, RewardClaims.GoldOf(5));

            RewardClaims.Add(40, "capped");
            ClientGateway.ClaimLevelReward = (i, id) => Task.FromResult(RewardClaims.DeferredWith(30));   // backlog paid now
            var paid = new List<(int, int)>();
            RewardClaims.Drain((i, g) => paid.Add((i, g)), attempts: 1);
            CollectionAssert.AreEqual(new[] { (40, 30) }, paid, "the toast shows the total gained");
            Assert.AreEqual("2026-11-16", RewardClaims.Pending()[0].NotBefore);

            _now = _now.AddDays(1);
            ClientGateway.ClaimLevelReward = (i, id) => Task.FromResult(0);   // next-day replay: server paid it itself
            RewardClaims.Drain(null, attempts: 1);
            Assert.AreEqual(0, RewardClaims.Pending().Count, "replayed once, then dropped (no daily loop)");
        }

        [Test]
        public void QueueFormat_RoundTrips_AndReadsOlderSaves()
        {
            PlayerPrefs.SetString(RewardClaims.PendingKey, "12:e1;3:e2:2026-11-16");
            var old = RewardClaims.Pending();
            CollectionAssert.AreEqual(new[] { 3, 12 }, old.ConvertAll(c => c.Index));
            Assert.AreEqual("2026-11-16", old[0].NotBefore);
            Assert.AreEqual("", old[1].Player, "older saves have no owner: treated as the current player");

            RewardClaims.Add(5, "e5", "p1");
            ClientGateway.ClaimLevelReward = (i, id) => i == 5 ? throw new RewardPendingException() : Task.FromResult(RewardClaims.Deferred);
            _player = "p1";
            RewardClaims.Drain(null, attempts: 1);
            var c5 = RewardClaims.Pending().Find(c => c.Index == 5);
            Assert.AreEqual("p1", c5.Player);
            Assert.AreEqual(1, c5.Refusals);
            Assert.Greater(c5.FirstRefusedUtc, 0);
        }

        // ------------------------------------------------------------------ M1

        [Test]
        public void StreakBonus_WaitsForItsBoostersUnlock()
        {
            // a new player wins levels 1-3: the Hint bonus is ready at level 4, but Hint unlocks at 5
            Assert.IsTrue(BoardView.StreakBonusWaits(true, BoosterKind.Hint, levelIndex: 3, frontierIndex: 3));
            Assert.IsFalse(BoardView.StreakBonusWaits(true, BoosterKind.Hint, levelIndex: 4, frontierIndex: 4), "handed out at level 5");
            Assert.IsTrue(BoardView.StreakBonusWaits(true, BoosterKind.Undo, 6, 6));
            Assert.IsTrue(BoardView.StreakBonusWaits(true, BoosterKind.Unclamp, 9, 9));
            Assert.IsFalse(BoardView.StreakBonusWaits(true, BoosterKind.Unclamp, 2, 40), "a replay of an early level keeps unlocks");
            Assert.IsFalse(BoardView.StreakBonusWaits(false, BoosterKind.Hint, 0, 0), "no bonus, nothing waits");
            Assert.AreEqual("Free Hint saved for level 5", DailyCalendar.WinsLine(3, BoosterKind.Hint, 5));
            Assert.AreEqual("Free Hint ready", DailyCalendar.WinsLine(3, BoosterKind.Hint));
        }

        // ------------------------------------------------------------------ M2

        sealed class MemoryStore : ITutorialStore
        {
            public readonly HashSet<TutorialStep> DoneSteps = new HashSet<TutorialStep>();
            public bool Done(TutorialStep step) => DoneSteps.Contains(step);
            public void MarkDone(TutorialStep step) => DoneSteps.Add(step);
        }

        [Test]
        public void UnlockCard_ShowsOnce_EvenWhenNotUsed_AndReportsItsOutcomeOnce()
        {
            var events = new List<TutorialStepEvent>();
            var flow = new TutorialFlow(new MemoryStore(), (k, d) => events.Add((TutorialStepEvent)d));
            Assert.AreEqual(TutorialStep.UnlockHint, flow.Peek(5, 5));
            Assert.AreEqual(TutorialStep.UnlockHint, flow.Peek(5, 5), "Peek has no side effects");
            Assert.AreEqual(TutorialStep.UnlockHint, flow.Begin(5, 5));
            Assert.IsTrue(flow.Spotlights(BoosterKind.Hint));
            flow.End();   // won without using the Hint
            Assert.AreEqual(TutorialStep.None, flow.Begin(5, 5), "a restart of level 5 doesn't show it again");
            Assert.AreEqual(TutorialStep.None, flow.Begin(6, 6), "nor the next levels in its window");
            Assert.AreEqual(0, events.Count, "shown but not acted on: no outcome reported");

            Assert.AreEqual(TutorialStep.UnlockUndo, flow.Begin(8, 8));
            flow.OnBooster(BoosterKind.Undo);
            flow.OnBooster(BoosterKind.Undo);
            Assert.AreEqual(1, events.Count);
            Assert.IsTrue(events[0].completed);
            Assert.AreEqual((int)TutorialStep.UnlockUndo, events[0].step);
        }

        // ------------------------------------------------------------------ M3 / L6 (+ review n, 1)

        [Test]
        public void ContinueKey_SurvivesTheSheet_AndARetryReplaysIt()
        {
            var intent = new SpendIntent();
            var keys = new List<string>();
            int shop = 0;
            var first = new GoldSpend(intent);
            var lost = first.Run(k => { keys.Add(k); return Task.FromResult(new SpendResult(SpendError.Offline)); }, () => shop++, 100, 60).Result;
            Assert.AreEqual(SpendError.Offline, lost.Error);
            Assert.IsTrue(intent.Unresolved, "the server may have charged: the key is kept");

            // the sheet is gone (Retry with no lives, Home card, ...); the reopened card has a new GoldSpend
            var reopened = new GoldSpend(intent);
            Assert.IsTrue(reopened.Unresolved, "the reopened row offers the retry, not a new price");
            var ok = reopened.Run(k => { keys.Add(k); return Task.FromResult(new SpendResult(SpendError.None, 5)); }, () => shop++, gold: 10, price: 60).Result;
            Assert.IsTrue(ok.Ok);
            Assert.AreEqual(0, shop, "an unresolved key skips the balance check (the gold may already be gone)");
            Assert.AreEqual(keys[0], keys[1], "same idempotency key: the server replays, never charges twice");
            Assert.IsFalse(intent.Unresolved);

            var next = new GoldSpend(intent).Run(k => { keys.Add(k); return Task.FromResult(new SpendResult(SpendError.None, 5)); }, null, 200, 90).Result;
            Assert.IsTrue(next.Ok);
            Assert.AreNotEqual(keys[1], keys[2], "the next continue is a new intent");
            Assert.AreEqual(90, ShopCatalog.ContinuePrice(1), "and it is priced as the server escalates it");
        }

        [Test]
        public void ContinueMoves_OnlyFromTheServerOrAReplay()
        {
            Assert.AreEqual(5, BoardView.ContinueMovesFor(new SpendResult(SpendError.None, 5)));
            Assert.AreEqual(5, BoardView.ContinueMovesFor(new SpendResult(SpendError.None, 0, replayed: true)));
            Assert.AreEqual(0, BoardView.ContinueMovesFor(new SpendResult(SpendError.None, 0)), "a fresh 0-move answer grants nothing");
        }

        // ------------------------------------------------------------------ M4

        sealed class HangingPort : IShopPort
        {
            public readonly TaskCompletionSource<SpendResult> Pending = new TaskCompletionSource<SpendResult>();
            public int Buys;
            public long Gold => 500;
            public bool RemoveAds => false;
            public string Price(string id) => null;
            public bool StarterOffered => false;
            public bool CanPresentStarter(DateTimeOffset now) => false;
            public void MarkStarterPresented() { }
            public event Action Changed { add { } remove { } }
            public Task<PurchaseOutcome> Purchase(string id) => Task.FromResult(PurchaseOutcome.Failed);
            public Task<PurchaseOutcome> Restore() => Task.FromResult(PurchaseOutcome.NothingToRestore);
            public Task<SpendResult> SpendContinue(string s, string k) => Pending.Task;
            public Task<SpendResult> BuyBooster(string kind, string key) { Buys++; return Pending.Task; }
        }

        static Button Labelled(GameObject root, string label)
        {
            foreach (var b in root.GetComponentsInChildren<Button>(true))
            {
                var t = b.GetComponentInChildren<Text>(true);
                if (t != null && t.text == label) return b;
            }
            return null;
        }

        [Test]
        public void BoosterSheet_CannotBeClosed_WhileAPurchaseIsInFlight()
        {
            var port = new HangingPort();
            ShopGateway.Fake = port;
            var root = Root();
            int closes = 0;
            var sheet = BoosterSheet.Build(root.transform, Kit(), BoosterKind.Hint, () => closes++, () => { });
            var buy = Labelled(sheet.gameObject, $"1 Hint for {ShopCatalog.BoosterGold} gold");
            Assert.IsNotNull(buy);
            var scrim = sheet.Scrim.GetComponent<Button>();
            scrim.onClick.Invoke();
            Assert.AreEqual(1, closes, "idle: the scrim closes the sheet");

            buy.onClick.Invoke();   // the purchase is now in flight (the fake never answers)
            Assert.AreEqual(1, port.Buys);
            scrim.onClick.Invoke();
            Assert.AreEqual(1, closes, "mid-purchase: the scrim is inert");
            foreach (var b in sheet.GetComponentsInChildren<Button>(true))
                if (b != buy && b != scrim) b.onClick.Invoke();   // the X
            Assert.AreEqual(1, closes, "mid-purchase: the X is inert");
        }

        // ------------------------------------------------------------------ L2

        [Test]
        public void EmptyRestore_SaysSo()
        {
            var model = new ShopModel(() => new HangingPort());
            Assert.AreEqual(PurchaseOutcome.NothingToRestore, model.Restore().Result);
            Assert.AreEqual("No purchases to restore.", model.Message);
            Assert.AreNotEqual(ShopModel.Tone.Success, model.MessageTone);
        }
    }
}
