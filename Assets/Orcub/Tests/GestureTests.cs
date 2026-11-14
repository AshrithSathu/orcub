using NUnit.Framework;
using Orcub.Core;
using Orcub.Game;

namespace Orcub.Tests
{
    /// <summary>
    /// Board input accounting for the gear mechanics (BoardView routes every drag and tap spin through
    /// <see cref="GestureTracker"/>): wrong-way ratchet drags never charge a move, and the model's CompleteMove
    /// runs exactly once per counted gesture however many drag samples it had.
    /// </summary>
    public class GestureTests
    {
        // ring 0 owns a holder clamping ring 1; ring 1's gap starts at 0°, the clamp sits at 180° on it
        static LevelDef Pair(int ratchet = 0, int spring = 0, bool clay = false)
        {
            var d = new LevelDef();
            d.Rings.Add(new RingDef(0, 0, 1.2f, 0, 90));
            d.Rings.Add(new RingDef(3.8f, 0, 1.2f, 1, 0) { Ratchet = ratchet });
            d.Links.Add(new LinkDef(0, 1) { SpringMoves = spring, Clay = clay });
            return d;
        }

        static void Drag(GestureTracker g, float total, float sample = 3f)
        {
            int n = UnityEngine.Mathf.CeilToInt(UnityEngine.Mathf.Abs(total) / sample);
            for (int i = 0; i < n; i++) g.Turn(total / n);
        }

        [Test]
        public void WrongDirectionRatchetDragIsRefusedAndChargesNoMove()
        {
            var m = new PuzzleModel(Pair(ratchet: -1));   // clockwise only
            var g = new GestureTracker(m, 1);
            for (int i = 0; i < 40; i++) Assert.AreEqual(RotateOutcome.WrongDirection, g.Turn(+3f));
            Assert.AreEqual(0f, g.Rotated);
            Assert.AreEqual(120f, g.WrongTurn, 0.01f);
            Assert.IsFalse(g.Counts, "a refused drag must not count as a move");
            Assert.AreEqual(0, m.Moves);
            Assert.AreEqual(0f, m.Rings[1].GapAngle, "the ring never turns the wrong way (no silent reverse)");
            Assert.IsTrue(m.Links[0].Active);
        }

        [Test]
        public void WrongDirectionOnTheGearPartnerIsRefusedToo()
        {
            var d = Pair();
            d.Rings.Add(new RingDef(0, -3.8f, 1.2f, 2, 90) { Ratchet = +1, GearGroup = 1 });
            d.Rings[1] = new RingDef(3.8f, 0, 1.2f, 1, 0) { GearGroup = 1 };
            var m = new PuzzleModel(d);
            var g = new GestureTracker(m, 1);
            // turning ring 1 counter-clockwise turns its partner clockwise, against the partner's ratchet
            Assert.AreEqual(RotateOutcome.WrongDirection, g.Turn(+5f));
            Assert.IsFalse(g.Counts);
            Assert.AreEqual(0, m.Moves);
        }

        [Test]
        public void CountedDragCompletesTheMoveExactlyOnce()
        {
            var m = new PuzzleModel(Pair());
            var g = new GestureTracker(m, 1);
            Drag(g, -200f);                              // 67 drag samples, sweeps the gap past the clamp
            Assert.IsFalse(m.Links[0].Active);
            Assert.AreEqual(0, m.Moves, "drag samples never complete a move");
            Assert.IsTrue(g.Counts);
            g.Complete();
            g.Complete();
            Assert.AreEqual(1, m.Moves);
            Assert.IsTrue(g.Completed);
        }

        [Test]
        public void TinyDragWithoutProgressDoesNotCount()
        {
            var m = new PuzzleModel(Pair());
            var g = new GestureTracker(m, 1);
            g.Turn(-4f);
            Assert.IsFalse(g.Counts);
            Assert.AreEqual(0, m.Moves);
        }

        [Test]
        public void SpringTimerRunsOnGesturesNotDragSamples()
        {
            var m = new PuzzleModel(Pair(spring: 2));
            var release = new GestureTracker(m, 1);
            Drag(release, -200f);
            Assert.IsFalse(m.Links[0].Active);
            release.Complete();                          // release gesture is move zero
            var link = m.Links[0];
            Assert.AreEqual(2, link.SpringMoves - (m.Moves - link.ReleasedAt));

            var second = new GestureTracker(m, 0);       // the owner is free to turn now
            Drag(second, 90f);
            second.Complete();
            Assert.IsFalse(m.Links[0].Active, "one further gesture of many samples: still open");

            var third = new GestureTracker(m, 0);
            Drag(third, 90f);
            third.Complete();
            Assert.IsTrue(m.Links[0].Active, "re-closes at the end of the second further gesture");
            Assert.AreEqual(3, m.Moves);
        }

        [Test]
        public void TapSpinRespectsTheRatchetAndReleases()
        {
            var m = new PuzzleModel(Pair(ratchet: -1));
            float delta = BoardView.SmartSpinDelta(m, 1, -60f);
            Assert.Less(delta, 0f, "a clockwise-only ring is spun clockwise");
            Assert.AreEqual(RotateOutcome.Rotated, m.Rotate(1, delta));
            Assert.IsFalse(m.Links[0].Active);
        }

        [Test]
        public void TapSpinGivesAClayCoatBothPassesInOneGesture()
        {
            var m = new PuzzleModel(Pair(clay: true));
            var g = new GestureTracker(m, 1);
            g.Turn(BoardView.SmartSpinDelta(m, 1, -60f));
            Assert.IsFalse(m.Links[0].Active);
            g.Complete();
            Assert.AreEqual(1, m.Moves);
        }

        /// <summary>Chapter 2–10 boards (tutorials and mid-chapter levels) are completable through gestures.</summary>
        [TestCase(20)] [TestCase(26)] [TestCase(40)] [TestCase(47)] [TestCase(60)] [TestCase(66)]
        [TestCase(80)] [TestCase(88)] [TestCase(100)] [TestCase(107)] [TestCase(120)] [TestCase(126)]
        [TestCase(140)] [TestCase(146)] [TestCase(160)] [TestCase(185)]
        public void MechanicBoardsSolveGestureByGesture(int index)
        {
            var def = LevelLibrary.Get(index).ToDef();
            var rules = LevelRules.For(def, tutorial: false);
            var m = new PuzzleModel(def);
            var memo = new System.Collections.Generic.Dictionary<string, int>();
            int gestures = 0;
            while (!m.IsSolved && gestures < 60)
            {
                for (int i = 0; i < m.Rings.Count; i++) if (m.IsFree(i)) m.TryRemove(i);
                if (m.IsSolved) break;
                var (ring, angle) = AutoPlan.Next(def, m, memo);
                Assert.GreaterOrEqual(ring, 0, $"level {index + 1}: no solving gesture at move {m.Moves}");
                var g = new GestureTracker(m, ring);
                Drag(g, angle, 10f);
                Assert.IsTrue(g.Counts);
                g.Complete();
                gestures++;
                Assert.AreEqual(gestures, m.Moves, "exactly one CompleteMove per gesture");
            }
            Assert.IsTrue(m.IsSolved, $"level {index + 1} unsolved");
            Assert.LessOrEqual(gestures, rules.MoveLimit);
        }
    }
}
