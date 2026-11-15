using System;
using System.Collections.Generic;
using Orcub.Game;

namespace Orcub.Daily
{
    // The composition owner replaces this instance after sign-in/sign-out/cloud restore.
    public sealed class DailyServices
    {
        bool active=true;
        public void Deactivate() { active=false; }
        public DailyContext Context { get; }
        public DailyPuzzleService Puzzle { get; }
        public DailyGiftService Gift { get; }
        public WinStreakService Wins { get; }
        public NotificationService Notifications { get; }
        public DailyServices(string canonicalPlayerId, ILocalNotifications notifications, IDailyClock clock = null)
        {
            if (string.IsNullOrEmpty(canonicalPlayerId)) throw new ArgumentException(nameof(canonicalPlayerId));
            var state = DailyPersistence.Load(canonicalPlayerId);
            Context = new DailyContext(state, clock ?? new SystemDailyClock(), () => { if(active) DailyPersistence.Save(canonicalPlayerId, state); }, ClientGateway.Track);
            var wallet = new DailyWalletClient(canonicalPlayerId) { IsCurrent=()=>active };
            Wallet = wallet;
            Puzzle = new DailyPuzzleService(Context, wallet, () => Orcub.Net.ClientServices.Instance?.Flush() ?? System.Threading.Tasks.Task.CompletedTask); Gift = new DailyGiftService(Context, wallet);
            Wins = new WinStreakService(Context); Notifications = new NotificationService(Context, notifications);
        }
        public DailyWalletClient Wallet { get; }
        // Call once for a new foreground session; refresh schedules separately after lives/completion changes.
        public void OnAppOpen(DateTimeOffset? livesFullAt)
        { Notifications.SessionOpened(); Notifications.TrackOpen(); Reschedule(livesFullAt); RetryPendingRewards(); }
        async void RetryPendingRewards()
        {
            try { await Puzzle.RetryRewards(); }
            catch (Exception error) { UnityEngine.Debug.LogWarning("Daily rewards remain pending: " + error.Message); }
        }
        public void Reschedule(DateTimeOffset? livesFullAt)
        {
            var list = new List<NotificationCandidate>();
            if (livesFullAt.HasValue && livesFullAt.Value > Context.Clock.UtcNow)
                list.Add(new NotificationCandidate("lives", livesFullAt.Value));
            list.Add(new NotificationCandidate("daily", DailyContext.Parse(Context.Today).AddDays(1)));
            if (Context.State.PuzzleStreak > 0 && !Context.State.Completed.Contains(Context.Today))
                list.Add(new NotificationCandidate("streak", DailyContext.Parse(Context.Today).AddHours(20)));
            Notifications.Reschedule(list);
        }
    }
}
