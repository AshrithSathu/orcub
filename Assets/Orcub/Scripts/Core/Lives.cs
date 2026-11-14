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
        public static int Count => ConsumableGateway.Count?.Invoke("life") ?? Max;

        /// <summary>Seconds until the next life comes back (0 when full).</summary>
        public static int SecondsToNext
        {
            get
            {
                return ConsumableGateway.SecondsToNext?.Invoke() ?? 0;
            }
        }

        /// <summary>Spend one life. Returns false (and spends nothing) when there are none left.</summary>
        public static bool TrySpend()
        {
            return ConsumableGateway.Spend?.Invoke("life") ?? false;
        }

        public static System.Threading.Tasks.Task<bool> RefillFromAd(string placement) => ConsumableGateway.RefillFromAd?.Invoke(placement) ?? System.Threading.Tasks.Task.FromResult(false);
        public static System.Threading.Tasks.Task<bool> RefillWithGold() => ConsumableGateway.RefillWithGold?.Invoke() ?? System.Threading.Tasks.Task.FromResult(false);
        public static void Refill() => ConsumableGateway.Refill?.Invoke();

        public static string FormatTimer(int seconds) => $"{seconds / 60:00}:{seconds % 60:00}";
    }
}
