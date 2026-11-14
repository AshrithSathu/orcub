using System;
using UnityEngine;

namespace Orcub.Core
{
    /// <summary>Independent consecutive failures per zero-based saga index; quitting counts as a failure.</summary>
    public sealed class FailureStreak
    {
        readonly string prefix;
        public FailureStreak(string keyPrefix = "orcub.skill.failure.") { prefix = keyPrefix; }
        public int Count(int levelIndex) => Mathf.Max(0, PlayerPrefs.GetInt(Key(levelIndex), 0));
        public void Record(int levelIndex, AttemptOutcome outcome)
        {
            if (!Enum.IsDefined(typeof(AttemptOutcome), outcome)) throw new ArgumentOutOfRangeException(nameof(outcome));
            int count = Count(levelIndex);
            PlayerPrefs.SetInt(Key(levelIndex), outcome == AttemptOutcome.Win ? 0 : count == int.MaxValue ? count : count + 1);
            PlayerPrefs.Save();
        }
        string Key(int levelIndex)
        {
            if (levelIndex < 0) throw new ArgumentOutOfRangeException(nameof(levelIndex));
            return prefix + levelIndex;
        }
    }
}
