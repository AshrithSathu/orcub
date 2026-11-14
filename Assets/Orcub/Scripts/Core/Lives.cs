using System;
using UnityEngine;

namespace Orcub.Core
{
    /// <summary>
    /// Candy-Crush-style lives: up to 5, one regenerates every 30 minutes (wall-clock, so it works while the app
    /// is closed). Spent when a player restarts mid-level, or retries after failing. Winning never costs a life.
    /// </summary>
    public static class Lives
    {
        public const int Max = 5;
        public const int RegenSeconds = 30 * 60;
        const string CountKey = "orcub.lives";
        const string StampKey = "orcub.lives.stamp";   // unix seconds when the current regen timer started

        static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        static long Stamp
        {
            get => long.TryParse(PlayerPrefs.GetString(StampKey, ""), out long v) ? v : Now;
            set => PlayerPrefs.SetString(StampKey, value.ToString());
        }

        /// <summary>Apply any regeneration that happened since the last check.</summary>
        static int Tick()
        {
            int c = PlayerPrefs.GetInt(CountKey, Max);
            if (c >= Max) return c;
            long elapsed = Now - Stamp;
            if (elapsed >= RegenSeconds)
            {
                int gained = (int)(elapsed / RegenSeconds);
                c = Mathf.Min(Max, c + gained);
                Stamp = c >= Max ? Now : Stamp + gained * (long)RegenSeconds;
                PlayerPrefs.SetInt(CountKey, c);
                PlayerPrefs.Save();
            }
            return c;
        }

        public static int Count => Tick();

        /// <summary>Seconds until the next life comes back (0 when full).</summary>
        public static int SecondsToNext
        {
            get
            {
                if (Tick() >= Max) return 0;
                return (int)Math.Max(0, RegenSeconds - (Now - Stamp));
            }
        }

        /// <summary>Spend one life. Returns false (and spends nothing) when there are none left.</summary>
        public static bool TrySpend()
        {
            int c = Tick();
            if (c <= 0) return false;
            if (c >= Max) Stamp = Now;   // the regen clock starts when you drop below full
            PlayerPrefs.SetInt(CountKey, c - 1);
            PlayerPrefs.Save();
            return true;
        }

        public static void Refill()
        {
            PlayerPrefs.SetInt(CountKey, Max);
            Stamp = Now;
            PlayerPrefs.Save();
        }

        public static string FormatTimer(int seconds) => $"{seconds / 60:00}:{seconds % 60:00}";
    }
}
