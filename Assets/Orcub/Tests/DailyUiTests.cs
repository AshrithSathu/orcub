using System;
using System.Collections.Generic;
using NUnit.Framework;
using Orcub.Core;
using Orcub.Game;

namespace Orcub.Tests
{
    /// <summary>Daily-loop UI state mapping (approved features_v1): calendar cells, week row, streak and reward copy.</summary>
    public sealed class DailyUiTests
    {
        static Func<string, DailyDay> States(string today, params string[] done)
        {
            var set = new HashSet<string>(done);
            return d => set.Contains(d) ? DailyDay.Completed
                : string.CompareOrdinal(d, today) < 0 ? DailyDay.Missed
                : d == today ? DailyDay.Available : DailyDay.Upcoming;
        }

        [Test]
        public void Month_IsMondayFirst_PaddedToWholeWeeks()
        {
            // 1 November 2026 is a Sunday: six empty Monday..Saturday slots first
            var cells = DailyCalendar.Month("2026-11-15", States("2026-11-15"));
            Assert.AreEqual(0, cells.Count % 7);
            for (int i = 0; i < 6; i++) Assert.AreEqual(DayMark.Empty, cells[i].Mark);
            Assert.AreEqual(1, cells[6].Day);
            Assert.AreEqual("2026-11-01", cells[6].UtcKey);
            Assert.AreEqual(30, cells.FindAll(c => c.Day > 0).Count);
        }

        [Test]
        public void Month_MarksCompletedTodayMissedAndUpcoming()
        {
            var cells = DailyCalendar.Month("2026-11-15", States("2026-11-15", "2026-11-13", "2026-11-14"));
            DayMark Of(int day) => cells.Find(c => c.Day == day).Mark;
            Assert.AreEqual(DayMark.Completed, Of(13));
            Assert.AreEqual(DayMark.Completed, Of(14));
            Assert.AreEqual(DayMark.Today, Of(15));
            Assert.AreEqual(DayMark.Missed, Of(12));
            Assert.AreEqual(DayMark.Plain, Of(16), "upcoming days are plain, never playable");
        }

        [Test]
        public void TodayCompleted_IsOutlinedAndChecked()
        {
            var cells = DailyCalendar.Month("2026-11-15", States("2026-11-15", "2026-11-15"));
            Assert.AreEqual(DayMark.TodayDone, cells.Find(c => c.Day == 15).Mark);
            Assert.AreEqual(DayMark.TodayDone, DailyCalendar.Mark(DailyDay.Completed, today: true));
        }

        [Test]
        public void Week_RunsMondayToSunday_AroundToday()
        {
            var week = DailyCalendar.Week("2026-11-13", States("2026-11-13", "2026-11-09", "2026-11-10"));   // a Friday
            Assert.AreEqual(7, week.Count);
            Assert.AreEqual("2026-11-09", week[0].UtcKey);
            Assert.AreEqual("2026-11-15", week[6].UtcKey);
            Assert.AreEqual(DayMark.Completed, week[0].Mark);
            Assert.AreEqual(DayMark.Missed, week[2].Mark);
            Assert.AreEqual(DayMark.Today, week[4].Mark);
            Assert.AreEqual(DayMark.Plain, week[5].Mark);
        }

        [Test]
        public void MonthBoundary_UsesTheUtcDayGiven()
        {
            var cells = DailyCalendar.Month("2026-12-01", States("2026-12-01", "2026-11-30"));
            Assert.AreEqual("December 2026", DailyCalendar.MonthTitle("2026-12-01"));
            Assert.AreEqual(DayMark.Today, cells.Find(c => c.Day == 1).Mark);
            Assert.IsFalse(cells.Exists(c => c.UtcKey == "2026-11-30"), "only this month's days are drawn");
        }

        [Test]
        public void Streak_Copy_FollowsServerMilestones()
        {
            Assert.AreEqual("Start a streak", DailyCalendar.StreakTitle(0));
            Assert.AreEqual("5-day streak", DailyCalendar.StreakTitle(5));
            Assert.AreEqual("3 days · 30 gold", DailyCalendar.NextMilestone(0));
            Assert.AreEqual("7 days · 1 free Hint", DailyCalendar.NextMilestone(5));
            Assert.AreEqual("14 days · 2 free Undo", DailyCalendar.NextMilestone(7));
            Assert.IsNull(DailyCalendar.NextMilestone(30));
        }

        [Test]
        public void Rewards_ReadAsTheServerTableSays()
        {
            Assert.AreEqual("20 gold", DailyCalendar.RewardText(new DailyRewardView("gold", 20)));
            Assert.AreEqual("1 Hint", DailyCalendar.RewardText(new DailyRewardView("hint", 1)));
            Assert.AreEqual("30 min lives", DailyCalendar.RewardText(new DailyRewardView("unlimited_lives_minutes", 30)));
        }

        [Test]
        public void Countdown_RoundsUpToTheMinute()
        {
            var now = new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
            Assert.AreEqual("Back in 14h 0m", DailyCalendar.Countdown(now, now.AddHours(14)));
            Assert.AreEqual("Back in 5m", DailyCalendar.Countdown(now, now.AddMinutes(4).AddSeconds(10)));
            Assert.AreEqual("Ready now", DailyCalendar.Countdown(now, now.AddSeconds(-1)));
        }

        [Test]
        public void WinStreak_Line_CountsDownToTheBonus()
        {
            Assert.AreEqual("Win 3 more in a row for a free Hint.", DailyCalendar.WinsLine(0, BoosterKind.Hint));
            Assert.AreEqual("One more win for a free Undo.", DailyCalendar.WinsLine(2, BoosterKind.Undo));
            Assert.AreEqual("Free Unclamp ready", DailyCalendar.WinsLine(3, BoosterKind.Unclamp));
        }
    }
}
