using System;
using NUnit.Framework;
using Orcub.Core;
using UnityEngine;

namespace Orcub.Tests
{
    public class MechanicTests
    {
        static LevelDef Pair()
        {
            var d = new LevelDef();
            d.Rings.Add(new RingDef(0, 0, 1.2f, 0, 90));
            d.Rings.Add(new RingDef(3.8f, 0, 1.2f, 1, 0));
            d.Links.Add(new LinkDef(0, 1));
            return d;
        }
        static void Ring(LevelDef d, int i, Func<RingDef, RingDef> edit) => d.Rings[i] = edit(d.Rings[i]);
        static void Link(LevelDef d, Func<LinkDef, LinkDef> edit) => d.Links[0] = edit(d.Links[0]);
        static string State(PuzzleModel m) => JsonUtility.ToJson(m.Capture());

        [Test]
        public void RatchetRejectsWrongDirectionWithoutMutationAndUndoRestores()
        {
            var d = Pair(); Ring(d, 1, r => { r.Ratchet = -1; return r; });
            var m = new PuzzleModel(d); var before = m.Capture();
            Assert.AreEqual(RotateOutcome.WrongDirection, m.Rotate(1, 30));
            Assert.AreEqual(0, m.Rings[1].GapAngle);
            m.Rotate(1, -360); m.CompleteMove(); Assert.IsFalse(m.Links[0].Active);
            m.Restore(before); Assert.AreEqual(JsonUtility.ToJson(before), State(m));
            Assert.AreEqual(1, SolverMeasure.Search(d).MinimumMoves);
            Assert.IsFalse(float.IsInfinity(m.MinRotationToClear(1)));
        }
        [Test]
        public void GearPairTurnsOppositelyAndSharesLocks()
        {
            var d = Pair(); d.Rings.Add(new RingDef(0, -3.8f, 1.2f, 2, 90));
            Ring(d, 1, r => { r.GearGroup = 1; return r; });
            Ring(d, 2, r => { r.GearGroup = 1; return r; });
            var m = new PuzzleModel(d); var before = m.Capture();
            m.Rotate(1, 30); Assert.AreEqual(60, m.Rings[2].GapAngle);
            m.Restore(before); Assert.AreEqual(JsonUtility.ToJson(before), State(m));
            d.Links.Add(new LinkDef(2, 0)); m = new PuzzleModel(d);
            Assert.IsFalse(m.CanRotate(1)); Assert.AreEqual(RotateOutcome.Locked, m.Rotate(1, 360));
        }
        [Test]
        public void GearPairSolverCountsOneGestureForTwoTargets()
        {
            var d = Pair(); d.Rings.Add(new RingDef(0, -3.8f, 1.2f, 2, 180));
            d.Links.Add(new LinkDef(0, 2));
            Ring(d, 1, r => { r.GearGroup = 1; return r; });
            Ring(d, 2, r => { r.GearGroup = 1; return r; });
            Assert.AreEqual(1, SolverMeasure.Search(d).MinimumMoves);
        }
        [TestCase(false)] [TestCase(true)]
        public void ColourClampsMatchExplicitTintIncludingPaletteZero(bool match)
        {
            var d = Pair(); Link(d, l => { l.Tinted = true; l.Color = match ? 1 : 0; return l; });
            var m = new PuzzleModel(d); var before = m.Capture(); m.Rotate(1, 720);
            Assert.AreEqual(!match, m.Links[0].Active);
            m.Restore(before); Assert.AreEqual(JsonUtility.ToJson(before), State(m));
            Assert.AreEqual(match, PuzzleModel.IsSolvable(d, out _));
        }
        [Test]
        public void ClayNeedsTwoEntriesAndUndoRestoresFirstPass()
        {
            var d = Pair(); Link(d, l => { l.Clay = true; return l; });
            var m = new PuzzleModel(d); m.Rotate(1, 180);
            Assert.IsTrue(m.Links[0].Active); Assert.AreEqual(1, m.Links[0].Passes);
            var before = m.Capture(); m.Rotate(1, 1);
            Assert.AreEqual(1, m.Links[0].Passes, "drag samples in same gap are not extra passes");
            m.Rotate(1, 360); Assert.IsFalse(m.Links[0].Active);
            m.Restore(before); Assert.AreEqual(JsonUtility.ToJson(before), State(m));
            Assert.AreEqual(1, SolverMeasure.Search(d).MinimumMoves, "two turns can be one continuous drag");
        }
        [Test]
        public void KeyRemovalUnlocksPadlockAndUndoRestoresIt()
        {
            var d = Pair(); d.Rings.Add(new RingDef(-3.8f, 0, 1.2f, 2));
            Ring(d, 2, r => { r.KeyId = 7; return r; });
            Ring(d, 1, r => { r.PadlockId = 7; return r; });
            var m = new PuzzleModel(d); var before = m.Capture(); int events = 0;
            m.KeyCollected += _ => events++;
            Assert.IsFalse(m.CanRotate(1)); m.TryRemove(2);
            Assert.IsTrue(m.CanRotate(1)); Assert.AreEqual(1, events);
            m.Restore(before); Assert.IsTrue(m.IsPadlocked(1)); Assert.AreEqual(JsonUtility.ToJson(before), State(m));
            Assert.AreEqual(1, SolverMeasure.Search(d).MinimumMoves);
        }
        [TestCase(false)] [TestCase(true)]
        public void RustFreezesAtDeadlineUnlessOilWasCollectedAndUndoRestores(bool oil)
        {
            var d = Pair(); d.Rings.Add(new RingDef(-3.8f, 0, 1.2f, 2));
            Ring(d, 2, r => { r.OilId = 4; return r; });
            Ring(d, 1, r => { r.RustAfter = 2; r.RustOilId = 4; return r; });
            var m = new PuzzleModel(d); var before = m.Capture(); int freezes = 0;
            m.RingFrozen += _ => freezes++;
            m.CompleteMove(); Assert.IsFalse(m.Rings[1].Frozen);
            if (oil) m.TryRemove(2);
            m.CompleteMove(); Assert.AreEqual(!oil, m.Rings[1].Frozen); Assert.AreEqual(oil ? 0 : 1, freezes);
            if (!oil) { m.TryRemove(2); Assert.IsFalse(m.CanRotate(1), "late oil cannot thaw"); }
            m.Restore(before); Assert.AreEqual(JsonUtility.ToJson(before), State(m));
        }
        [Test]
        public void SpringDeadlineAndRemovalAndUndo()
        {
            var d = Pair(); Link(d, l => { l.SpringMoves = 2; return l; });
            var m = new PuzzleModel(d); int recloses = 0; m.ClampReclosed += _ => recloses++;
            m.Rotate(1, 360); m.CompleteMove(); var released = m.Capture();
            m.CompleteMove(); Assert.IsFalse(m.Links[0].Active);
            m.CompleteMove(); Assert.IsTrue(m.Links[0].Active); Assert.AreEqual(1, recloses);
            m.Restore(released); Assert.AreEqual(JsonUtility.ToJson(released), State(m));
            m.TryRemove(1); m.CompleteMove(); m.CompleteMove(); Assert.IsFalse(m.Links[0].Active);
            Assert.AreEqual(1, SolverMeasure.Search(d).MinimumMoves);
        }
        [Test]
        public void RustSearchFindsSafeOrderAndReportsUnsafeBranch()
        {
            var d = Pair(); d.Rings.Add(new RingDef(0, -3.8f, 1.2f, 2, 180));
            d.Links.Add(new LinkDef(0, 2));
            Ring(d, 1, r => { r.RustAfter = 1; return r; });
            var measure = SolverMeasure.Search(d);
            Assert.AreEqual(2, measure.MinimumMoves); Assert.Greater(measure.DeadEnds, 0);
        }
        [Test]
        public void LegacyJsonAbsentFieldsIsClassic()
        {
            const string json = "{\"id\":1,\"rings\":[{\"x\":0,\"y\":0,\"r\":1.2,\"color\":0,\"gap\":90},{\"x\":3.8,\"y\":0,\"r\":1.2,\"color\":1,\"gap\":0}],\"links\":[{\"owner\":0,\"target\":1}]}";
            var data = JsonUtility.FromJson<LevelData>(json);
            Assert.IsFalse(MechanicSolver.HasMechanics(data.ToDef()));
            Assert.AreEqual(1, SolverMeasure.Search(data.ToDef()).MinimumMoves);
            Assert.AreEqual(JsonUtility.ToJson(data), JsonUtility.ToJson(JsonUtility.FromJson<LevelData>(JsonUtility.ToJson(data))));
        }
        [Test]
        public void SagaSamplesAreSolvableWithMetadataAndSafeBudgets()
        {
            for (int chapter = 2; chapter <= 10; chapter++)
            foreach (int offset in new[] { 0, 9, 19 })
            {
                var d = SagaGenerator.Generate((chapter - 1) * 20 + offset, 0);
                Assert.IsTrue(PuzzleModel.IsSolvable(d.ToDef(), out _));
                Assert.AreEqual(offset == 19, d.is_boss);
                Assert.AreEqual(offset == 0, !string.IsNullOrEmpty(d.mechanic_intro));
                if (d.is_boss) Assert.That(d.rings.Length, Is.InRange(14, 18));
                Assert.GreaterOrEqual(LevelRules.For(d.ToDef()).MoveLimit, d.solver.MinimumMoves + 2);
                Assert.IsNotEmpty(d.mechanics);
            }
        }
    }
}
