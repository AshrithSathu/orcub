using System;
using System.Collections.Generic;

namespace Orcub.Game
{
    /// <summary>Tutorial moments (approved tutorial_v1). The numbers are the tutorial_step telemetry ids.</summary>
    public enum TutorialStep
    {
        None = 0,
        Rotate = 1,        // level 1: "Turn toward the clamp" (rotate_B)
        Release = 2,       // level 1: "Released!" (release_A), auto-advances
        Pull = 3,          // level 1: "Drag the free ring out" (pull_A)
        Moves = 4,         // level 2: "Fewer moves can earn more stars" (moves_A)
        UnlockHint = 5,    // level 5 (boosters_B)
        UnlockUndo = 6,    // level 8
        UnlockUnclamp = 7, // level 12
    }

    /// <summary>Which steps were finished (or skipped). PlayerPrefs in the game, a dictionary in tests.</summary>
    public interface ITutorialStore
    {
        bool Done(TutorialStep step);
        void MarkDone(TutorialStep step);
    }

    public sealed class PrefsTutorialStore : ITutorialStore
    {
        static string Key(TutorialStep s) => "orcub.tutorial." + (int)s;
        public bool Done(TutorialStep step) => UnityEngine.PlayerPrefs.GetInt(Key(step), 0) == 1;
        public void MarkDone(TutorialStep step)
        {
            UnityEngine.PlayerPrefs.SetInt(Key(step), 1);
            UnityEngine.PlayerPrefs.Save();
        }
    }

    /// <summary>
    /// The tutorial as a small state machine, separate from its overlay so it can be tested. It only ever reacts to
    /// what the player did on the board; it never blocks input, every step can be skipped, and leaving a level
    /// mid-step simply ends it (shown again the next time that level is new).
    ///
    /// Rules: tutorial steps run only on the player's newest level (never on replays). Level 1 teaches rotate →
    /// release → pull, level 2 the move counter. Booster unlock cards appear at levels 5 (Hint), 8 (Undo) and
    /// 12 (Unclamp), or within two levels after if that level was skipped past; each once (review m, M2: an
    /// unlock card counts as seen the moment it is shown, whether or not the player uses the booster that level).
    /// </summary>
    public sealed class TutorialFlow
    {
        public static readonly (TutorialStep step, Orcub.Core.BoosterKind kind, int level)[] Unlocks =
        {
            (TutorialStep.UnlockHint, Orcub.Core.BoosterKind.Hint, 5),
            (TutorialStep.UnlockUndo, Orcub.Core.BoosterKind.Undo, 8),
            (TutorialStep.UnlockUnclamp, Orcub.Core.BoosterKind.Unclamp, 12),
        };

        readonly ITutorialStore _store;
        readonly Action<string, object> _track;
        public TutorialStep Current { get; private set; }
        /// <summary>Fires on every change of <see cref="Current"/> (the overlay redraws).</summary>
        public event Action<TutorialStep> Changed;

        public TutorialFlow(ITutorialStore store, Action<string, object> track)
        {
            _store = store;
            _track = track;
        }

        /// <summary>Booster unlock level (displayed number) for a kind.</summary>
        public static int UnlockLevel(Orcub.Core.BoosterKind kind)
        {
            foreach (var u in Unlocks) if (u.kind == kind) return u.level;
            return 1;
        }

        /// <summary>A booster is locked until the furthest level the player has reached is its unlock level.</summary>
        public static bool Locked(Orcub.Core.BoosterKind kind, int displayedLevel, int frontierDisplayed) =>
            Math.Max(displayedLevel, frontierDisplayed) < UnlockLevel(kind);

        public static Orcub.Core.BoosterKind? KindOf(TutorialStep step)
        {
            foreach (var u in Unlocks) if (u.step == step) return u.kind;
            return null;
        }

        /// <summary>The step a level would teach, without starting it (layout reserves its space up front).</summary>
        public TutorialStep Peek(int displayedLevel, int frontierDisplayed)
        {
            if (displayedLevel != frontierDisplayed) return TutorialStep.None;   // replays never teach
            if (displayedLevel == 1 && !_store.Done(TutorialStep.Rotate)) return TutorialStep.Rotate;
            if (displayedLevel == 1 && !_store.Done(TutorialStep.Pull)) return TutorialStep.Pull;
            if (displayedLevel == 2 && !_store.Done(TutorialStep.Moves)) return TutorialStep.Moves;
            foreach (var u in Unlocks)
                if (displayedLevel >= u.level && displayedLevel <= u.level + 2 && !_store.Done(u.step)) return u.step;
            return TutorialStep.None;
        }

        /// <summary>The step to start when a level's board has landed (None: nothing to teach).</summary>
        public TutorialStep Begin(int displayedLevel, int frontierDisplayed)
        {
            Set(TutorialStep.None);
            var step = Peek(displayedLevel, frontierDisplayed);
            if (KindOf(step).HasValue) _store.MarkDone(step);   // M2: each unlock card once, even if it isn't used
            Set(step);
            return Current;
        }

        /// <summary>Is an unlock card for this booster on screen (its "1 free" spotlight)?</summary>
        public bool Spotlights(Orcub.Core.BoosterKind kind) => KindOf(Current) == kind;

        /// <summary>A clamp let go of its ring: the rotate lesson worked.</summary>
        public void OnReleased()
        {
            if (Current != TutorialStep.Rotate) return;
            Finish(TutorialStep.Rotate, true);
            Set(TutorialStep.Release);
        }

        /// <summary>The "Released!" moment has been on screen long enough (auto-advance).</summary>
        public void OnReleaseShown()
        {
            if (Current != TutorialStep.Release) return;
            Finish(TutorialStep.Release, true);
            Set(TutorialStep.Pull);
        }

        /// <summary>A ring left the board. Ends the level-1 lesson wherever it is (a quick player can outrun it).</summary>
        public void OnPulled()
        {
            switch (Current)
            {
                case TutorialStep.Rotate:
                    Finish(TutorialStep.Rotate, true); Finish(TutorialStep.Release, true); Finish(TutorialStep.Pull, true);
                    Set(TutorialStep.None);
                    break;
                case TutorialStep.Release:
                    Finish(TutorialStep.Release, true); Finish(TutorialStep.Pull, true);
                    Set(TutorialStep.None);
                    break;
                case TutorialStep.Pull:
                    Finish(TutorialStep.Pull, true);
                    Set(TutorialStep.None);
                    break;
            }
        }

        /// <summary>A counted move. The move-counter lesson is read once the player acts.</summary>
        public void OnMove()
        {
            if (Current != TutorialStep.Moves) return;
            Finish(TutorialStep.Moves, true);
            Set(TutorialStep.None);
        }

        /// <summary>A booster was used successfully.</summary>
        public void OnBooster(Orcub.Core.BoosterKind kind)
        {
            if (KindOf(Current) != kind) return;
            Finish(Current, true);
            Set(TutorialStep.None);
        }

        /// <summary>Skip: this step and the rest of its lesson are done for good (no confirmation).</summary>
        public void Skip()
        {
            var s = Current;
            if (s == TutorialStep.None) return;
            Finish(s, false);
            if (s == TutorialStep.Rotate) { Finish(TutorialStep.Release, false); Finish(TutorialStep.Pull, false); }
            if (s == TutorialStep.Release) Finish(TutorialStep.Pull, false);
            Set(TutorialStep.None);
        }

        /// <summary>The level ended or was left: hide the step. Unfinished steps return the next time.</summary>
        public void End() => Set(TutorialStep.None);

        readonly HashSet<TutorialStep> _reported = new HashSet<TutorialStep>();

        void Finish(TutorialStep step, bool completed)
        {
            // unlock cards are marked done when shown; their outcome (used / skipped) is still reported once
            if (KindOf(step).HasValue) { if (!_reported.Add(step)) return; _store.MarkDone(step); }
            else
            {
                if (_store.Done(step)) return;
                _store.MarkDone(step);
            }
            _track?.Invoke("tutorial_step", new TutorialStepEvent { step = (int)step, completed = completed });
        }

        void Set(TutorialStep step)
        {
            if (Current == step) return;
            Current = step;
            Changed?.Invoke(step);
        }
    }

    /// <summary>Backend typed schema TutorialStep { step: u32, completed: bool } (completed=false: skipped).</summary>
    [Serializable]
    public struct TutorialStepEvent { public int step; public bool completed; }
}
