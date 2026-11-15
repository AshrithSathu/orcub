using System.Collections.Generic;
using NUnit.Framework;
using Orcub.Core;
using Orcub.Game;

namespace Orcub.Tests
{
    /// <summary>Tutorial step flow (approved tutorial_v1): order, skip safety, replays, unlock cards, telemetry.</summary>
    public sealed class TutorialTests
    {
        sealed class MemoryStore : ITutorialStore
        {
            public readonly HashSet<TutorialStep> DoneSteps = new HashSet<TutorialStep>();
            public bool Done(TutorialStep step) => DoneSteps.Contains(step);
            public void MarkDone(TutorialStep step) => DoneSteps.Add(step);
        }

        readonly List<TutorialStepEvent> _events = new List<TutorialStepEvent>();
        MemoryStore _store;
        TutorialFlow _flow;

        [SetUp]
        public void SetUp()
        {
            _events.Clear();
            _store = new MemoryStore();
            _flow = new TutorialFlow(_store, (kind, data) =>
            {
                Assert.AreEqual("tutorial_step", kind);
                _events.Add((TutorialStepEvent)data);
            });
        }

        [Test]
        public void LevelOne_TeachesRotateReleasePull_InOrder()
        {
            Assert.AreEqual(TutorialStep.Rotate, _flow.Begin(1, 1));
            _flow.OnMove();   // a move that didn't release anything changes nothing
            Assert.AreEqual(TutorialStep.Rotate, _flow.Current);
            _flow.OnReleased();
            Assert.AreEqual(TutorialStep.Release, _flow.Current);
            _flow.OnReleaseShown();
            Assert.AreEqual(TutorialStep.Pull, _flow.Current);
            _flow.OnPulled();
            Assert.AreEqual(TutorialStep.None, _flow.Current);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, _events.ConvertAll(e => e.step));
            Assert.IsTrue(_events.TrueForAll(e => e.completed));
        }

        [Test]
        public void FastPlayer_PullingDuringRelease_FinishesTheLesson()
        {
            _flow.Begin(1, 1);
            _flow.OnReleased();
            _flow.OnPulled();   // pulled before "Released!" auto-advanced
            Assert.AreEqual(TutorialStep.None, _flow.Current);
            Assert.IsTrue(_store.Done(TutorialStep.Release) && _store.Done(TutorialStep.Pull));
            Assert.AreEqual(TutorialStep.None, _flow.Begin(1, 1), "never repeats");
        }

        [Test]
        public void Skip_EndsTheLessonForGood_AndReportsNotCompleted()
        {
            _flow.Begin(1, 1);
            _flow.Skip();
            Assert.AreEqual(TutorialStep.None, _flow.Current);
            Assert.AreEqual(3, _events.Count);
            Assert.IsTrue(_events.TrueForAll(e => !e.completed));
            Assert.AreEqual(TutorialStep.None, _flow.Begin(1, 1));
            _flow.Skip();   // nothing showing: harmless
            Assert.AreEqual(3, _events.Count);
        }

        [Test]
        public void LeavingMidStep_ShowsItAgainNextTime()
        {
            _flow.Begin(1, 1);
            _flow.End();
            Assert.AreEqual(TutorialStep.None, _flow.Current);
            Assert.AreEqual(0, _events.Count);
            Assert.AreEqual(TutorialStep.Rotate, _flow.Begin(1, 1));
        }

        [Test]
        public void LevelTwo_TeachesMoves_OnTheFirstMove()
        {
            Assert.AreEqual(TutorialStep.Moves, _flow.Begin(2, 2));
            _flow.OnMove();
            Assert.AreEqual(TutorialStep.None, _flow.Current);
            Assert.AreEqual(4, _events[0].step);
        }

        [Test]
        public void Replays_NeverTeach()
        {
            Assert.AreEqual(TutorialStep.None, _flow.Begin(1, 9));
            Assert.AreEqual(TutorialStep.None, _flow.Begin(5, 30));
        }

        [Test]
        public void UnlockCards_AtFiveEightTwelve_EndOnUse()
        {
            Assert.AreEqual(TutorialStep.None, _flow.Begin(4, 4));
            Assert.AreEqual(TutorialStep.UnlockHint, _flow.Begin(5, 5));
            _flow.OnBooster(BoosterKind.Undo);   // the wrong booster doesn't count
            Assert.AreEqual(TutorialStep.UnlockHint, _flow.Current);
            _flow.OnBooster(BoosterKind.Hint);
            Assert.AreEqual(TutorialStep.None, _flow.Current);
            Assert.AreEqual(TutorialStep.UnlockUndo, _flow.Begin(8, 8));
            _flow.Skip();
            Assert.AreEqual(TutorialStep.UnlockUnclamp, _flow.Begin(13, 13), "a skipped-past unlock level still shows it");
            _flow.End();
            Assert.AreEqual(TutorialStep.None, _flow.Begin(20, 20), "past the window: no late card");
        }

        [Test]
        public void BoosterLocks_FollowTheFurthestLevel()
        {
            Assert.IsTrue(TutorialFlow.Locked(BoosterKind.Hint, 4, 4));
            Assert.IsFalse(TutorialFlow.Locked(BoosterKind.Hint, 5, 5));
            Assert.IsTrue(TutorialFlow.Locked(BoosterKind.Unclamp, 8, 11));
            Assert.IsFalse(TutorialFlow.Locked(BoosterKind.Unclamp, 1, 40), "replaying an early level keeps unlocked boosters");
        }
    }
}
