using System;
using System.IO;
using NUnit.Framework;
using Orcub.Core;
using UnityEngine;
namespace Orcub.Tests
{
    public class AdaptiveV2Tests
    {
        [Test]
        public void PackHasFiveMeasuredDistinctSolvableVariantsPerSlot()
        {
            LevelLibrary.Reload();
            Assert.AreEqual(200, LevelLibrary.Count);
            for (int slot = 0; slot < LevelLibrary.Count; slot++)
            {
                var variants = LevelLibrary.GetVariants(slot);
                Assert.AreEqual(5, variants.Length);
                Assert.AreEqual(JsonUtility.ToJson(LevelLibrary.Get(slot)), JsonUtility.ToJson(variants[0]));
                var seeds = new System.Collections.Generic.HashSet<int>();
                foreach (var data in variants)
                {
                    Assert.IsTrue(seeds.Add(data.seed));
                    Assert.AreEqual(slot + 1, data.id);
                    Assert.AreEqual(LevelRating.For(data), data.rating, .001);
                    var measure = SolverMeasure.Search(data.ToDef());
                    Assert.AreEqual(measure.MinimumMoves, data.solver.MinimumMoves);
                    Assert.AreEqual(0, measure.DeadEnds);
                    foreach (var link in new PuzzleModel(data.ToDef()).Links)
                        Assert.IsFalse(new PuzzleModel(data.ToDef()).ClampInGap(link));
                }
            }
        }
        [Test]
        public void BetaSamplerMatchesMeanAndIsDeterministic()
        {
            var a = new System.Random(42); var b = new System.Random(42);
            double sum = 0;
            for (int i = 0; i < 10000; i++)
            {
                double value = DifficultyPolicy.Beta(3, 7, a);
                Assert.AreEqual(value, DifficultyPolicy.Beta(3, 7, b));
                Assert.That(value, Is.InRange(0d, 1d)); sum += value;
            }
            Assert.AreEqual(.3, sum / 10000, .01);
        }
        [Test]
        public void ConfigRejectsInvalidAndPreservesDefaults()
        {
            Assert.AreEqual(5, Tuning.FromJson("{\"Version\":\"remote-2\"}").EarlyMoves);
            Assert.Throws<ArgumentException>(() => Tuning.FromJson("{\"TargetWinRate\":1}"));
            Assert.Throws<ArgumentException>(() => Tuning.FromJson("{\"MovesAfter\":1}"));
            Assert.Throws<ArgumentException>(() => PlayerModel.Parse("{\"Schema\":99}"));
        }
        [Test]
        public void FeatureWindowAndModelRoundTrip()
        {
            var f = new EarlyFeatures(); var t = new Tuning();
            for (int i = 0; i < 20; i++) f.Add(new MoveFeature(true, 2, 1, false), t);
            Assert.AreEqual(5, f.Count);
            Assert.AreEqual(1, f.Vector()[0]);
            var model = PlayerModel.Load();
            Assert.AreEqual(model.Predict(f), PlayerModel.Parse(JsonUtility.ToJson(model)).Predict(f));
            Assert.That(model.Predict(f), Is.InRange(0f, 1f));
            Assert.AreNotEqual("bootstrap", model.Version);
        }
        [Test]
        public void RetryStepsDownBeforeMoveAidAndEnforcesMinimum()
        {
            var choices = LevelLibrary.GetVariants(100);
            var log = new AttemptLog(persist: false); var t = new Tuning();
            int hardest = 0;
            for (int i = 1; i < choices.Length; i++) if (choices[i].rating > choices[hardest].rating) hardest = i;
            log.Append(new AttemptRecord { LevelIndex = 100, Variant = hardest });
            var p = DifficultyPolicy.Select(100, choices, new SkillEstimate(1800, 1, 2), 2, log, t, new System.Random(1));
            Assert.Less(choices[p.Variant].rating, choices[hardest].rating);
            Assert.AreEqual(0, p.ExtraMoves);
            Assert.GreaterOrEqual(p.MoveLimit, choices[p.Variant].solver.MinimumMoves + t.SafetyMargin);
            var assisted = DifficultyPolicy.Select(100, choices, new SkillEstimate(800, 1, 0), 6, log, t, new System.Random(1));
            Assert.Greater(assisted.ExtraMoves, 0); Assert.IsTrue(assisted.FreeHintGlow);
        }
        [Test]
        public void StrongPlayersSelectHarderBoardsAndAvoidRecent()
        {
            var choices = LevelLibrary.GetVariants(100); var t = new Tuning();
            var log = new AttemptLog(persist: false);
            var low = DifficultyPolicy.Select(100, choices, new SkillEstimate(700, 1, 0), 0, log, t, new System.Random(1));
            var high = DifficultyPolicy.Select(100, choices, new SkillEstimate(1800, 1, 2), 0, log, t, new System.Random(1));
            Assert.Greater(choices[high.Variant].rating, choices[low.Variant].rating);
            log.Append(new AttemptRecord { LevelIndex = 100, Variant = high.Variant });
            Assert.AreNotEqual(high.Variant, DifficultyPolicy.Select(100, choices, new SkillEstimate(1800, 1, 2), 0, log, t, new System.Random(1)).Variant);
        }
        [Test]
        public void LegacyLogLoadsAndRemoteModelFallsBack()
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "{\"Attempts\":[{\"Version\":1,\"LevelIndex\":5}]}");
                var log = new AttemptLog(path);
                Assert.AreEqual(1, log.Entries.Count); Assert.AreEqual(0, log.Entries[0].Variant);
                Assert.AreEqual(PlayerModel.Load().Version, PlayerModel.Load(path).Version);
            }
            finally { File.Delete(path); }
        }
        [Test]
        public void V2FacadeFreezesConfigurationAndLogsFeaturesExactlyOnce()
        {
            string prefix = "orcub.test.v2." + Guid.NewGuid().ToString("N");
            var skill = new PlayerSkill(prefix); var streak = new FailureStreak(prefix + ".fail.");
            var log = new AttemptLog(persist: false);
            try
            {
                var adaptive = new Adaptive(skill, streak, log, random: new System.Random(5));
                adaptive.Configure("{\"Version\":\"test-config\"}");
                var plan = adaptive.BeginAttempt(20);
                Assert.Throws<InvalidOperationException>(() => adaptive.Configure("{}"));
                adaptive.RecordMove(true, 2, 1);
                var result = new AttemptResult(AttemptOutcome.Win, 8, 16);
                adaptive.EndAttempt(result); adaptive.EndAttempt(result);
                Assert.AreEqual(1, log.Entries.Count);
                Assert.AreEqual(plan.Variant, log.Entries[0].Variant);
                Assert.AreEqual("test-config", log.Entries[0].TuningVersion);
                Assert.AreEqual(1, log.Entries[0].Features.Moves.Count);
                Assert.That(log.Entries[0].ModelPrediction, Is.InRange(0f, 1f));
            }
            finally { PlayerPrefs.DeleteKey(prefix + "state"); PlayerPrefs.DeleteKey(prefix + ".fail.20"); }
        }
        [Test]
        public void BotActuallySolvesAndSkillImprovesCompletion()
        {
            var t = new Tuning(); int weak = 0, strong = 0;
            var a = new System.Random(551); var b = new System.Random(551);
            for (int i = 0; i < 300; i++)
            {
                var data = LevelLibrary.Get(3 + i % 197);
                int budget = LevelRules.For(data.ToDef()).MoveLimit;
                var low = DifficultySimulation.Play(data, 0, 650, budget, a, t);
                var high = DifficultySimulation.Play(data, 0, 1600, budget, b, t);
                if (low.Result.Outcome == AttemptOutcome.Win) weak++;
                if (high.Result.Outcome == AttemptOutcome.Win) strong++;
                Assert.LessOrEqual(high.Result.MovesUsed, budget);
            }
            Assert.Greater(strong, weak + 30);
        }
    }
}
