using System;
using System.Threading.Tasks;
using Orcub.Core;
using Orcub.Daily;
using Orcub.Game;

namespace Orcub.Features
{
    /// <summary>
    /// IDailyPort over DailyRuntime.Current (Daily README "UI API"). The instance follows account switches; an
    /// operation started for one account never reports into another (the prepared puzzle is held per service).
    /// </summary>
    public sealed class DailyPort : IDailyPort
    {
        DailyServices _bound;
        DailyPuzzle _prepared;
        public event Action Changed;

        static DailyServices S => DailyRuntime.Current;

        public void Poll()
        {
            var s = S;
            if (s == _bound) return;
            if (_bound != null) _bound.Context.Changed -= Raise;
            _bound = s;
            _prepared = null;
            if (s != null) s.Context.Changed += Raise;
            Raise();
        }

        void Raise() => Changed?.Invoke();

        public bool Ready => S != null;
        public string TodayUtc => S?.Context.Today ?? DailyContext.Day(DateTimeOffset.UtcNow);

        public DailyDay StateFor(string utcDay)
        {
            var s = S;
            if (s == null) return string.CompareOrdinal(utcDay, TodayUtc) > 0 ? DailyDay.Upcoming : utcDay == TodayUtc ? DailyDay.Available : DailyDay.Missed;
            switch (s.Puzzle.StateFor(utcDay))
            {
                case DailyPuzzleState.Completed: return DailyDay.Completed;
                case DailyPuzzleState.Available: return DailyDay.Available;
                case DailyPuzzleState.Upcoming: return DailyDay.Upcoming;
                default: return DailyDay.Missed;
            }
        }

        public bool CanPlay => S?.Puzzle.CanPlay == true;
        public int Streak => S?.Puzzle.Streak ?? 0;
        public bool HasFreeze => S?.Puzzle.HasFreeze == true;

        public async Task<LevelData> PrepareDaily()
        {
            var s = S ?? throw new InvalidOperationException("daily_unavailable");
            var puzzle = await s.Puzzle.PrepareAsync();
            if (S != s || puzzle.Day != s.Context.Today) throw new InvalidOperationException("daily_stale");   // account or day changed
            _prepared = puzzle;
            return puzzle.Level;
        }

        public bool StartDaily()
        {
            var s = S;
            if (s == null || _prepared == null || _prepared.Day != s.Context.Today) return false;
            try { s.Puzzle.Start(_prepared); return true; }
            catch (InvalidOperationException) { return false; }
        }

        public async Task CompleteDaily(bool won)
        {
            var s = S;
            if (s == null || _prepared == null) return;
            await s.Puzzle.Complete(_prepared, won);
            s.Reschedule(LivesClock.FullAt(DateTimeOffset.UtcNow));
        }

        public bool CanClaimGift => S?.Gift.CanClaim == true;
        public DateTimeOffset NextGiftAt => S?.Gift.NextAt ?? DateTimeOffset.UtcNow.Date.AddDays(1);

        public DailyRewardView GiftFor(string utcDay)
        {
            var r = DailyGiftService.RewardFor(utcDay);
            return new DailyRewardView(r.Kind, r.Amount);
        }

        public async Task<bool> ClaimGift()
        {
            var s = S;
            if (s == null) return false;
            try { return await s.Gift.Claim(); }
            catch (Exception) { return false; }   // never granted locally: the sheet offers a retry
        }

        public int Wins => S?.Wins.Count ?? 0;
        public bool BonusReady => S?.Wins.BonusReady == true;
        public BoosterKind NextBonus => (BoosterKind)(((S?.Context.State.BonusRotation ?? 0) % 3 + 3) % 3);

        public BoosterKind? WinStart(string attemptId)
        {
            var s = S;
            if (s == null) return null;
            switch (s.Wins.OnLevelStart(attemptId))
            {
                case LevelBonus.Hint: return BoosterKind.Hint;
                case LevelBonus.Undo: return BoosterKind.Undo;
                case LevelBonus.Unclamp: return BoosterKind.Unclamp;
                default: return null;
            }
        }

        public void WinContinue(string attemptId) => S?.Wins.OnContinue(attemptId);

        public void WinEnd(string attemptId, StreakOutcome outcome, bool firstTry) =>
            S?.Wins.OnLevelEnd(attemptId, outcome == StreakOutcome.Win ? StreakEnd.Win : outcome == StreakOutcome.Loss ? StreakEnd.Loss : StreakEnd.Quit, firstTry);

        public bool ShouldOfferOptIn => S?.Notifications.ShouldOfferOptIn == true;
        public bool OptInShown() => S?.Notifications.OfferShown() == true;

        public async Task<bool> ContinueOptIn()
        {
            var s = S;
            if (s == null) return false;
            bool ok = await s.Notifications.ContinueOptIn();
            s.Reschedule(LivesClock.FullAt(DateTimeOffset.UtcNow));
            return ok;
        }

        public void Reschedule(DateTimeOffset? livesFullAtUtc) => S?.Reschedule(livesFullAtUtc);
    }
}
