using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Orcub.Core;
using UnityEditor;
using UnityEngine;
namespace Orcub.EditorTools
{
    public static class DifficultySimulator
    {
        [Serializable] sealed class Dataset { public List<AttemptRecord> Attempts = new List<AttemptRecord>(); }
        [MenuItem("Orcub/Simulate Difficulty")]
        public static void Run()
        {
            var tuning = new Tuning();
            var dataset = new Dataset();
            var report = new StringBuilder("# Difficulty simulation\n\nSeed 48271. Actual PuzzleModel actions, no sampled outcomes. Synthetic evidence only; human calibration required.\n\n| Skill band | First try wins / attempts | Win rate |\n|---|---:|---:|\n");
            var rng = new System.Random(48271);
            foreach (float ability in new[] { 800f, 1100f, 1450f })
            {
                string prefix = "orcub.sim." + Guid.NewGuid().ToString("N");
                var skill = new PlayerSkill(prefix);
                var history = new AttemptLog(persist: false);
                int wins = 0;
                // Warm start Elo with separate calibration games, never include warmup in reported rate.
                for (int i = 0; i < 120; i++)
                {
                    var d = LevelLibrary.Get(rng.Next(3, LevelLibrary.Count));
                    var r = DifficultySimulation.Play(d, 0, ability, LevelRules.For(d.ToDef()).MoveLimit, rng, tuning);
                    skill.Record(d.rating, d.solver.MinimumMoves, r.Result);
                }
                for (int i = 0; i < 600; i++)
                {
                    int slot = rng.Next(3, LevelLibrary.Count);
                    var estimate = SkillModel.Estimate(skill, history, tuning);
                    var plan = DifficultyPolicy.Select(slot, LevelLibrary.GetVariants(slot), estimate, 0, history, tuning, rng);
                    var d = LevelLibrary.Get(slot, plan.Variant);
                    var r = DifficultySimulation.Play(d, plan.Variant, ability, plan.MoveLimit, rng, tuning);
                    r.LevelIndex = slot; r.SkillEstimate = estimate.Rating;
                    r.ModelPrediction = PlayerModel.Load().Predict(r.Features);
                    r.ModelVersion = PlayerModel.Load().Version; r.TuningVersion = tuning.Version;
                    skill.Record(d.rating, d.solver.MinimumMoves, r.Result); history.Append(r);
                    if (r.Result.Outcome == AttemptOutcome.Win) wins++;
                }
                report.AppendLine($"| {ability:F0} | {wins} / 600 | {wins / 6f:F1}% |");
                PlayerPrefs.DeleteKey(prefix + "state");
            }
            // Independent varied players/boards for offline supervised training.
            rng = new System.Random(1729);
            for (int i = 0; i < 10000; i++)
            {
                int slot = rng.Next(3, LevelLibrary.Count), variant = rng.Next(5);
                var d = LevelLibrary.Get(slot, variant);
                var r = DifficultySimulation.Play(d, variant, 500 + (float)rng.NextDouble() * 1300,
                    LevelRules.For(d.ToDef()).MoveLimit, rng, tuning);
                r.LevelIndex = slot;
                dataset.Attempts.Add(r);
            }
            Directory.CreateDirectory("ArtSource/ml");
            File.WriteAllText("ArtSource/ml/sim_attempts.json", JsonUtility.ToJson(dataset));
            File.WriteAllText("ArtSource/ml/sim_report.md", report.ToString());
            Debug.Log(report.ToString());
        }
    }
}
