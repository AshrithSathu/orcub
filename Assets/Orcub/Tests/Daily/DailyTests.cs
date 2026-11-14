using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Orcub.Core;
using UnityEngine;

namespace Orcub.Daily.Tests
{
    public class DailyTests
    {
        sealed class Clock : IDailyClock
        {
            public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-11-15T12:00:00Z");
            public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;
        }
        sealed class Wallet : IDailyWallet
        {
            public readonly HashSet<string> Keys = new HashSet<string>();
            public int Calls, Awards;
            public bool LoseResponse;
            public readonly Dictionary<string, Exception> Errors = new Dictionary<string, Exception>();
            public Task Grant(string reason, string day, string key)
            {
                Calls++; if (Errors.TryGetValue(day, out var error)) throw error;
                if (Keys.Add(key)) Awards++;
                if (LoseResponse) { LoseResponse = false; throw new TimeoutException(); }
                return Task.CompletedTask;
            }
        }
        sealed class Notifications : ILocalNotifications
        {
            public bool Authorized { get; set; } = true;
            public int Requests, Schedules, Cancels;
            public string OpenedId { get; set; }
            public DateTimeOffset LastAt;
            public Task<bool> RequestPermission() { Requests++; return Task.FromResult(true); }
            public void Cancel(string id) { Cancels++; }
            public void Schedule(string id, DateTimeOffset at, string title, string body) { Schedules++; LastAt = at; }
        }
        Clock clock;
        DailySaveSection state;
        DailyContext c;
        Wallet wallet;
        [SetUp] public void Setup()
        { clock = new Clock(); state = new DailySaveSection(); c = new DailyContext(state, clock, () => { }, (k, v) => { }); wallet = new Wallet(); }
        [Test] public async Task PermanentExpiredDayIsDroppedAndNextDayIsClaimedAfterFlush()
        {
            state.PendingPuzzleGrants.AddRange(new[] { "2026-11-13", "2026-11-15" });
            wallet.Errors["2026-11-13"] = new DailyGrantException(410, "daily_completion_expired");
            bool flushed = false;
            var service = new DailyPuzzleService(c, wallet, () => { Assert.AreEqual(0, wallet.Calls); flushed = true; return Task.CompletedTask; });
            await service.RetryRewards();
            Assert.IsTrue(flushed); Assert.AreEqual(2, wallet.Calls);
            Assert.AreEqual(1, wallet.Awards); Assert.IsEmpty(state.PendingPuzzleGrants);
        }
        [TestCase(409)] [TestCase(429)] [TestCase(503)]
        public void TransientErrorsKeepQueue(int status)
        {
            state.PendingPuzzleGrants.AddRange(new[] { "2026-11-14", "2026-11-15" });
            wallet.Errors["2026-11-14"] = new DailyGrantException(status, "pending");
            Assert.ThrowsAsync<DailyGrantException>(async () => await new DailyPuzzleService(c, wallet).RetryRewards());
            Assert.AreEqual(2, state.PendingPuzzleGrants.Count); Assert.AreEqual(1, wallet.Calls);
        }
        [Test] public void RestoredStreakReflectsBreakage()
        {
            state.LastPuzzleDay = "2026-11-12"; state.PuzzleStreak = 12; state.Freeze = true;
            Assert.AreEqual(0, new DailyPuzzleService(c, wallet).Streak);
            state.LastPuzzleDay = "2026-11-13";
            Assert.AreEqual(12, new DailyPuzzleService(c, wallet).Streak);
            state.Freeze = false;
            Assert.AreEqual(0, new DailyPuzzleService(c, wallet).Streak);
        }
        [Test] public void DailyIdentityIsStableAndSeparateFromSaga()
        {
            var a = DailyPuzzleService.Generate(c.Today); var b = DailyPuzzleService.Generate(c.Today);
            Assert.AreEqual(a.seed, b.seed); Assert.AreEqual(a.gen_version, b.gen_version);
            Assert.That(b.rings, Is.EqualTo(a.rings)); Assert.That(b.links, Is.EqualTo(a.links)); Assert.Less(a.id, 0);
            Assert.That(a.gen_version, Does.StartWith("daily/"));
            Assert.AreNotEqual(a.seed, DailyPuzzleService.Generate("2026-11-16").seed);
            Assert.IsTrue(MechanicSolver.Solve(a.ToDef(), out _, out _)); Assert.That(a.rings.Length, Is.InRange(4, 6));
        }
        [Test] public void DailySampleAlwaysHasSolution()
        {
            for (int i = 0; i < 90; i++)
            {
                var level = DailyPuzzleService.Generate(DailyContext.Day(clock.UtcNow.AddDays(i)));
                Assert.IsTrue(MechanicSolver.Solve(level.ToDef(), out _, out _));
            }
        }
        [Test] public async Task MidnightCompletionUsesStartedDay()
        {
            var service = new DailyPuzzleService(c, wallet); var p = await service.PrepareAsync(); service.Start(p);
            clock.UtcNow = DateTimeOffset.Parse("2026-11-16T00:00:01Z"); await service.Complete(p, true);
            Assert.AreEqual("2026-11-15", state.LastPuzzleDay); Assert.IsTrue(service.CanPlay);
            Assert.AreEqual(DailyPuzzleState.Completed, service.StateFor(p.Day));
            Assert.AreEqual(DailyPuzzleState.Missed, service.StateFor("2026-11-14"));
            Assert.AreEqual(DailyPuzzleState.Upcoming, service.StateFor("2026-11-17"));
        }
        [Test] public async Task AmbiguousGrantSurvivesSaveAndRetryWithoutSecondAward()
        {
            var service = new DailyPuzzleService(c, wallet); var p = await service.PrepareAsync(); service.Start(p); wallet.LoseResponse = true;
            Assert.ThrowsAsync<TimeoutException>(async () => await service.Complete(p, true));
            var restored = DailyPersistence.Restore(DailyPersistence.Capture(state));
            var next = new DailyPuzzleService(new DailyContext(restored, clock, null, null), wallet);
            await next.RetryRewards(); Assert.AreEqual(1, wallet.Awards); Assert.IsEmpty(restored.PendingPuzzleGrants);
        }
        [Test] public async Task GiftRollbackAndLostAcknowledgementAreIdempotent()
        {
            var gift = new DailyGiftService(c, wallet); wallet.LoseResponse = true;
            Assert.ThrowsAsync<TimeoutException>(async () => await gift.Claim());
            Assert.IsTrue(await gift.Claim()); Assert.IsFalse(await gift.Claim()); Assert.AreEqual(1, wallet.Awards);
            clock.UtcNow = clock.UtcNow.AddDays(-1); Assert.IsFalse(gift.CanClaim); Assert.IsFalse(await gift.Claim());
            clock.UtcNow = clock.UtcNow.AddDays(2); Assert.IsTrue(await gift.Claim()); Assert.AreEqual(2, wallet.Awards);
        }
        [Test] public async Task PuzzleRollbackCannotCompleteOrPrepareTwice()
        {
            var daily = new DailyPuzzleService(c, wallet); var p = await daily.PrepareAsync(); daily.Start(p); await daily.Complete(p, true);
            await daily.Complete(p, true); Assert.AreEqual(1, wallet.Awards);
            clock.UtcNow = clock.UtcNow.AddDays(-1); Assert.IsFalse(daily.CanPlay);
            Assert.Throws<InvalidOperationException>(() => daily.PrepareAsync());
        }
        [Test] public async Task SevenDaysEarnOneFreezeAndOneMissUsesIt()
        {
            var daily = new DailyPuzzleService(c, wallet);
            for (int i = 0; i < 7; i++) { var p = await daily.PrepareAsync(); daily.Start(p); await daily.Complete(p, true); clock.UtcNow = clock.UtcNow.AddDays(1); }
            Assert.IsTrue(daily.HasFreeze); Assert.AreEqual(7, daily.Streak);
            clock.UtcNow = clock.UtcNow.AddDays(1); var next = await daily.PrepareAsync(); daily.Start(next); await daily.Complete(next, true);
            Assert.AreEqual(8, daily.Streak); Assert.IsFalse(daily.HasFreeze);
            clock.UtcNow = clock.UtcNow.AddDays(2); next = await daily.PrepareAsync(); daily.Start(next); await daily.Complete(next, true);
            Assert.AreEqual(1, daily.Streak); Assert.IsFalse(daily.HasFreeze);
        }
        [Test] public void WinStreakBonusRotatesAndDuplicateHooksDoNotAdvance()
        {
            var win = new WinStreakService(c);
            for (int cycle = 0; cycle < 3; cycle++)
            {
                for (int i = 0; i < 3; i++) { string id = cycle + "-" + i; win.OnLevelStart(id); win.OnLevelEnd(id, StreakEnd.Win, true); win.OnLevelEnd(id, StreakEnd.Win, true); }
                Assert.AreEqual(3, win.Count); string bonusId = "bonus" + cycle;
                Assert.AreEqual((LevelBonus)(cycle + 1), win.OnLevelStart(bonusId));
                Assert.AreEqual((LevelBonus)(cycle + 1), win.OnLevelStart(bonusId));
                win.OnLevelEnd(bonusId, StreakEnd.Win, false);
                Assert.AreEqual(0, win.Count);
            }
        }
        [Test] public void ContinuesDoNotAdvanceOrBreakButQuitResets()
        {
            var win = new WinStreakService(c); win.OnLevelStart("1"); win.OnLevelEnd("1", StreakEnd.Win, true);
            win.OnLevelStart("2"); win.OnContinue("2"); win.OnLevelEnd("2", StreakEnd.Win, true); Assert.AreEqual(1, win.Count);
            win.OnLevelStart("3"); win.OnLevelEnd("3", StreakEnd.Quit, true); Assert.AreEqual(0, win.Count);
        }
        [TestCase("2026-03-08T04:00:00Z", "2026-03-08T12:00:00Z")]
        [TestCase("2026-11-01T03:00:00Z", "2026-11-01T13:00:00Z")]
        public void QuietHoursUseCivilMorningAcrossDst(string input, string expected)
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            Assert.AreEqual(DateTimeOffset.Parse(expected), NotificationService.OutsideQuietHours(DateTimeOffset.Parse(input), zone));
        }
        [TestCase("2026-11-15T08:00:00Z", "2026-11-15T08:00:00Z")]
        [TestCase("2026-11-15T22:00:00Z", "2026-11-16T08:00:00Z")]
        public void QuietHourBoundaries(string at, string expected)
        { Assert.AreEqual(DateTimeOffset.Parse(expected), NotificationService.OutsideQuietHours(DateTimeOffset.Parse(at), TimeZoneInfo.Utc)); }
        [Test] public async Task OptInRequiresSheetContinueAndWeeklyCooldown()
        {
            var platform = new Notifications(); var n = new NotificationService(c, platform);
            n.SessionOpened(); n.SessionOpened(); Assert.IsFalse(n.ShouldOfferOptIn);
            n.SessionOpened(); Assert.IsTrue(n.ShouldOfferOptIn); Assert.IsTrue(n.OfferShown());
            Assert.AreEqual(0, platform.Requests); Assert.IsFalse(n.ShouldOfferOptIn);
            clock.UtcNow = clock.UtcNow.AddDays(7); Assert.IsTrue(n.ShouldOfferOptIn); n.OfferShown();
            Assert.IsTrue(await n.ContinueOptIn()); Assert.AreEqual(1, platform.Requests);
            Assert.IsFalse(await n.ContinueOptIn()); Assert.IsFalse(n.ShouldOfferOptIn);
        }
        [Test] public void FirstDailyCompletionOffersBeforeThirdSession()
        { state.FirstDailyCompletion = true; Assert.IsTrue(new NotificationService(c, new Notifications()).ShouldOfferOptIn); }
        [Test] public void NotificationsCapAcrossRescheduleRestartAndOptOut()
        {
            var platform = new Notifications(); state.NotificationsEnabled = true;
            var n = new NotificationService(c, platform);
            var candidates = new[] { new NotificationCandidate("lives", clock.UtcNow.AddHours(1)), new NotificationCandidate("daily", clock.UtcNow.AddHours(2)) };
            n.Reschedule(candidates); Assert.AreEqual(1, platform.Schedules);
            clock.UtcNow = clock.UtcNow.AddHours(2);
            n = new NotificationService(c, platform); n.Reschedule(candidates); Assert.AreEqual(1, platform.Schedules);
            n.SetEnabled(false); clock.UtcNow = clock.UtcNow.AddDays(1); n.Reschedule(candidates); Assert.AreEqual(1, platform.Schedules);
        }
        [Test] public void GiftRotationAndNextAtUseUtcNotLocalMidnight()
        {
            clock.Zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); clock.UtcNow = DateTimeOffset.Parse("2026-11-15T23:59:59Z");
            Assert.AreEqual(DateTimeOffset.Parse("2026-11-16T00:00:00Z"), new DailyGiftService(c, wallet).NextAt);
            var kinds = new HashSet<string>(); for (int i = 0; i < 3; i++) kinds.Add(DailyGiftService.RewardFor(DailyContext.Day(clock.UtcNow.AddDays(i))).Kind);
            Assert.AreEqual(3, kinds.Count);
        }
    }
}
