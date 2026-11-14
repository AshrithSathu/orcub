using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Orcub.Daily
{
    [Serializable]
    public sealed class DailySaveSection
    {
        public int Version = 1;
        public string HighestUtcDay = "", LastPuzzleDay = "", LastGiftDay = "";
        public List<string> Completed = new List<string>(), PendingPuzzleGrants = new List<string>();
        public int PuzzleStreak, WinStreak, BonusRotation, Sessions;
        public bool Freeze, FreezeEarned, BonusPending, FirstDailyCompletion;
        public string ActiveAttempt = "", LastEndedAttempt = "";
        public bool ActiveContinued;
        public string ActiveBonus = "";
        public bool NotificationsEnabled, PermissionRequested;
        public long LastOptInOffer;
        public string LastNotificationDay = "", ScheduledId = "", ScheduledDay = "", LastOpenedId = "";
        public long ScheduledAt;
    }

    public interface IDailyClock { DateTimeOffset UtcNow { get; } TimeZoneInfo Zone { get; } }
    public sealed class SystemDailyClock : IDailyClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public TimeZoneInfo Zone => TimeZoneInfo.Local;
    }
    public interface IDailyWallet
    {
        Task Grant(string reason, string utcDay, string idempotencyKey);
    }
    public sealed class DailyContext
    {
        public readonly DailySaveSection State;
        public readonly IDailyClock Clock;
        readonly Action persist;
        readonly Action<string, object> telemetry;
        public event Action Changed;
        public DailyContext(DailySaveSection state, IDailyClock clock, Action persist, Action<string, object> telemetry)
        { State = state; Clock = clock; this.persist = persist; this.telemetry = telemetry; }
        public string Today => Day(Clock.UtcNow);
        public bool ClockRolledBack => string.CompareOrdinal(Today, State.HighestUtcDay) < 0;
        public void Observe()
        {
            if (string.CompareOrdinal(Today, State.HighestUtcDay) > 0) { State.HighestUtcDay = Today; Save(); }
        }
        public void Save() { persist?.Invoke(); Changed?.Invoke(); }
        public void Track(string kind, object data) => telemetry?.Invoke(kind, data);
        public static string Day(DateTimeOffset date) => date.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        public static DateTimeOffset Parse(string day) => new DateTimeOffset(DateTime.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None), TimeSpan.Zero);
        public static string GrantKey(string reason, string day)
        {
            using var hash = SHA256.Create();
            var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes("orcub/daily/v1/" + reason + "/" + day));
            var guid = new byte[16]; Array.Copy(bytes, guid, 16); return new Guid(guid).ToString();
        }
    }
    public static class DailyPersistence
    {
        // Scope by canonical account ID; discard/recreate services when the account changes.
        public static DailySaveSection Load(string playerId) => JsonUtility.FromJson<DailySaveSection>(PlayerPrefs.GetString("orcub.daily." + playerId, "{}")) ?? new DailySaveSection();
        public static void Save(string playerId, DailySaveSection state)
        { PlayerPrefs.SetString("orcub.daily." + playerId, JsonUtility.ToJson(state)); PlayerPrefs.Save(); }
        public static string Capture(DailySaveSection state) => JsonUtility.ToJson(state);
        public static DailySaveSection Restore(string json) => JsonUtility.FromJson<DailySaveSection>(json) ?? new DailySaveSection();
    }
}
