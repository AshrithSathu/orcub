using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orcub.Core
{
    /// <summary>Outcome-based Elo plus eight recent performance samples. No economy inputs.</summary>
    public sealed class PlayerSkill
    {
        public const string DefaultPrefix = "orcub.skill.";
        public const int WindowSize = 8;
        readonly string key;
        readonly State state;

        [Serializable]
        sealed class State
        {
            public float Rating = 1000f;
            public int Games;
            public List<Sample> Recent = new List<Sample>();
        }

        [Serializable]
        public struct Sample
        {
            public bool Won;
            public float Efficiency;
            public float SecondsPerMove;
            public int Boosters, Continues;
        }

        public PlayerSkill(string keyPrefix = DefaultPrefix)
        {
            key = keyPrefix + "state";
            try { state = JsonUtility.FromJson<State>(PlayerPrefs.GetString(key, "")); }
            catch (ArgumentException) { state = null; }
            if (state == null) state = new State();
            if (float.IsNaN(state.Rating) || float.IsInfinity(state.Rating)) state.Rating = 1000f;
            state.Rating = Mathf.Clamp(state.Rating, 100f, 2500f);
            state.Games = Mathf.Max(0, state.Games);
            if (state.Recent == null) state.Recent = new List<Sample>();
            Trim();
        }

        public float Rating => state.Rating;
        public int Games => state.Games;
        /// <summary>0: learning (&lt;900), 1: developing, 2: fluent (1100+), 3: expert (1250+).</summary>
        public int Tier => Rating < 900 ? 0 : Rating < 1100 ? 1 : Rating < 1250 ? 2 : 3;
        /// <summary>Evidence coverage, not a statistical probability; saturates after 24 attempts.</summary>
        public float Confidence => Mathf.Clamp01(Games / 24f);
        public IReadOnlyList<Sample> Recent => state.Recent.AsReadOnly();
        public float KFactor => 12f + 12f / (1f + Games / 20f);

        public static float ExpectedScore(float playerRating, float levelRating) =>
            (float)(1.0 / (1.0 + Math.Pow(10.0, (levelRating - playerRating) / 400.0)));

        public void Record(float levelRating, int minMoves, AttemptResult result)
        {
            if (float.IsNaN(levelRating) || float.IsInfinity(levelRating))
                throw new ArgumentOutOfRangeException(nameof(levelRating));
            result.Validate();
            bool won = result.Outcome == AttemptOutcome.Win;
            float delta = KFactor * ((won ? 1f : 0f) - ExpectedScore(Rating, levelRating));
            // Even an unexpected accidental loss can remove at most 12 points; tiers span 150+.
            state.Rating = Mathf.Clamp(Rating + Mathf.Max(-12f, delta), 100f, 2500f);
            if (state.Games < int.MaxValue) state.Games++;
            state.Recent.Add(new Sample
            {
                Won = won,
                Efficiency = won && result.MovesUsed > 0 ? Mathf.Clamp01(minMoves / (float)result.MovesUsed) : 0f,
                SecondsPerMove = result.MovesUsed > 0 ? result.Seconds / result.MovesUsed : 0f,
                Boosters = result.BoostersUsed, Continues = result.Continues
            });
            Trim();
            PlayerPrefs.SetString(key, JsonUtility.ToJson(state));
            PlayerPrefs.Save();
        }

        void Trim()
        {
            if (state.Recent.Count > WindowSize) state.Recent.RemoveRange(0, state.Recent.Count - WindowSize);
        }
    }
}
