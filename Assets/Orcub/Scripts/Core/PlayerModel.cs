using System;
using System.IO;
using UnityEngine;
namespace Orcub.Core
{
    [Serializable]
    public struct MoveFeature
    {
        public bool Useful, Undo;
        public float ThinkSeconds;
        public int LockedTouches;
        public MoveFeature(bool useful, float thinkSeconds, int lockedTouches = 0, bool undo = false)
        { Useful = useful; ThinkSeconds = thinkSeconds; LockedTouches = lockedTouches; Undo = undo; }
    }
    [Serializable]
    public sealed class EarlyFeatures
    {
        public System.Collections.Generic.List<MoveFeature> Moves = new System.Collections.Generic.List<MoveFeature>();
        public int Count;
        public float Useful, Think, Locked, Undo;
        public void Add(MoveFeature move, Tuning tuning)
        {
            if (move.ThinkSeconds < 0 || float.IsNaN(move.ThinkSeconds) || float.IsInfinity(move.ThinkSeconds) || move.LockedTouches < 0)
                throw new ArgumentException("Invalid move feature");
            if (Count >= tuning.EarlyMoves) return;
            Moves.Add(move);
            Count++; Useful += move.Useful ? 1 : 0; Think += Mathf.Min(tuning.SecondsCap, move.ThinkSeconds);
            Locked += Mathf.Min(10, move.LockedTouches); Undo += move.Undo ? 1 : 0;
        }
        // Contract shared with train_skill_model.py; fixed normalisation is schema, not balancing.
        public float[] Vector() => new[] { Useful / Math.Max(1, Count), Think / Math.Max(1, Count) / 30f,
            Locked / Math.Max(1, Count) / 10f, Undo / Math.Max(1, Count) };
    }
    [Serializable]
    public sealed class PlayerModel
    {
        public string Version;
        public int Schema = 1;
        public float Bias;
        public float[] Weights;
        public static PlayerModel Parse(string json)
        {
            var model = JsonUtility.FromJson<PlayerModel>(json);
            if (model == null || model.Schema != 1 || string.IsNullOrEmpty(model.Version) || model.Weights == null || model.Weights.Length != 4 || !Finite(model.Bias))
                throw new ArgumentException("Invalid model schema");
            foreach (float w in model.Weights) if (!Finite(w) || Math.Abs(w) > 100) throw new ArgumentException("Invalid model weight");
            return model;
        }
        static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        public static PlayerModel Load(string overridePath = null)
        {
            // Remote loader writes a vetted local JSON file; no networking on the gameplay path.
            if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
                try { return Parse(File.ReadAllText(overridePath)); } catch (Exception e) when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException) { }
            var asset = Resources.Load<TextAsset>("Orcub/skill_model");
            if (asset == null) throw new InvalidOperationException("Missing shipped skill model");
            return Parse(asset.text);
        }
        public float Predict(EarlyFeatures features)
        {
            var x = features.Vector(); double score = Bias;
            for (int i = 0; i < x.Length; i++) score += Weights[i] * x[i];
            return (float)(1 / (1 + Math.Exp(-score)));
        }
    }
}
