using System;
using System.Threading.Tasks;
using Orcub.Core;

namespace Orcub.Game
{
    // SDK-free boundary: gameplay remains usable before any client has initialised.
    public static class ClientGateway
    {
        public static Action<string, object> Event;
        public static Action<Adaptive> Configure;
        public static Action Save;
        public static Action<double, int, int> AdCaps;
        public static Action<string, long, string, string, string, string> Paid;
        public static void Track(string kind, object data) => Event?.Invoke(kind, data);

        // ------------------------------------------------------------------ booster tutorial unlocks

        /// <summary>
        /// Server booster_unlock grant for "hint" (level 5), "undo" (level 8) or "unclamp" (level 12). True once the
        /// wallet accepted it (or already had it). Bound by the Store composition; null until a client API exists.
        /// </summary>
        public static Func<string, Task<bool>> BoosterUnlock;

        // ------------------------------------------------------------------ level rewards (GAME_WIRING.md, review f)

        /// <summary>Event id of the level_end just queued (ClientServices.LastLevelEndEventId).</summary>
        public static Func<string> LastLevelEndEventId;
        /// <summary>StoreRuntime.ClaimLevelReward(levelIndex, eventId): gold granted (0 on replay); throws when not ready.</summary>
        public static Func<int, string, Task<int>> ClaimLevelReward;

        // ------------------------------------------------------------------ daily loop (backend/docs/daily-client-followups.md)

        /// <summary>Current daily save section as JSON (DailyPersistence.Capture), for Net/SaveSnapshot.Capture.</summary>
        public static Func<string> DailyCapture;
        /// <summary>Apply a reconciled cloud daily section (accepted / conflict / GET save responses).</summary>
        public static Action<string> DailyRestore;
        /// <summary>Boot and every foreground resume: DailyServices.OnAppOpen(livesFullAtUtc).</summary>
        public static Action<DateTimeOffset?> AppOpened;
        /// <summary>After each successful ClientServices.Flush: DailyServices retries pending puzzle rewards.</summary>
        public static Action Flushed;

        /// <summary>Game calls this on boot and on foreground resume, after the loader finished.</summary>
        public static void OpenApp(DateTimeOffset? livesFullAtUtc)
        {
            try { AppOpened?.Invoke(livesFullAtUtc); }
            catch (Exception e) { UnityEngine.Debug.LogWarning("App-open hook failed: " + e.Message); }
        }

        /// <summary>Net calls this after a successful telemetry flush (kept here so neither side references the other).</summary>
        public static void NotifyFlushed()
        {
            try { Flushed?.Invoke(); }
            catch (Exception e) { UnityEngine.Debug.LogWarning("Flush hook failed: " + e.Message); }
        }
    }

    /// <summary>When the lives meter will be full again (UTC), for notifications; null when already full.</summary>
    public static class LivesClock
    {
        public static DateTimeOffset? FullAt(DateTimeOffset nowUtc) => FullAt(nowUtc, Lives.Count, Lives.SecondsToNext);

        public static DateTimeOffset? FullAt(DateTimeOffset nowUtc, int count, int secondsToNext)
        {
            if (count >= Lives.Max) return null;
            int missing = Lives.Max - count;
            int first = secondsToNext > 0 ? secondsToNext : Lives.RegenSeconds;
            return nowUtc.AddSeconds(first + (long)(missing - 1) * Lives.RegenSeconds);
        }
    }

    /// <summary>Booster tutorial unlocks (displayed level numbers) and their one-time grant bookkeeping.</summary>
    public static class BoosterUnlocks
    {
        public static readonly (string kind, int level)[] Schedule = { ("hint", 5), ("undo", 8), ("unclamp", 12) };

        public static string Key(string kind) => "orcub.booster_unlock." + kind;

        /// <summary>Kinds whose unlock level has been reached (displayed level) and whose grant isn't confirmed yet.</summary>
        public static System.Collections.Generic.List<string> Due(int displayedLevel, Func<string, bool> claimed)
        {
            var due = new System.Collections.Generic.List<string>();
            foreach (var (kind, level) in Schedule)
                if (displayedLevel >= level && !claimed(kind)) due.Add(kind);
            return due;
        }
    }
}
