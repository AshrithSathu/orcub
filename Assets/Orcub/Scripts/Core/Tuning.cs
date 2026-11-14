using System;
using UnityEngine;
namespace Orcub.Core
{
    [Serializable]
    public sealed class Tuning
    {
        public string Version = "v2.1";
        public int EarlyMoves = 5, PaceWindow = 8, RecentVariants = 2;
        public int StepDownAfter = 2, MovesAfter = 4, HintAfter = 5, SafetyMargin = 2, MaxExtraMoves = 10;
        public float TargetWinRate = .75f, BetaConcentration = 32, TargetSpread = 140;
        public float InitialRating = 1000, MinimumRating = 100, MaximumRating = 2500, EloKBase = 12, EloKBonus = 12, EloDecayGames = 20, MaxRatingLoss = 12;
        public float EloWeight = .6f, PaceWeight = .25f, ModelWeight = .15f;
        public float SecondsCap = 30, ExcessCap = 4, FastSeconds = 2, SlowSeconds = 7, FastExcess = .2f, SlowExcess = 1.5f;
        public float PaceLowRating = 750, PaceHighRating = 1450, ModelRatingScale = 400, ConfidenceGames = 24;
        public static Tuning FromJson(string json)
        {
            var value = new Tuning();
            JsonUtility.FromJsonOverwrite(json, value);
            value.Validate();
            return value;
        }
        public Tuning Snapshot() => FromJson(JsonUtility.ToJson(this));
        public void Validate()
        {
            foreach (var field in typeof(Tuning).GetFields())
                if (field.FieldType == typeof(float))
                {
                    float v = (float)field.GetValue(this);
                    if (float.IsNaN(v) || float.IsInfinity(v) || v < 0) throw new ArgumentException("Invalid tuning: " + field.Name);
                }
            if (string.IsNullOrEmpty(Version) || EarlyMoves < 1 || EarlyMoves > 32 || PaceWindow < 1 || PaceWindow > 8 || RecentVariants < 0 || RecentVariants > 4 ||
                StepDownAfter < 1 || MovesAfter <= StepDownAfter || HintAfter < MovesAfter || SafetyMargin < 2 || MaxExtraMoves < 0 || MaxExtraMoves > 100 ||
                TargetWinRate < .5f || TargetWinRate > .95f || BetaConcentration < 2 || BetaConcentration > 1000 || TargetSpread > 1000 ||
                EloWeight + PaceWeight + ModelWeight <= 0 || SecondsCap <= 0 || ExcessCap <= 0 || FastSeconds >= SlowSeconds || FastExcess >= SlowExcess ||
                PaceLowRating >= PaceHighRating || ConfidenceGames <= 0 || EloDecayGames <= 0 || MinimumRating >= MaximumRating || InitialRating < MinimumRating || InitialRating > MaximumRating) throw new ArgumentException("Invalid tuning ranges");
        }
    }
}
