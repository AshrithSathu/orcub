using System;
using Orcub.Core;
using UnityEngine;

namespace Orcub.Game
{
    /// <summary>
    /// Review i, H1 (client side): every first-clear win sends level_end evidence, so its gold claim can be made and
    /// the server's in-order first-clear rule never stalls. This covers the attempt whose adaptive selection failed
    /// (C14 neutral plan): it sends level_start / level_end with reason "neutral_fallback" and no rating change
    /// (nothing is recorded in the skill model or the local attempt log).
    /// </summary>
    public sealed partial class BoardView
    {
        bool _neutralAttempt;
        string _neutralStartedUtc;

        void TrackNeutralStart()
        {
            _neutralStartedUtc = DateTime.UtcNow.ToString("o");
            ClientGateway.Track("level_start", new
            {
                LevelIndex = _levelIndex, Variant = 0,
                Plan = new { variant = 0, extraMoves = 0, freeHintGlow = false, reason = "neutral_fallback", moveLimit = _rules.MoveLimit },
            });
        }

        /// <returns>True when a level_end went out for this (neutral) attempt.</returns>
        bool EndNeutralAttempt(AttemptOutcome outcome)
        {
            if (!_neutralAttempt) return false;
            _neutralAttempt = false;
            try
            {
                var record = new AttemptRecord
                {
                    Variant = 0, TuningVersion = "neutral", ModelVersion = "neutral", Features = new EarlyFeatures(),
                    LevelIndex = _levelIndex, StartedUtc = _neutralStartedUtc ?? DateTime.UtcNow.ToString("o"),
                    Plan = _plan ?? AttemptPlan.Neutral(_rules.MoveLimit),
                    Result = new AttemptResult(outcome, _movesUsed, Mathf.Max(0f, Time.unscaledTime - _attemptStart),
                        _attemptBoosters, _attemptContinues),
                };
                ClientGateway.Track("level_end", record);
                ClientGateway.Save?.Invoke();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("Neutral level_end skipped: " + e.Message);
                ConsumableGateway.LevelEnded?.Invoke();
                return false;
            }
        }
    }
}
