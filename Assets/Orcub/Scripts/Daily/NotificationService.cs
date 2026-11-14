using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace Orcub.Daily
{
    public interface ILocalNotifications
    {
        bool Authorized { get; }
        Task<bool> RequestPermission();
        void Cancel(string id);
        void Schedule(string id, DateTimeOffset at, string title, string body);
        string OpenedId { get; }
    }
    public readonly struct NotificationCandidate
    {
        public readonly string Kind;
        public readonly DateTimeOffset At;
        public NotificationCandidate(string kind, DateTimeOffset at) { Kind = kind; At = at; }
    }
    public static class DailyStrings
    {
        public static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>
        {
            ["optin_title"] = "Play on your time",
            ["optin_body"] = "We’ll tell you when lives refill and the daily puzzle is ready.",
            ["optin_continue"] = "Continue", ["optin_later"] = "Not now",
            ["optin_footer"] = "iOS will ask next. You choose.",
            ["lives_title"] = "Ready for another turn?", ["lives_body"] = "Your lives have refilled. Pick up whenever you like.",
            ["daily_title"] = "A new puzzle, at your pace", ["daily_body"] = "Today’s Daily Puzzle is ready.",
            ["streak_title"] = "Another turn, whenever you’re ready", ["streak_body"] = "There’s still time for today’s Daily Puzzle.",
            ["reset"] = "A fresh start", ["reset_body"] = "Win 3 in a row for a free booster."
        };
    }
    public sealed class NotificationService
    {
        readonly DailyContext c;
        readonly ILocalNotifications platform;
        bool requesting;
        public event Action Changed;
        public NotificationService(DailyContext context, ILocalNotifications platform) { c = context; this.platform = platform; }
        public bool ShouldOfferOptIn => !c.State.NotificationsEnabled && !c.State.PermissionRequested && !c.ClockRolledBack &&
            (c.State.Sessions >= 3 || c.State.FirstDailyCompletion) &&
            (c.State.LastOptInOffer == 0 || c.Clock.UtcNow.ToUnixTimeSeconds() - c.State.LastOptInOffer >= 7 * 86400);
        public void SessionOpened()
        { c.Observe(); c.State.Sessions++; c.Save(); }
        // Call when the sheet becomes visible, including if it is later dismissed.
        public bool OfferShown()
        {
            if (!ShouldOfferOptIn) return false;
            c.State.LastOptInOffer = c.Clock.UtcNow.ToUnixTimeSeconds(); c.Save(); return true;
        }
        public async Task<bool> ContinueOptIn()
        {
            if (requesting || c.State.PermissionRequested || c.State.LastOptInOffer == 0) return false;
            requesting = true; c.State.PermissionRequested = true; c.Save();
            try
            {
                c.State.NotificationsEnabled = await platform.RequestPermission(); c.Save();
                c.Track("notif_optin", new { metric_success = c.State.NotificationsEnabled }); Changed?.Invoke();
                return c.State.NotificationsEnabled;
            }
            finally { requesting = false; }
        }
        public void SetEnabled(bool enabled)
        {
            // Settings may re-enable only a previously granted system permission.
            c.State.NotificationsEnabled = enabled && platform.Authorized;
            if (!c.State.NotificationsEnabled) CancelPending();
            c.Save(); Changed?.Invoke();
        }
        public static DateTimeOffset OutsideQuietHours(DateTimeOffset at, TimeZoneInfo zone)
        {
            var local = TimeZoneInfo.ConvertTime(at, zone).DateTime;
            if (local.Hour >= 8 && local.Hour < 22) return at;
            var morning = local.Date.AddDays(local.Hour >= 22 ? 1 : 0).AddHours(8);
            while (zone.IsInvalidTime(morning)) morning = morning.AddMinutes(1);
            // For an ambiguous civil time choose the later occurrence.
            var offset = zone.GetUtcOffset(morning);
            if (zone.IsAmbiguousTime(morning))
                foreach (var choice in zone.GetAmbiguousTimeOffsets(morning)) if (choice < offset) offset = choice;
            return new DateTimeOffset(morning, offset).ToUniversalTime();
        }
        void CancelPending()
        {
            if (c.State.ScheduledId != "") platform.Cancel(c.State.ScheduledId);
            c.State.ScheduledId = ""; c.State.ScheduledDay = ""; c.State.ScheduledAt = 0;
        }
        // One pending notification total. A presumed delivery reserves its local day permanently.
        public void Reschedule(IEnumerable<NotificationCandidate> candidates)
        {
            c.Observe(); var s = c.State; var now = c.Clock.UtcNow;
            if (s.ScheduledAt > 0 && s.ScheduledAt <= now.ToUnixTimeSeconds() && string.CompareOrdinal(s.ScheduledDay, s.LastNotificationDay) > 0)
                s.LastNotificationDay = s.ScheduledDay;
            CancelPending(); c.Save();
            if (!s.NotificationsEnabled || !platform.Authorized || c.ClockRolledBack) return;
            NotificationCandidate? best = null;
            foreach (var item in candidates)
            {
                if (!DailyStrings.English.ContainsKey(item.Kind + "_title")) continue;
                var at = OutsideQuietHours(item.At > now ? item.At : now.AddMinutes(1), c.Clock.Zone);
                // At-risk reminders stop at UTC midnight; defer-ready copy instead of stale risk copy.
                if (item.Kind == "streak" && DailyContext.Day(at) != c.Today) continue;
                string localDay = TimeZoneInfo.ConvertTime(at, c.Clock.Zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (string.CompareOrdinal(localDay, s.LastNotificationDay) <= 0) continue;
                if (!best.HasValue || at < best.Value.At) best = new NotificationCandidate(item.Kind, at);
            }
            if (!best.HasValue) return;
            var pick = best.Value;
            string day = TimeZoneInfo.ConvertTime(pick.At, c.Clock.Zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string id = "orcub.daily." + day + "." + pick.Kind;
            // Persist intent before scheduling; restart can cancel this identifier even after a crash.
            s.ScheduledId = id; s.ScheduledDay = day; s.ScheduledAt = pick.At.ToUnixTimeSeconds(); c.Save();
            platform.Schedule(id, pick.At, DailyStrings.English[pick.Kind + "_title"], DailyStrings.English[pick.Kind + "_body"]);
        }
        public void TrackOpen()
        {
            string id = platform.OpenedId;
            if (string.IsNullOrEmpty(id) || !id.StartsWith("orcub.daily.", StringComparison.Ordinal) || id == c.State.LastOpenedId) return;
            c.State.LastOpenedId = id; c.Save(); c.Track("notif_open", new { metric_count = 1 });
        }
    }
}
