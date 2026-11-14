using System;
namespace Orcub.Daily
{
    public enum StreakEnd { Win, Loss, Quit }
    public enum LevelBonus { None, Hint, Undo, Unclamp }
    public sealed class WinStreakService
    {
        readonly DailyContext c;
        public int Count => c.State.WinStreak;
        public bool BonusReady => c.State.BonusPending;
        public event Action Changed;
        public event Action GentleReset;
        public WinStreakService(DailyContext context) { c = context; }
        public LevelBonus OnLevelStart(string attemptId)
        {
            if (string.IsNullOrEmpty(attemptId)) throw new ArgumentException(nameof(attemptId));
            var s = c.State;
            if (s.LastEndedAttempt == attemptId) return LevelBonus.None;
            if (s.ActiveAttempt == attemptId) return Enum.TryParse(s.ActiveBonus, out LevelBonus bonus) ? bonus : LevelBonus.None;
            if (s.ActiveAttempt != "") OnLevelEnd(s.ActiveAttempt, StreakEnd.Quit, false);
            s.ActiveAttempt = attemptId; s.ActiveContinued = false;
            var result = LevelBonus.None;
            if (s.BonusPending)
            { result = (LevelBonus)(1 + s.BonusRotation % 3); s.BonusRotation = (s.BonusRotation + 1) % 3; s.BonusPending = false; s.WinStreak = 0; }
            s.ActiveBonus = result.ToString(); c.Save(); Changed?.Invoke(); return result;
        }
        public void OnContinue(string attemptId)
        { if (c.State.ActiveAttempt == attemptId) { c.State.ActiveContinued = true; c.Save(); } }
        public void OnLevelEnd(string attemptId, StreakEnd outcome, bool firstTry)
        {
            var s = c.State;
            if (s.ActiveAttempt != attemptId || string.IsNullOrEmpty(attemptId)) return;
            bool reset = outcome != StreakEnd.Win;
            if (reset) { s.WinStreak = 0; s.BonusPending = false; }
            else if (firstTry && !s.ActiveContinued) { s.WinStreak = Math.Min(3, s.WinStreak + 1); if (s.WinStreak == 3) s.BonusPending = true; }
            s.LastEndedAttempt = attemptId; s.ActiveAttempt = ""; s.ActiveBonus = "";
            c.Save(); c.Track("win_streak_change", new { metric_count = s.WinStreak, metric_reset = reset }); Changed?.Invoke();
            if (reset) GentleReset?.Invoke();
        }
    }
}
