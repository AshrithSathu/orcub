using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Orcub.Core;

namespace Orcub.Game
{
    /// <summary>A UTC day's daily-puzzle state as the service reports it.</summary>
    public enum DailyDay { Completed, Available, Missed, Upcoming }

    /// <summary>How a win-streak attempt ended (Daily StreakEnd).</summary>
    public enum StreakOutcome { Win, Loss, Quit }

    /// <summary>One reward ("gold" 20, "hint" 1, "unlimited_lives_minutes" 30, ...) from the server table.</summary>
    public readonly struct DailyRewardView
    {
        public readonly string Kind;
        public readonly int Amount;
        public DailyRewardView(string kind, int amount) { Kind = kind; Amount = amount; }
    }

    /// <summary>
    /// SDK-free daily-loop boundary (Game cannot reference Daily). Bound by Orcub.Features (DailyPort) to
    /// DailyRuntime.Current; the service and the server decide every date and reward.
    /// </summary>
    public interface IDailyPort
    {
        string TodayUtc { get; }
        DailyDay StateFor(string utcDay);
        bool CanPlay { get; }
        int Streak { get; }
        bool HasFreeze { get; }
        Task<LevelData> PrepareDaily();
        /// <summary>The prepared board is actually entering (Puzzle.Start). False when the day rolled over.</summary>
        bool StartDaily();
        Task CompleteDaily(bool won);

        bool CanClaimGift { get; }
        DateTimeOffset NextGiftAt { get; }
        DailyRewardView GiftFor(string utcDay);
        Task<bool> ClaimGift();

        int Wins { get; }
        bool BonusReady { get; }
        BoosterKind NextBonus { get; }
        /// <summary>Saga attempt start: the free attempt-local booster this attempt carries (null: none).</summary>
        BoosterKind? WinStart(string attemptId);
        void WinContinue(string attemptId);
        void WinEnd(string attemptId, StreakOutcome outcome, bool firstTry);

        bool ShouldOfferOptIn { get; }
        /// <summary>Records the weekly cooldown; call right before the sheet appears.</summary>
        bool OptInShown();
        Task<bool> ContinueOptIn();
        void Reschedule(DateTimeOffset? livesFullAtUtc);

        event Action Changed;
    }

    public static class DailyGateway
    {
        public static IDailyPort Port;
        /// <summary>Capture tools: a demo port that wins over the real one.</summary>
        public static IDailyPort Fake;
        public static IDailyPort Current => Fake ?? Port;
    }

    /// <summary>What a calendar cell shows (approved daily_puzzle_B): a check, the outlined today, or plain.</summary>
    public enum DayMark { Empty, Plain, Completed, Today, TodayDone, Missed }

    public readonly struct DayCell
    {
        public readonly int Day;        // 1..31, 0 for an empty grid slot
        public readonly string UtcKey;  // yyyy-MM-dd, null for an empty slot
        public readonly DayMark Mark;
        public DayCell(int day, string key, DayMark mark) { Day = day; UtcKey = key; Mark = mark; }
    }

    /// <summary>Pure mapping from the daily service's states to the calendar UI (tested without Unity UI).</summary>
    public static class DailyCalendar
    {
        public static readonly string[] WeekDays = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

        public static DateTime Parse(string utcDay) =>
            DateTime.ParseExact(utcDay, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None);

        public static string Key(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        /// <summary>Monday-first column of a date (0..6).</summary>
        public static int Column(DateTime d) => ((int)d.DayOfWeek + 6) % 7;

        public static DayMark Mark(DailyDay state, bool today)
        {
            if (today) return state == DailyDay.Completed ? DayMark.TodayDone : DayMark.Today;
            switch (state)
            {
                case DailyDay.Completed: return DayMark.Completed;
                case DailyDay.Missed: return DayMark.Missed;
                default: return DayMark.Plain;
            }
        }

        /// <summary>The month containing today, Monday-first, padded with empty slots to whole weeks.</summary>
        public static List<DayCell> Month(string todayUtc, Func<string, DailyDay> stateFor)
        {
            var today = Parse(todayUtc);
            var first = new DateTime(today.Year, today.Month, 1);
            int days = DateTime.DaysInMonth(today.Year, today.Month);
            var cells = new List<DayCell>();
            for (int i = 0; i < Column(first); i++) cells.Add(new DayCell(0, null, DayMark.Empty));
            for (int d = 1; d <= days; d++)
            {
                var date = first.AddDays(d - 1);
                string key = Key(date);
                cells.Add(new DayCell(d, key, Mark(stateFor(key), key == todayUtc)));
            }
            while (cells.Count % 7 != 0) cells.Add(new DayCell(0, null, DayMark.Empty));
            return cells;
        }

        /// <summary>Monday..Sunday of the current week.</summary>
        public static List<DayCell> Week(string todayUtc, Func<string, DailyDay> stateFor)
        {
            var today = Parse(todayUtc);
            var monday = today.AddDays(-Column(today));
            var cells = new List<DayCell>(7);
            for (int i = 0; i < 7; i++)
            {
                var date = monday.AddDays(i);
                string key = Key(date);
                cells.Add(new DayCell(date.Day, key, Mark(stateFor(key), key == todayUtc)));
            }
            return cells;
        }

        public static string MonthTitle(string todayUtc) => Parse(todayUtc).ToString("MMMM yyyy", CultureInfo.InvariantCulture);

        /// <summary>Streak milestones (server table): 3 = 30 gold, 7 = 1 Hint, 14 = 2 Undo, 30 = 100 gold.</summary>
        public static readonly (int days, DailyRewardView reward)[] Milestones =
        {
            (3, new DailyRewardView("gold", 30)), (7, new DailyRewardView("hint", 1)),
            (14, new DailyRewardView("undo", 2)), (30, new DailyRewardView("gold", 100)),
        };

        /// <summary>"5-day streak" / "Start a streak".</summary>
        public static string StreakTitle(int streak) => streak <= 0 ? "Start a streak" : streak == 1 ? "1-day streak" : $"{streak}-day streak";

        /// <summary>The next milestone line, e.g. "7 days · 1 free Hint" (null past the last one).</summary>
        public static string NextMilestone(int streak)
        {
            foreach (var (days, reward) in Milestones)
                if (days > streak) return $"{days} days · {RewardText(reward, free: true)}";
            return null;
        }

        /// <summary>"20 gold", "1 Hint", "2 Undo", "30 min lives". <paramref name="free"/> reads "1 free Hint".</summary>
        public static string RewardText(DailyRewardView r, bool free = false)
        {
            switch (r.Kind)
            {
                case "gold": return $"{r.Amount} gold";
                case "hint": return free ? $"{r.Amount} free Hint" : $"{r.Amount} Hint";
                case "undo": return free ? $"{r.Amount} free Undo" : $"{r.Amount} Undo";
                case "unclamp": return free ? $"{r.Amount} free Unclamp" : $"{r.Amount} Unclamp";
                case "unlimited_lives_minutes": return $"{r.Amount} min lives";
                default: return $"{r.Amount} {r.Kind}";
            }
        }

        /// <summary>"Back in 5h 12m" / "Back in 12m" for the next gift / puzzle.</summary>
        public static string Countdown(DateTimeOffset now, DateTimeOffset next)
        {
            var span = next - now;
            if (span <= TimeSpan.Zero) return "Ready now";
            int total = Math.Max(1, (int)Math.Ceiling(span.TotalMinutes - 1e-9));
            int h = total / 60, m = total % 60;
            return h > 0 ? $"Back in {h}h {m}m" : $"Back in {m}m";
        }

        /// <summary>Win-streak line under the track: "One more win for a free Hint." etc.</summary>
        /// <param name="waitsForLevel">&gt; 0: the bonus is earned but its booster unlocks at that level (review m, M1).</param>
        public static string WinsLine(int wins, BoosterKind next, int waitsForLevel = 0)
        {
            string name = next == BoosterKind.Hint ? "Hint" : next == BoosterKind.Undo ? "Undo" : "Unclamp";
            int left = Math.Max(0, 3 - wins);
            if (left == 0 && waitsForLevel > 0) return $"Free {name} saved for level {waitsForLevel}";
            return left == 0 ? $"Free {name} ready" : left == 1 ? $"One more win for a free {name}." : $"Win {left} more in a row for a free {name}.";
        }
    }
}
