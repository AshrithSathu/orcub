using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Orcub.Core
{
    public enum AttemptOutcome { Win, Lose, Quit }

    [Serializable]
    public struct AttemptResult
    {
        public AttemptOutcome Outcome;
        public int MovesUsed;
        public float Seconds;
        public int BoostersUsed;
        public int Continues;

        public AttemptResult(AttemptOutcome outcome, int movesUsed, float seconds, int boostersUsed = 0, int continues = 0)
        { Outcome = outcome; MovesUsed = movesUsed; Seconds = seconds; BoostersUsed = boostersUsed; Continues = continues; }

        internal void Validate()
        {
            if (!Enum.IsDefined(typeof(AttemptOutcome), Outcome) || MovesUsed < 0 || Seconds < 0 ||
                float.IsNaN(Seconds) || float.IsInfinity(Seconds) || BoostersUsed < 0 || Continues < 0)
                throw new ArgumentOutOfRangeException(nameof(AttemptResult), "Attempt counters and duration must be finite and nonnegative.");
        }
    }

    [Serializable]
    public sealed class AttemptRecord
    {
        public int Version = 2;
        public int Variant;
        public string TuningVersion, ModelVersion;
        public EarlyFeatures Features;
        public float ModelPrediction;
        public float SkillEstimate, SkillConfidence;
        public int LevelIndex;
        public string StartedUtc;
        public AttemptPlan Plan;
        public float LevelRating;
        public float RatingBefore, RatingAfter;
        public AttemptResult Result;
    }

    /// <summary>Best-effort local JSON telemetry, oldest records discarded. No network dependency.</summary>
    public sealed class AttemptLog
    {
        public const int Capacity = 500;
        readonly string path;
        readonly bool persist;
        readonly Records records;
        [Serializable]
        sealed class Records { public List<AttemptRecord> Attempts = new List<AttemptRecord>(); }

        public AttemptLog(string filePath = null, bool persist = true)
        {
            this.persist = persist;
            path = filePath ?? Path.Combine(Application.persistentDataPath, "orcub-attempts.json");
            try
            {
                if (persist && File.Exists(path)) records = JsonUtility.FromJson<Records>(File.ReadAllText(path));
            }
            catch (Exception) { /* Telemetry must never prevent play, including corrupt or inaccessible files. */ }
            if (records == null || records.Attempts == null) records = new Records();
            Trim();
        }

        public IReadOnlyList<AttemptRecord> Entries => records.Attempts.AsReadOnly();

        /// <summary>Returns false on IO/serialization failure; capped history still remains in memory.</summary>
        public bool Append(AttemptRecord record)
        {
            if (record == null) return false;
            records.Attempts.Add(record);
            Trim();
            if (!persist) return true;
            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                // Write to a sibling first so interrupted writes don't truncate the last good history.
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonUtility.ToJson(records));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
                return true;
            }
            catch (Exception) { return false; }
        }

        void Trim()
        {
            if (records.Attempts.Count > Capacity) records.Attempts.RemoveRange(0, records.Attempts.Count - Capacity);
        }
    }
}
