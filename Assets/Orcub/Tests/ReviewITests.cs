using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Orcub.Core;
using Orcub.Game;
using UnityEngine;

namespace Orcub.Tests
{
    /// <summary>
    /// Review i (Game side): H1 win-gold queue (order, no lossy cap, cap retry on a later UTC day), M1 dead boards,
    /// M2 the Unclamp rescue link, M3 loss recorded only after a life is spent, M4 broken boards fall back.
    /// Claim fakes return completed tasks and Drain runs with attempts: 1, so nothing waits on the main thread.
    /// </summary>
    public sealed class ReviewITests
    {
        string _savedQueue;
        Func<int, string, Task<int>> _savedClaim;
        Func<string> _savedEvent;
        Func<DateTimeOffset> _savedClock;
        DateTimeOffset _now;

        [SetUp]
        public void SetUp()
        {
            _savedQueue = PlayerPrefs.HasKey(RewardClaims.PendingKey) ? PlayerPrefs.GetString(RewardClaims.PendingKey) : null;
            _savedClaim = ClientGateway.ClaimLevelReward;
            _savedEvent = ClientGateway.LastLevelEndEventId;
            _savedClock = RewardClaims.Clock;
            PlayerPrefs.DeleteKey(RewardClaims.PendingKey);
            _now = new DateTimeOffset(2026, 11, 15, 22, 0, 0, TimeSpan.Zero);
            RewardClaims.Clock = () => _now;
        }

        [TearDown]
        public void TearDown()
        {
            if (_savedQueue == null) PlayerPrefs.DeleteKey(RewardClaims.PendingKey); else PlayerPrefs.SetString(RewardClaims.PendingKey, _savedQueue);
            ClientGateway.ClaimLevelReward = _savedClaim;
            ClientGateway.LastLevelEndEventId = _savedEvent;
            RewardClaims.Clock = _savedClock;
        }

        // ------------------------------------------------------------------ H1

        [Test]
        public void Queue_KeepsLevelOrder_OnePerLevel_AndNeverDropsTheHead()
        {
            RewardClaims.Add(9, "e9");
            RewardClaims.Add(2, "e2");
            RewardClaims.Add(5, "e5");
            RewardClaims.Add(5, "e5-fresh");   // a newer win of the same level replaces its evidence
            var list = RewardClaims.Pending();
            CollectionAssert.AreEqual(new[] { 2, 5, 9 }, list.ConvertAll(c => c.Index));
            Assert.AreEqual("e5-fresh", list[1].EventId);

            for (int i = 10; i < 60; i++) RewardClaims.Add(i, "e" + i);   // far beyond the old cap of 20
            list = RewardClaims.Pending();
            Assert.AreEqual(53, list.Count);
            Assert.AreEqual(2, list[0].Index, "the oldest claim, the one blocking the sequence, is never evicted");
        }

        [Test]
        public void Drain_StopsAtTheFirstFailure_KeepingEveryLaterClaim()
        {
            RewardClaims.Add(1, "a");
            RewardClaims.Add(2, "b");
            RewardClaims.Add(3, "c");
            var calls = new List<int>();
            ClientGateway.ClaimLevelReward = (index, id) =>
            {
                calls.Add(index);
                if (index == 2) throw new InvalidOperationException("reward_sequence_pending");
                return Task.FromResult(5);
            };
            var granted = new List<int>();
            Assert.IsTrue(RewardClaims.Drain((i, g) => granted.Add(i), attempts: 1).IsCompleted);
            CollectionAssert.AreEqual(new[] { 1, 2 }, calls, "claims run in order and stop at the refusal");
            CollectionAssert.AreEqual(new[] { 1 }, granted);
            CollectionAssert.AreEqual(new[] { 2, 3 }, RewardClaims.Pending().ConvertAll(c => c.Index));

            ClientGateway.ClaimLevelReward = (index, id) => Task.FromResult(5);
            RewardClaims.Drain((i, g) => granted.Add(i), attempts: 1);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, granted, "a later drain pays the rest in order");
            Assert.AreEqual(0, RewardClaims.Pending().Count);
        }

        [Test]
        public void CappedClaim_StaysQueued_AndPaysOnALaterUtcDay()
        {
            RewardClaims.Add(20, "capped");
            RewardClaims.Add(21, "next");
            int calls = 0;
            bool capped = true;
            ClientGateway.ClaimLevelReward = (index, id) => { calls++; return Task.FromResult(capped ? RewardClaims.Deferred : 5); };
            var granted = new List<int>();

            RewardClaims.Drain((i, g) => granted.Add(i), attempts: 1);
            Assert.AreEqual(1, calls);
            var head = RewardClaims.Pending()[0];
            Assert.AreEqual(20, head.Index, "a capped claim is kept, never dropped");
            Assert.AreEqual("2026-11-16", head.NotBefore);
            Assert.AreEqual(2, RewardClaims.Pending().Count);

            RewardClaims.Drain((i, g) => granted.Add(i), attempts: 1);   // still today: not even tried
            Assert.AreEqual(1, calls);

            _now = _now.AddHours(3);   // past UTC midnight
            capped = false;
            RewardClaims.Drain((i, g) => granted.Add(i), attempts: 1);
            CollectionAssert.AreEqual(new[] { 20, 21 }, granted);
            Assert.AreEqual(0, RewardClaims.Pending().Count);
        }

        [Test]
        public void ZeroGrant_IsFinal_AndDropsOnlyThatClaim()
        {
            RewardClaims.Add(3, "replay");
            RewardClaims.Add(4, "first");
            ClientGateway.ClaimLevelReward = (index, id) => Task.FromResult(index == 3 ? 0 : 5);
            var granted = new List<int>();
            RewardClaims.Drain((i, g) => granted.Add(i), attempts: 1);
            CollectionAssert.AreEqual(new[] { 4 }, granted);
            Assert.AreEqual(0, RewardClaims.Pending().Count);
        }

        [Test]
        public void OldQueueFormat_StillParses()
        {
            PlayerPrefs.SetString(RewardClaims.PendingKey, "39:evt-1;12:evt-2;12:evt-dup");
            var list = RewardClaims.Pending();
            CollectionAssert.AreEqual(new[] { 12, 39 }, list.ConvertAll(c => c.Index));
            Assert.AreEqual("", list[0].NotBefore);
        }

        // ------------------------------------------------------------------ M1

        [Test]
        public void DeadBoard_IsADeadEnd_EvenOnTheLastMove_AndNeverAContinue()
        {
            Assert.AreEqual(FailFlow.Card.DeadEnd, FailFlow.Decide(0, dead: true), "the last move rusted it solid");
            Assert.AreEqual(FailFlow.Card.DeadEnd, FailFlow.Decide(4, dead: true));
            Assert.AreEqual(FailFlow.Card.OutOfMoves, FailFlow.Decide(0, dead: false));
            Assert.AreEqual(FailFlow.Card.None, FailFlow.Decide(2, dead: false));
        }

        // ------------------------------------------------------------------ M2

        [Test]
        public void Unclamp_Rescue_OpensTheFrozenRingsOwnClamp()
        {
            var def = new LevelDef { Name = "rescue" };
            def.Rings.Add(new RingDef(0f, 0f, 1.2f, 0));
            def.Rings.Add(new RingDef(3f, 0f, 1.2f, 1));
            def.Rings.Add(new RingDef(-3f, 0f, 1.2f, 2));
            def.Links.Add(new LinkDef(0, 1));   // link 0: ring 0 clamps ring 1
            def.Links.Add(new LinkDef(2, 0));   // link 1: ring 2 clamps ring 0
            var m = new PuzzleModel(def);
            m.Rings[0].Frozen = true;
            Assert.AreEqual(1, FailFlow.RescueLink(m), "the frozen ring is freed by opening its own clamp");
            m.Rings[0].Frozen = false;
            Assert.GreaterOrEqual(FailFlow.RescueLink(m), 0, "without rust: the booster's usual pick");
        }

        // ------------------------------------------------------------------ M3

        [Test]
        public void Retry_RecordsTheLossOnlyAfterALifeIsSpent()
        {
            var log = new List<string>();
            bool spent = FailFlow.SpendThen(() => false, () => log.Add("loss"), () => log.Add("restart"), () => log.Add("out-of-lives"));
            Assert.IsFalse(spent);
            CollectionAssert.AreEqual(new[] { "out-of-lives" }, log, "no life: the attempt stays open, nothing recorded");

            log.Clear();
            spent = FailFlow.SpendThen(() => true, () => log.Add("loss"), () => log.Add("restart"), () => log.Add("out-of-lives"));
            Assert.IsTrue(spent);
            CollectionAssert.AreEqual(new[] { "loss", "restart" }, log);
        }

        // ------------------------------------------------------------------ M4

        [Test]
        public void BrokenBoard_FailsTheGuard_SoTheStandInStarts()
        {
            var broken = new LevelDef { Name = "broken" };
            broken.Rings.Add(new RingDef(0f, 0f, 1.2f, 0) { GearGroup = 1 });   // a gear group of one ring: invalid
            broken.Rings.Add(new RingDef(3f, 0f, 1.2f, 1));
            broken.Links.Add(new LinkDef(0, 1));
            Assert.IsFalse(FailFlow.TryRules(broken, 40, 0, out _));
            var selfLink = new LevelDef { Name = "self" };
            selfLink.Rings.Add(new RingDef(0f, 0f, 1.2f, 0));
            selfLink.Links.Add(new LinkDef(0, 0));
            Assert.IsFalse(FailFlow.TryRules(selfLink, 40, 0, out _));
            Assert.IsTrue(FailFlow.TryRules(Levels.Basic10(), 40, 0, out var ok));
            Assert.Greater(ok.MoveLimit, 0);
        }

        // ------------------------------------------------------------------ L9

        [Test]
        public void CuratedBoards_NeverNeedMoreThanSixClampShapes()
        {
            for (int i = 0; i < LevelLibrary.CuratedCount; i++)
            {
                var data = LevelLibrary.Get(i);
                var colours = new HashSet<int>();
                foreach (var l in data.links)
                    if (l.tinted) { colours.Add(Mathf.Abs(l.color)); colours.Add(Mathf.Abs(data.rings[l.target].color)); }
                Assert.LessOrEqual(colours.Count, OrcubArt.Shapes.Length, $"level {i + 1}: more stamped colours than shapes");
            }
        }
    }
}
