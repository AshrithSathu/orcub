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
    /// Review e (ArtSource/reviews/opus_review_2026-11-15e_mechanics.md) fixes: sequential gestures (H1), ratchet
    /// flicks (M1), clay smart spins (M2), solver cache (M4), dead-end detection (M5), board-unique colour stamps
    /// (L3), booster spring timing (L4), and the SDK-free Game wiring (lives clock, booster unlocks, reward claims).
    /// </summary>
    public class ReviewETests
    {
        // ring 0 owns a holder clamping ring 1; ring 1's gap starts at <gap>, the clamp sits at 180° on it
        static LevelDef Pair(int ratchet = 0, int spring = 0, bool clay = false, float gap = 0f)
        {
            var d = new LevelDef();
            d.Rings.Add(new RingDef(0, 0, 1.2f, 0, 90));
            d.Rings.Add(new RingDef(3.8f, 0, 1.2f, 1, gap) { Ratchet = ratchet });
            d.Links.Add(new LinkDef(0, 1) { SpringMoves = spring, Clay = clay });
            return d;
        }

        /// <summary>Two independent pairs: 0 clamps 1, and 2 clamps 3 (3 turns clockwise only).</summary>
        static LevelDef TwoPairs()
        {
            var d = Pair();
            d.Rings.Add(new RingDef(0, -6f, 1.2f, 2, 90));
            d.Rings.Add(new RingDef(3.8f, -6f, 1.2f, 3, 0) { Ratchet = -1 });
            d.Links.Add(new LinkDef(2, 3));
            return d;
        }

        static SpinSet.TurnFn Turn(PuzzleModel m) => (id, delta, speed) => m.Rotate(id, delta);

        // ------------------------------------------------------------------ H1

        [Test]
        public void RunningTapSpinIsFinishedAndCompletedBeforeTheNextGesture()
        {
            var m = new PuzzleModel(TwoPairs());
            var spins = new SpinSet();
            float delta = BoardView.SmartSpinDelta(m, 1, -60f);
            spins.Start(1, delta, 420f, new GestureTracker(m, 1));
            spins.Step(0.02f, 420f, Turn(m));                       // part-way through the spin
            Assert.IsTrue(spins.IsSpinning(1));
            Assert.AreEqual(0, m.Moves, "a tap's move completes only when its spin stops");

            Assert.IsTrue(spins.FinishAll(Turn(m)));               // what BoardView does on any new press / booster
            Assert.IsFalse(spins.Busy);
            Assert.AreEqual(1, m.Moves, "the tap's move completed exactly once");
            Assert.IsFalse(m.Links[0].Active, "the spin turned the rest of the way and released its clamp");
            var snap = m.Capture();                                 // the next gesture's Undo snapshot
            Assert.AreEqual(1, snap.Moves, "the snapshot is taken after the tap's move, never mid-spin");

            var drag = new GestureTracker(m, 3);
            Assert.AreEqual(RotateOutcome.WrongDirection, drag.Turn(+5f));
            Assert.IsFalse(drag.Counts, "a refused drag on another ring is never charged");
            Assert.IsFalse(spins.FinishAll(Turn(m)));
            Assert.AreEqual(1, m.Moves);
        }

        [Test]
        public void GestureNeverCountsProgressMadeOnAnotherRing()
        {
            var m = new PuzzleModel(TwoPairs());
            var drag = new GestureTracker(m, 3);
            m.Rotate(1, -200f);                                     // another ring's spin releases its clamp meanwhile
            Assert.IsFalse(m.Links[0].Active);
            drag.Turn(+3f);                                         // wrong way: refused
            Assert.IsFalse(drag.Counts, "B's refused drag must not be charged for A's release");
        }

        [Test]
        public void EndingASpinTwiceCompletesItsMoveOnce()
        {
            var m = new PuzzleModel(Pair());
            var spins = new SpinSet();
            spins.Start(1, -40f, 420f, new GestureTracker(m, 1));
            Assert.IsTrue(spins.End(1));
            Assert.IsFalse(spins.End(1));
            spins.FinishAll(Turn(m));
            Assert.AreEqual(1, m.Moves);
        }

        // ------------------------------------------------------------------ M1

        [Test]
        public void WrongWayFlickOnARatchetIsNeverATapSpin()
        {
            var m = new PuzzleModel(Pair(ratchet: -1));
            var g = new GestureTracker(m, 1);
            Assert.AreEqual(RotateOutcome.WrongDirection, g.Turn(+8f));   // quick 8° flick the forbidden way
            g.ClearWrongTurn();                                          // the shake cue was shown
            Assert.IsTrue(g.Refused, "the refusal is remembered for the whole press");
            Assert.AreEqual(0f, g.Rotated);
            Assert.IsFalse(BoardView.IsTapSpin(true, g.Rotated, g.Refused), "no reversed paid spin after a refusal");
            Assert.IsTrue(BoardView.IsTapSpin(true, 0f, false), "a clean short press is still a tap");
            Assert.IsFalse(BoardView.IsTapSpin(false, 0f, false));
            Assert.AreEqual(0, m.Moves);
        }

        // ------------------------------------------------------------------ M2

        [TestCase(-1, 0f)] [TestCase(-1, 100f)] [TestCase(-1, -60f)] [TestCase(1, 0f)] [TestCase(1, 100f)] [TestCase(1, -60f)]
        public void RatchetForcedClayTapSpinGivesBothPasses(int ratchet, float gap)
        {
            var m = new PuzzleModel(Pair(ratchet: ratchet, clay: true, gap: gap));
            float delta = BoardView.SmartSpinDelta(m, 1, -60f);
            Assert.IsTrue(m.DirectionAllowed(1, delta));
            Assert.AreEqual(RotateOutcome.Rotated, m.Rotate(1, delta));
            Assert.IsFalse(m.Links[0].Active, $"gap {gap}, ratchet {ratchet}: one tap must crack AND open a fresh coat");
            Assert.LessOrEqual(Mathf.Abs(delta), 1080f);
        }

        [Test]
        public void ReclosedClayClampInTheGapGetsBothPasses()
        {
            var m = new PuzzleModel(Pair(spring: 1, clay: true));
            Assert.AreEqual(RotateOutcome.Rotated, m.Rotate(1, BoardView.SmartSpinDelta(m, 1, -60f)));
            Assert.IsFalse(m.Links[0].Active);
            m.CompleteMove();                                       // the release gesture (move zero)
            m.CompleteMove();                                       // one further gesture: the spring re-closes
            var l = m.Links[0];
            Assert.IsTrue(l.Active && l.InGap && l.Passes == 0, "re-closed in the gap with a fresh coat");
            float delta = BoardView.SmartSpinDelta(m, 1, -60f);
            Assert.AreEqual(720f, Mathf.Abs(delta), 0.01f);
            m.Rotate(1, delta);
            Assert.IsFalse(l.Active, "two re-entries in one tap");
        }

        // ------------------------------------------------------------------ M4

        [Test]
        public void SolverCacheMatchesTheExactSearchAndKeysOnGeometry()
        {
            var def = LevelLibrary.Get(39).ToDef();   // a curated boss
            Assert.IsTrue(MechanicSolver.Solve(def, out var exact, out _));
            Assert.IsTrue(MechanicSolver.SolveCached(def, out var cached));
            Assert.AreEqual(exact.MinimumMoves, cached.MinimumMoves);
            Assert.IsTrue(MechanicSolver.IsCached(LevelLibrary.Get(39).ToDef()), "same board, new def instance");
            var moved = Pair(gap: 10f);
            Assert.AreNotEqual(MechanicSolver.Fingerprint(Pair()), MechanicSolver.Fingerprint(moved));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 5; i++) LevelRules.For(def, extraMoves: i);
            Assert.Less(watch.ElapsedMilliseconds, 50, "level start rules are cached lookups");
        }

        [Test]
        public void AdaptiveSelectsFromThePreparedArray()
        {
            string prefix = "orcub.test.reviewe." + Guid.NewGuid().ToString("N");
            var a = new Adaptive(new PlayerSkill(prefix), new FailureStreak(prefix + ".fail."), new AttemptLog(persist: false), random: new System.Random(1));
            var choices = LevelLibrary.GetVariants(25);
            var plan = a.BeginAttempt(25, choices);
            Assert.That(plan.Variant, Is.InRange(0, choices.Length - 1));
            a.CancelAttempt();
            Assert.AreEqual(0, AttemptPlan.Neutral().Variant);
            Assert.AreEqual(0, AttemptPlan.Neutral().ExtraMoves);
            Assert.IsFalse(AttemptPlan.Neutral().FreeHintGlow);
        }

        // ------------------------------------------------------------------ M5

        [Test]
        public void RustFrozenRingWithAClampOnItIsADeadEnd()
        {
            var d = Pair();
            d.Rings[1] = new RingDef(3.8f, 0, 1.2f, 1, 0) { RustAfter = 1 };
            var m = new PuzzleModel(d);
            Assert.IsFalse(Deadlock.IsDead(m));
            m.CompleteMove();                                       // the deadline passes without oil
            Assert.IsTrue(m.Rings[1].Frozen);
            Assert.IsTrue(Deadlock.IsDead(m), "a frozen ring can never release its own clamps");
        }

        [Test]
        public void RustOnAFreeableRingIsNotADeadEnd()
        {
            var d = Pair();
            d.Rings[0] = new RingDef(0, 0, 1.2f, 0, 90) { RustAfter = 1 };   // the owner rusts; its target can still turn
            var m = new PuzzleModel(d);
            m.CompleteMove();
            Assert.IsTrue(m.Rings[0].Frozen);
            Assert.IsFalse(Deadlock.IsDead(m));
            m.Rotate(1, -200f);
            Assert.IsFalse(Deadlock.IsDead(m), "both rings free now");
        }

        [Test]
        public void NoReleasableClampAnywhereIsADeadEnd()
        {
            var d = Pair();
            d.Links[0] = new LinkDef(0, 1) { Tinted = true, Color = 4 };   // a colour clamp that never opens for ring 1
            Assert.IsTrue(Deadlock.IsDead(new PuzzleModel(d)));
            d.Links[0] = new LinkDef(0, 1) { Tinted = true, Color = 1 };
            Assert.IsFalse(Deadlock.IsDead(new PuzzleModel(d)));
        }

        [TestCase(20)] [TestCase(100)] [TestCase(107)] [TestCase(126)] [TestCase(185)]
        public void ShippedBoardsAreNeverDeadAtTheStart(int index)
        {
            Assert.IsFalse(Deadlock.IsDead(new PuzzleModel(LevelLibrary.Get(index).ToDef())));
        }

        // ------------------------------------------------------------------ L3, L4

        [Test]
        public void ColourClampStampsAreUniquePerBoard()
        {
            var d = new LevelDef();
            int[] colours = { 0, 6, 1, 7, 3, 9 };   // the palette pairs that share a default shape
            for (int i = 0; i < colours.Length; i++) d.Rings.Add(new RingDef(i * 3.8f, 0, 1.2f, colours[i], 0));
            for (int i = 0; i + 1 < colours.Length; i++) d.Links.Add(new LinkDef(i, i + 1) { Tinted = true, Color = colours[i + 1] });
            d.Links.Add(new LinkDef(1, 0) { Tinted = true, Color = 0 });
            var m = new PuzzleModel(d);
            var seen = new HashSet<int>();
            foreach (int c in colours) Assert.IsTrue(seen.Add(OrcubArt.ShapeFor(c, m)), $"colour {c} shares a stamp");
            Assert.AreEqual(OrcubArt.ShapeFor(0), OrcubArt.ShapeFor(0, m), "coral keeps its approved triangle");
            Assert.AreEqual(OrcubArt.ShapeFor(1), OrcubArt.ShapeFor(1, m));
        }

        [Test]
        public void UnclampBoosterSpringGivesExactlyKFurtherGestures()
        {
            var m = new PuzzleModel(Pair(spring: 2));
            Assert.IsTrue(m.ReleaseLink(0));
            m.Links[0].ReleasedAt = m.Moves;   // what BoardView does for a booster release (outside a gesture)
            m.CompleteMove();
            Assert.IsFalse(m.Links[0].Active, "first of K gestures");
            m.CompleteMove();
            Assert.IsTrue(m.Links[0].Active, "re-closes after K = 2 gestures, not K + 1");
        }

        // ------------------------------------------------------------------ wiring (SDK-free)

        [Test]
        public void LivesFullAtCountsEveryMissingLife()
        {
            var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
            Assert.IsNull(LivesClock.FullAt(now, Lives.Max, 0));
            Assert.AreEqual(now.AddSeconds(600 + 1 * Lives.RegenSeconds), LivesClock.FullAt(now, Lives.Max - 2, 600));
            Assert.AreEqual(now.AddSeconds(Lives.Max * Lives.RegenSeconds), LivesClock.FullAt(now, 0, 0));
        }

        [Test]
        public void BoosterUnlocksAreDueOnceTheirLevelIsReached()
        {
            CollectionAssert.IsEmpty(BoosterUnlocks.Due(4, _ => false));
            CollectionAssert.AreEqual(new[] { "hint" }, BoosterUnlocks.Due(5, _ => false));
            CollectionAssert.AreEqual(new[] { "undo", "unclamp" }, BoosterUnlocks.Due(12, k => k == "hint"));
            CollectionAssert.IsEmpty(BoosterUnlocks.Due(40, _ => true));
        }

        [Test]
        public void RewardClaimsRetryWithTheSameEventId()
        {
            string saved = PlayerPrefs.GetString(RewardClaims.PendingKey, null);
            var lastId = ClientGateway.LastLevelEndEventId; var claim = ClientGateway.ClaimLevelReward;
            try
            {
                PlayerPrefs.DeleteKey(RewardClaims.PendingKey);
                var seen = new List<string>();
                bool fail = true;
                ClientGateway.LastLevelEndEventId = () => "evt-1";
                ClientGateway.ClaimLevelReward = (index, id) =>
                {
                    seen.Add(id);
                    if (fail) throw new InvalidOperationException("reward_not_ready");
                    return Task.FromResult(index % 20 == 19 ? 50 : 5);
                };
                int granted = 0;
                // single-attempt drains with synchronous fakes complete without ever yielding (no Task.Delay)
                PlayerPrefs.SetString(RewardClaims.PendingKey, "39:evt-1");
                var first = RewardClaims.Drain((i, g) => granted = g, attempts: 1);
                Assert.IsTrue(first.IsCompleted, "never blocks the main thread");
                Assert.AreEqual(1, RewardClaims.Pending().Count, "a failed claim stays queued");
                fail = false;
                Assert.IsTrue(RewardClaims.Drain((i, g) => granted = g, attempts: 1).IsCompleted);
                CollectionAssert.AreEqual(new[] { "evt-1", "evt-1" }, seen, "retried with the same level_end id");
                Assert.AreEqual(50, granted, "boss first clear");
                Assert.AreEqual(0, RewardClaims.Pending().Count);
            }
            finally
            {
                ClientGateway.LastLevelEndEventId = lastId; ClientGateway.ClaimLevelReward = claim;
                if (saved == null) PlayerPrefs.DeleteKey(RewardClaims.PendingKey); else PlayerPrefs.SetString(RewardClaims.PendingKey, saved);
            }
        }

        [Test]
        public void AppOpenHookNeverThrowsIntoTheGame()
        {
            var hook = ClientGateway.AppOpened;
            try
            {
                DateTimeOffset? seen = null;
                ClientGateway.AppOpened = at => { seen = at; throw new InvalidOperationException("daily not ready"); };
                var at0 = DateTimeOffset.UtcNow;
                Assert.DoesNotThrow(() => ClientGateway.OpenApp(at0));
                Assert.AreEqual(at0, seen);
            }
            finally { ClientGateway.AppOpened = hook; }
        }
    }
}
