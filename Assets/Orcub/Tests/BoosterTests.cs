using System.Collections.Generic;
using NUnit.Framework;
using Orcub.Core;
using UnityEngine;

namespace Orcub.Tests
{
    public class BoosterTests
    {
        readonly Dictionary<string, int?> savedPrefs = new Dictionary<string, int?>();

        void ResetKey(string key)
        {
            if (!savedPrefs.ContainsKey(key))
                savedPrefs.Add(key, PlayerPrefs.HasKey(key) ? PlayerPrefs.GetInt(key) : (int?)null);
            PlayerPrefs.DeleteKey(key);
        }

        [TearDown]
        public void RestorePrefs()
        {
            foreach (var entry in savedPrefs)
            {
                if (entry.Value.HasValue) PlayerPrefs.SetInt(entry.Key, entry.Value.Value);
                else PlayerPrefs.DeleteKey(entry.Key);
            }
            savedPrefs.Clear();
            PlayerPrefs.Save();
        }

        static PuzzleModel Model(int ringCount, params LinkDef[] links)
        {
            var def = new LevelDef { Name = "booster test" };
            for (int i = 0; i < ringCount; i++)
                def.Rings.Add(new RingDef(i * 3.4f, 0f, 1.2f, i));
            def.Links.AddRange(links);
            return new PuzzleModel(def);
        }

        static void AssertSnapshot(ModelSnapshot expected, PuzzleModel m)
        {
            var actual = m.Capture();
            CollectionAssert.AreEqual(expected.Gaps, actual.Gaps);
            CollectionAssert.AreEqual(expected.LinkActive, actual.LinkActive);
            CollectionAssert.AreEqual(expected.Removed, actual.Removed);
        }

        [Test]
        public void Snapshot_RoundTripsRotationsReleasesAndRemovals_WithoutEvents()
        {
            var m = Model(2, new LinkDef(0, 1));
            var initial = m.Capture();
            m.Rotate(1, 17f);
            Assert.IsTrue(m.ReleaseLink(0));
            m.Rotate(0, -43f);
            Assert.IsTrue(m.TryRemove(1));
            var changed = m.Capture();
            Assert.AreNotEqual(initial.Gaps[1], changed.Gaps[1]);
            Assert.IsFalse(changed.LinkActive[0]);
            Assert.IsTrue(changed.Removed[1]);

            int events = 0;
            m.LinkReleased += _ => events++;
            m.RingRemoved += _ => events++;
            m.Solved += () => events++;
            m.Restore(initial);
            AssertSnapshot(initial, m);
            Assert.IsFalse(m.CanRotate(0));
            m.Restore(changed);
            AssertSnapshot(changed, m);
            Assert.IsTrue(m.CanRotate(0));
            Assert.AreEqual(0, events);
            m.Rotate(0, 20f);
            Assert.AreEqual(PuzzleModel.Normalize(initial.Gaps[0] - 43f), changed.Gaps[0], 0.001f,
                "later rotations must not mutate the snapshot");
            m.Restore(changed);
            AssertSnapshot(changed, m);
        }

        [Test]
        public void ReleaseLink_FiresOnce_AndUnlocksOwner()
        {
            var m = Model(2, new LinkDef(0, 1));
            int events = 0;
            m.LinkReleased += link => { Assert.AreSame(m.Links[0], link); events++; };
            Assert.IsFalse(m.CanRotate(0));
            Assert.IsTrue(m.ReleaseLink(0));
            Assert.IsFalse(m.ReleaseLink(0));
            m.Rotate(1, 360f);
            Assert.AreEqual(1, events);
            Assert.IsTrue(m.CanRotate(0));
        }

        [Test]
        public void Hints_SolveEveryLibraryLevel_AndEachRotationReleasesAClamp()
        {
            Assert.Greater(LevelLibrary.Count, 0);
            for (int level = 0; level < LevelLibrary.Count; level++)
            {
                var m = new PuzzleModel(LevelLibrary.Get(level).ToDef());
                int limit = m.Rings.Count + m.Links.Count;
                for (int step = 0; !m.IsSolved && step < limit; step++)
                {
                    Assert.IsTrue(Hints.TryFind(m, out var hint), $"level {level}, step {step}");
                    if (hint.PullOut)
                    {
                        Assert.IsTrue(m.IsFree(hint.Ring));
                        Assert.IsTrue(m.TryRemove(hint.Ring));
                    }
                    else
                    {
                        Assert.IsTrue(m.CanRotate(hint.Ring));
                        var released = new List<Link>();
                        m.Rotate(hint.Ring, hint.Delta, released);
                        Assert.Greater(released.Count, 0, $"level {level}, step {step}");
                    }
                }
                Assert.IsTrue(m.IsSolved, $"level {level}");
                Assert.IsFalse(Hints.TryFind(m, out _));
            }
        }

        [Test]
        public void Hints_PreferFreeRings_ThenSmallestSignedRotation()
        {
            var m = Model(5, new LinkDef(0, 1), new LinkDef(2, 3));
            Assert.IsTrue(Hints.TryFind(m, out var hint));
            Assert.IsTrue(hint.PullOut);
            Assert.AreEqual(4, hint.Ring);
            m.TryRemove(4);
            m.Rings[1].GapAngle = m.Links[0].ClampAngle - 80f;
            m.Rings[3].GapAngle = m.Links[1].ClampAngle + 50f;
            Assert.IsTrue(Hints.TryFind(m, out hint));
            Assert.IsFalse(hint.PullOut);
            Assert.AreEqual(3, hint.Ring);
            float expected = -(50f - (PuzzleModel.GapHalfAngle - m.Links[1].ClampHalfAngle) + 3f);
            Assert.AreEqual(expected, hint.Delta, 0.001f);
        }

        [Test]
        public void Unclamp_PrefersUnlockingOwner_ThenUpstreamCount_ThenLowestId()
        {
            var m = Model(6, new LinkDef(0, 1), new LinkDef(1, 2),
                new LinkDef(3, 4), new LinkDef(3, 5));
            Assert.AreEqual(1, Unclamp.PickLink(m));
            Assert.IsFalse(m.CanRotate(1));
            m.ReleaseLink(Unclamp.PickLink(m));
            Assert.IsTrue(m.CanRotate(1));
            Assert.AreEqual(0, Unclamp.PickLink(m), "last owned link beats a branching owner");
            m.ReleaseLink(0);
            Assert.AreEqual(2, Unclamp.PickLink(m), "equal scores use lowest link id");
            m.ReleaseLink(2);
            m.ReleaseLink(3);
            Assert.AreEqual(-1, Unclamp.PickLink(m));
        }

        [Test]
        public void Unclamp_HandlesCycles()
        {
            var m = Model(2, new LinkDef(0, 1), new LinkDef(1, 0));
            Assert.AreEqual(0, Unclamp.PickLink(m));
            m.ReleaseLink(0);
            Assert.IsTrue(m.CanRotate(0));
        }

        [TestCase(BoosterKind.Hint)]
        [TestCase(BoosterKind.Undo)]
        [TestCase(BoosterKind.Unclamp)]
        public void Inventory_DefaultsSpendsAndRefillsIndependently(BoosterKind kind)
        {
            var counts = new Dictionary<string,int> { ["booster_hint"] = 3, ["booster_undo"] = 3, ["booster_unclamp"] = 3 };
            ConsumableGateway.Count = k => counts[k];
            ConsumableGateway.Spend = k => { if (counts[k] <= 0) return false; counts[k]--; return true; };
            try
            {
                for (int i = 0; i < 3; i++) Assert.True(BoosterInventory.TrySpend(kind));
                Assert.False(BoosterInventory.TrySpend(kind));
                BoosterInventory.Add(kind, 5);
                Assert.AreEqual(0, BoosterInventory.Count(kind), "Local grants cannot overwrite the wallet");
                foreach (BoosterKind k in System.Enum.GetValues(typeof(BoosterKind)))
                    if (k != kind) Assert.AreEqual(3, BoosterInventory.Count(k));
            }
            finally { ConsumableGateway.Count = null; ConsumableGateway.Spend = null; }

        }

        void ResetMeta()
        {
            ResetKey("orcub.stars.spent");
            ResetKey("orcub.level");
            ResetKey("orcub.highest");
            for (int i = 0; i <= LevelLibrary.Count; i++) ResetKey("orcub.stars." + i);
            foreach (var task in Workshop.Tasks) ResetKey("orcub.workshop." + task.Id);
        }

        [Test]
        public void StarBank_UsesBestStarsAcrossSaga_AndRejectsOverspending()
        {
            ResetMeta();
            Assert.AreEqual(0, StarBank.TotalEarned);
            Assert.AreEqual(0, StarBank.Spent);
            Assert.IsFalse(StarBank.TrySpend(1));
            Assert.IsFalse(StarBank.TrySpend(-1));
            Progress.RecordWin(0, 2);
            Progress.RecordWin(0, 1);
            Progress.RecordWin(1, 3);
            Assert.AreEqual(5, StarBank.TotalEarned);
            Assert.IsTrue(StarBank.TrySpend(0));
            Assert.IsTrue(StarBank.TrySpend(3));
            Assert.AreEqual(3, StarBank.Spent);
            Assert.AreEqual(2, StarBank.Available);
            Assert.IsFalse(StarBank.TrySpend(3));
            Assert.AreEqual(3, StarBank.Spent);
            Progress.RecordWin(0, 3);
            Assert.AreEqual(3, StarBank.Available);
            Assert.IsTrue(StarBank.TrySpend(3));
            Assert.AreEqual(0, StarBank.Available);
            Progress.RecordWin(LevelLibrary.CuratedCount, 3);
            Assert.AreEqual(3, StarBank.Available, "Generated levels earn spendable stars");
            Assert.IsTrue(StarBank.TrySpend(3));
            Assert.AreEqual(0, StarBank.Available);
        }

        [Test]
        public void Workshop_RequiresNextTaskAndEnoughStars_AndCannotSpendTwice()
        {
            ResetMeta();
            Assert.AreEqual("lamp", Workshop.Next.Id);
            Assert.IsFalse(Workshop.TryRestore("lamp"));
            Assert.IsFalse(Workshop.IsRestored("lamp"));
            Assert.IsFalse(Workshop.TryRestore("unknown"));
            Assert.IsFalse(Workshop.TryRestore(null));
            for (int i = 0; i < 10; i++) Progress.RecordWin(i, 3);
            Assert.IsFalse(Workshop.TryRestore("clock"));
            Assert.AreEqual(0, StarBank.Spent);
            int spent = 0;
            foreach (var task in Workshop.Tasks)
            {
                Assert.AreSame(task, Workshop.Next);
                Assert.IsTrue(Workshop.TryRestore(task.Id));
                Assert.IsTrue(Workshop.IsRestored(task.Id));
                spent += task.Cost;
                Assert.AreEqual(spent, StarBank.Spent);
                Assert.IsFalse(Workshop.TryRestore(task.Id));
                Assert.AreEqual(spent, StarBank.Spent);
            }
            Assert.IsNull(Workshop.Next);
            Assert.AreEqual(30, StarBank.Spent);
            Assert.AreEqual(0, StarBank.Available);
        }
    }
}
