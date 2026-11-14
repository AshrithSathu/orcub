using System.Collections.Generic;
using NUnit.Framework;
using Orcub.Core;
using UnityEngine;

namespace Orcub.Tests
{
    public class PuzzleModelTests
    {
        static LevelDef Pair()
        {
            // Ring 0 owns a holder clamping ring 1 (ring 1 sits to the right of ring 0).
            var level = new LevelDef { Name = "pair" };
            level.Rings.Add(new RingDef(0f, 0f, 1.2f, 0));
            level.Rings.Add(new RingDef(3.4f, 0f, 1.2f, 1));
            level.Links.Add(new LinkDef(0, 1));
            return level;
        }

        [Test]
        public void Basic10_IsSolvable_AndClearsEveryRing()
        {
            Assert.IsTrue(PuzzleModel.IsSolvable(Levels.Basic10(), out var order), "Basic 10 should be solvable");
            Assert.AreEqual(10, order.Count);
        }

        [Test]
        public void Basic10_StartsWithEveryLinkActive_AndNoClampInAGap()
        {
            var m = new PuzzleModel(Levels.Basic10());
            foreach (var l in m.Links)
            {
                Assert.IsTrue(l.Active);
                Assert.IsFalse(m.ClampInGap(l), $"Link {l.Owner}->{l.Target} starts inside the target's gap");
            }
        }

        [Test]
        public void Basic10_WeldsNeverSitInTheirOwnersGap()
        {
            var m = new PuzzleModel(Levels.Basic10());
            foreach (var l in m.Links)
                Assert.Greater(Mathf.Abs(l.WeldOffset), PuzzleModel.GapHalfAngle, $"Weld of {l.Owner}->{l.Target} is inside the gap");
        }

        [Test]
        public void Basic10_OnlyTheFeetCanTurnAtTheStart()
        {
            var m = new PuzzleModel(Levels.Basic10());
            var rotatable = new List<int>();
            for (int i = 0; i < m.Rings.Count; i++)
                if (m.CanRotate(i)) rotatable.Add(i);
            CollectionAssert.AreEquivalent(new[] { 8, 9 }, rotatable);
        }

        [Test]
        public void Basic10_HoldersFitBetweenRings()
        {
            var m = new PuzzleModel(Levels.Basic10());
            foreach (var l in m.Links)
            {
                float stem = (m.ClampPoint(l) - m.WeldPoint(l)).magnitude;
                Assert.That(stem, Is.InRange(0.4f, 1.6f), $"Stem {l.Owner}->{l.Target} length {stem}");
            }
        }

        [Test]
        public void OwnerIsLocked_UntilItsClampIsReleased()
        {
            var m = new PuzzleModel(Pair());
            Assert.AreEqual(RotateOutcome.Locked, m.Rotate(0, 10f));
            Assert.IsFalse(m.IsFree(0));
            Assert.IsFalse(m.TryRemove(0));
        }

        [Test]
        public void RotatingTarget_ReleasesClampExactlyWhenGapCoversIt()
        {
            var m = new PuzzleModel(Pair());
            var target = m.Rings[1];
            var link = m.Links[0];
            Assert.AreEqual(180f, link.ClampAngle, 0.01f, "clamp faces the owner");

            int releasedEvents = 0;
            m.LinkReleased += _ => releasedEvents++;

            // Walk the gap toward the clamp in small steps; it must release once and only when it fits.
            float dist = Mathf.DeltaAngle(target.GapAngle, link.ClampAngle);
            float dir = Mathf.Sign(dist);
            float threshold = PuzzleModel.GapHalfAngle - link.ClampHalfAngle;
            for (int i = 0; i < 400 && link.Active; i++)
            {
                m.Rotate(1, dir * 1f);
                if (link.Active)
                    Assert.Greater(Mathf.Abs(Mathf.DeltaAngle(target.GapAngle, link.ClampAngle)), threshold - 1.01f);
            }

            Assert.IsFalse(link.Active);
            Assert.AreEqual(1, releasedEvents);
            Assert.IsTrue(m.IsFree(0));
            Assert.IsTrue(m.IsFree(1));
        }

        [Test]
        public void FastSpin_CannotSkipOverAClamp()
        {
            var m = new PuzzleModel(Pair());
            Assert.AreEqual(RotateOutcome.Rotated, m.Rotate(1, 359f));
            Assert.IsFalse(m.Links[0].Active, "one huge rotation must still sweep the gap past the clamp");
        }

        [Test]
        public void Removal_RequiresFreedom_AndSolvesTheBoard()
        {
            var m = new PuzzleModel(Pair());
            bool solved = false;
            m.Solved += () => solved = true;

            Assert.IsFalse(m.TryRemove(1));
            m.Rotate(1, 360f);
            Assert.IsTrue(m.TryRemove(1));
            Assert.IsFalse(m.TryRemove(1), "already removed");
            Assert.IsFalse(solved);
            Assert.IsTrue(m.TryRemove(0));
            Assert.IsTrue(solved);
            Assert.IsTrue(m.IsSolved);
        }

        [Test]
        public void Cycle_IsReportedUnsolvable()
        {
            var level = Pair();
            level.Links.Add(new LinkDef(1, 0));
            Assert.IsFalse(PuzzleModel.IsSolvable(level, out _));
        }

        [Test]
        public void WeldRotatesWithOwner()
        {
            var m = new PuzzleModel(Pair());
            m.Rotate(1, 360f); // free the owner
            var l = m.Links[0];
            Vector2 before = m.WeldPoint(l);
            m.Rotate(0, 90f);
            Vector2 after = m.WeldPoint(l);
            Vector2 expected = (Vector2)(Quaternion.Euler(0, 0, 90f) * (before - m.Rings[0].Center)) + m.Rings[0].Center;
            Assert.Less((after - expected).magnitude, 1e-3f);
        }
    }
}
