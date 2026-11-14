using System;
using System.IO;
using NUnit.Framework;
using Orcub.Core;
using UnityEngine;

namespace Orcub.Tests
{
    public class AdaptiveTests
    {
        string prefix, directory, logPath;
        PlayerSkill skill;
        FailureStreak streak;

        [SetUp]
        public void SetUp()
        {
            prefix = "orcub.test.adaptive." + Guid.NewGuid().ToString("N") + ".";
            directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            logPath = Path.Combine(directory, "attempts.json");
            skill = new PlayerSkill(prefix);
            streak = new FailureStreak(prefix + "failure.");
        }

        [TearDown]
        public void TearDown()
        {
            PlayerPrefs.DeleteKey(prefix + "state");
            for (int i = 0; i < 200; i++) PlayerPrefs.DeleteKey(prefix + "failure." + i);
            PlayerPrefs.Save();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        [Test]
        public void RatingMovesWithOutcome_AndLimitsAccidentalLoss()
        {
            Assert.AreEqual(1000f, skill.Rating);
            Assert.AreEqual(24f, skill.KFactor);
            skill.Record(1000, 3, new AttemptResult(AttemptOutcome.Win, 4, 8));
            Assert.Greater(skill.Rating, 1000);
            float before = skill.Rating;
            skill.Record(650, 3, new AttemptResult(AttemptOutcome.Lose, 5, 10));
            Assert.Less(skill.Rating, before);
            Assert.GreaterOrEqual(skill.Rating, before - 12f);
            Assert.AreEqual(2, new PlayerSkill(prefix).Games);
            Assert.AreEqual(skill.Rating, new PlayerSkill(prefix).Rating);
        }

        [TestCase(1000f, 1000f)]
        [TestCase(800f, 1200f)]
        [TestCase(1250f, 700f)]
        public void ExpectedScoreIsSymmetric(float player, float level)
        {
            Assert.AreEqual(1f, PlayerSkill.ExpectedScore(player, level) + PlayerSkill.ExpectedScore(level, player), 0.00001f);
        }

        [Test]
        public void RecentWindowAndConfidencePersist()
        {
            for (int i = 0; i < 30; i++)
                skill.Record(1000, 3, new AttemptResult(AttemptOutcome.Win, 6, 12));
            var loaded = new PlayerSkill(prefix);
            Assert.AreEqual(8, loaded.Recent.Count);
            Assert.AreEqual(0, loaded.Recent[0].Boosters);
            Assert.AreEqual(0.5f, loaded.Recent[7].Efficiency);
            Assert.AreEqual(2f, loaded.Recent[7].SecondsPerMove);
            Assert.AreEqual(0, loaded.Recent[7].Continues);
            Assert.AreEqual(1f, loaded.Confidence);
            Assert.Less(loaded.KFactor, 24f);
            Assert.Greater(loaded.KFactor, 12f);
        }

        [Test]
        public void StreakAssistanceStartsAtTwo_Caps_AndResetsOnWin()
        {
            var rules = LevelRules.For(LevelGenerator.Tutorial(1).ToDef());
            Assert.AreEqual(0, DifficultyPolicy.Plan(5, rules, skill, streak).ExtraMoves);
            streak.Record(5, AttemptOutcome.Lose);
            Assert.AreEqual(0, DifficultyPolicy.Plan(5, rules, skill, streak).ExtraMoves);
            streak.Record(5, AttemptOutcome.Quit);
            Assert.AreEqual(2, DifficultyPolicy.Plan(5, rules, skill, streak).ExtraMoves);
            streak.Record(5, AttemptOutcome.Lose);
            Assert.IsTrue(DifficultyPolicy.Plan(5, rules, skill, streak).FreeHintGlow);
            Assert.AreEqual(3, new FailureStreak(prefix + "failure.").Count(5));
            Assert.AreEqual(0, streak.Count(6));
            for (int i = 0; i < 30; i++) streak.Record(5, AttemptOutcome.Lose);
            Assert.AreEqual(DifficultyPolicy.MaxAssistanceMoves, DifficultyPolicy.Plan(5, rules, skill, streak).ExtraMoves);
            streak.Record(5, AttemptOutcome.Win);
            Assert.AreEqual(0, streak.Count(5));
        }

        [Test]
        public void StrongPlayerKeepsAuthoredBudget()
        {
            for (int i = 0; i < 60; i++) skill.Record(1800, 1, new AttemptResult(AttemptOutcome.Win, 1, 2));
            Assert.GreaterOrEqual(skill.Tier, 2);
            var rules = LevelRules.For(LevelGenerator.Tutorial(0).ToDef());
            var plan = DifficultyPolicy.Plan(10, rules, skill, streak);
            Assert.AreEqual(0, plan.ExtraMoves);
            Assert.AreEqual(rules.MoveLimit, plan.MoveLimit);
        }

        [Test]
        public void WeakPlayerReceivesAssistanceAfterEnoughEvidence()
        {
            for (int i = 0; i < 12; i++) skill.Record(1000, 1, new AttemptResult(AttemptOutcome.Lose, 3, 6));
            var rules = new LevelRules(8, 12, 16, 8, 13);
            Assert.Greater(DifficultyPolicy.Plan(10, rules, skill, streak).ExtraMoves, 0);
        }

        [Test]
        public void GeneratedBudgetsCoverSolutionCost()
        {
            for (int rings = 4; rings <= 13; rings++)
            {
                int found = 0;
                for (int seed = 0; seed < 600 && found < 3; seed++)
                {
                    var data = LevelGenerator.TryGenerate(seed * 31 + rings,
                        new LevelGenerator.Settings { Rings = rings, Symmetric = seed % 2 == 0 });
                    if (data == null) continue;
                    var def = data.ToDef();
                    Assert.IsTrue(LevelMetrics.Evaluate(def, out _, out _, out _));
                    Assert.AreEqual(LevelRating.For(data), LevelRating.For(def));
                    var rules = LevelRules.For(def);
                    var plan = DifficultyPolicy.Plan(10, rules, skill, streak);
                    Assert.GreaterOrEqual(plan.MoveLimit, rules.MinMoves + DifficultyPolicy.SafetyMoves);
                    // A full turn of each target suffices on these validated dependency graphs.
                    var model = new PuzzleModel(def);
                    int cost = 0;
                    for (int round = 0; round <= rings && !model.IsSolved; round++)
                        for (int i = 0; i < rings; i++)
                        {
                            if (model.IsFree(i)) model.TryRemove(i);
                            else if (!model.Rings[i].Removed && model.CanRotate(i)) { model.Rotate(i, 360); cost++; }
                        }
                    Assert.IsTrue(model.IsSolved);
                    Assert.LessOrEqual(cost, plan.MoveLimit);
                    found++;
                }
                Assert.Greater(found, 0, "No sample for ring count " + rings);
            }
        }

        [Test]
        public void InvalidBudgetIsRepairedEvenBeyondAssistanceCap()
        {
            var rules = new LevelRules(20, 24, 0, 20, 24);
            var plan = DifficultyPolicy.Plan(10, rules, skill, streak);
            Assert.GreaterOrEqual(plan.MoveLimit, 26);
        }

        [Test]
        public void LogCapsAndRoundTripsPlan_AndSurvivesIoFailure()
        {
            var log = new AttemptLog(logPath);
            var plan = DifficultyPolicy.Plan(10, LevelRules.For(LevelGenerator.Tutorial(0).ToDef()), skill, streak);
            for (int i = 0; i < 505; i++)
                Assert.IsTrue(log.Append(new AttemptRecord { LevelIndex = i, Plan = plan,
                    Result = new AttemptResult(AttemptOutcome.Quit, 2, 5, 1, 2) }));
            var loaded = new AttemptLog(logPath);
            Assert.AreEqual(500, loaded.Entries.Count);
            Assert.AreEqual(5, loaded.Entries[0].LevelIndex);
            Assert.AreEqual(504, loaded.Entries[499].LevelIndex);
            Assert.AreEqual(plan.Reason, loaded.Entries[0].Plan.Reason);
            Assert.AreEqual(plan.MoveLimit, loaded.Entries[0].Plan.MoveLimit);
            Assert.AreEqual(2, loaded.Entries[0].Result.Continues);
            // A directory cannot be replaced with a JSON file on supported platforms.
            var blocked = new AttemptLog(directory);
            Assert.IsFalse(blocked.Append(new AttemptRecord()));
            File.WriteAllText(logPath, "invalid json");
            Assert.AreEqual(0, new AttemptLog(logPath).Entries.Count);
        }

        [Test]
        public void FacadeFreezesPlanAndFinishesExactlyOnce()
        {
            var adaptive = new Adaptive(skill, streak, new AttemptLog(logPath));
            var def = LevelGenerator.Tutorial(1).ToDef();
            var plan = adaptive.BeginAttempt(5, def);
            Assert.Throws<InvalidOperationException>(() => adaptive.BeginAttempt(5, def));
            streak.Record(5, AttemptOutcome.Lose);
            streak.Record(5, AttemptOutcome.Lose);
            Assert.AreEqual(0, plan.ExtraMoves);
            var result = new AttemptResult(AttemptOutcome.Quit, 4, 10, 1, 1);
            adaptive.EndAttempt(result);
            adaptive.EndAttempt(result);
            Assert.AreEqual(1, skill.Games);
            Assert.AreEqual(1, adaptive.Log.Entries.Count);
            Assert.AreSame(plan, adaptive.Log.Entries[0].Plan);
            Assert.AreEqual(3, streak.Count(5));
            Assert.IsTrue(adaptive.BeginAttempt(5, def).FreeHintGlow);
            adaptive.EndAttempt(new AttemptResult(AttemptOutcome.Win, 2, 5));
            Assert.AreEqual(0, streak.Count(5));
        }

        [Test]
        public void FacadeRejectsUnsolvableLevel()
        {
            var def = LevelGenerator.Tutorial(0).ToDef();
            def.Links.Add(new LinkDef(1, 0));
            Assert.Throws<ArgumentException>(() => new Adaptive(skill, streak, new AttemptLog(logPath)).BeginAttempt(0, def));
        }
    }
}
