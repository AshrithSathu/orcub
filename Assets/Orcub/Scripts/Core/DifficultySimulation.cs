using System;
using System.Collections.Generic;
using UnityEngine;
namespace Orcub.Core
{
    /// <summary>Seeded action noise on the actual model. Outcomes are never sampled or forced.</summary>
    public static class DifficultySimulation
    {
        public static AttemptRecord Play(LevelData data, int variant, float ability, int budget, System.Random rng, Tuning tuning)
        {
            var puzzle = new PuzzleModel(data.ToDef());
            var features = new EarlyFeatures();
            float seconds = 0; int moves = 0;
            while (!puzzle.IsSolved && moves < budget)
            {
                for (int i = 0; i < puzzle.Rings.Count; i++) if (puzzle.IsFree(i)) puzzle.TryRemove(i);
                if (puzzle.IsSolved) break;
                var useful = new List<int>();
                for (int i = 0; i < puzzle.Rings.Count; i++)
                    if (puzzle.CanRotate(i) && !puzzle.IsFree(i) && !puzzle.Rings[i].Removed) useful.Add(i);
                if (useful.Count == 0) throw new InvalidOperationException("Solver-validated bot board stuck");
                // Action accuracy varies with challenge and latent player ability, not the target win rate.
                double accuracy = .50 + .48 / (1 + Math.Exp((data.rating - ability - 100) / 180));
                bool deliberate = rng.NextDouble() < accuracy;
                int ring = deliberate ? useful[rng.Next(useful.Count)] : rng.Next(puzzle.Rings.Count);
                float before = Adaptive.RequiredRotation(puzzle);
                bool locked = !puzzle.CanRotate(ring), undo = !deliberate && rng.NextDouble() < .15;
                var snapshot = puzzle.Capture();
                puzzle.Rotate(ring, deliberate ? 360 : (float)(rng.NextDouble() * 90 - 45));
                if (undo) puzzle.Restore(snapshot);
                float think = (float)(Math.Exp((1200 - ability) / 600) * (1 + 3 * rng.NextDouble()));
                features.Add(new MoveFeature(Adaptive.RequiredRotation(puzzle) < before, think, locked ? 1 : 0, undo), tuning);
                seconds += think; moves++;
            }
            for (int i = 0; i < puzzle.Rings.Count; i++) if (puzzle.IsFree(i)) puzzle.TryRemove(i);
            return new AttemptRecord { Variant = variant, LevelRating = data.rating, Features = features,
                Result = new AttemptResult(puzzle.IsSolved ? AttemptOutcome.Win : AttemptOutcome.Lose, moves, seconds) };
        }
    }
}
