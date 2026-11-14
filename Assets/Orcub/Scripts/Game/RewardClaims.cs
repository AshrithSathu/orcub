using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Orcub.Game
{
    /// <summary>
    /// First-clear gold (GAME_WIRING.md, review f): after a win's level_end is queued, claim the reward with that
    /// event's id. The server is idempotent per event id, so a failed claim is kept (PlayerPrefs) and retried with
    /// the SAME id: a few times right away, then on the next boot / foreground resume / win.
    /// </summary>
    public static class RewardClaims
    {
        public const string PendingKey = "orcub.reward.pending";
        const int MaxPending = 20;
        static bool _busy;

        /// <summary>Parsed pending claims ("index:eventId;..."), oldest first.</summary>
        public static List<(int index, string eventId)> Pending()
        {
            var list = new List<(int, string)>();
            foreach (var part in PlayerPrefs.GetString(PendingKey, "").Split(';'))
            {
                int colon = part.IndexOf(':');
                if (colon <= 0 || !int.TryParse(part.Substring(0, colon), out int index)) continue;
                string id = part.Substring(colon + 1);
                if (id.Length > 0) list.Add((index, id));
            }
            return list;
        }

        static void Save(List<(int index, string eventId)> list)
        {
            while (list.Count > MaxPending) list.RemoveAt(0);
            var parts = new string[list.Count];
            for (int i = 0; i < list.Count; i++) parts[i] = list[i].index + ":" + list[i].eventId;
            PlayerPrefs.SetString(PendingKey, string.Join(";", parts));
            PlayerPrefs.Save();
        }

        /// <summary>Queue a win's claim (its level_end event id) and start claiming. No-op when unbound.</summary>
        public static void Enqueue(int levelIndex, Action<int, int> onGranted)
        {
            string id = ClientGateway.LastLevelEndEventId?.Invoke();
            if (ClientGateway.ClaimLevelReward == null || string.IsNullOrEmpty(id)) return;
            var list = Pending();
            if (!list.Exists(p => p.eventId == id)) list.Add((levelIndex, id));
            Save(list);
            _ = Drain(onGranted);
        }

        /// <summary>Claims every pending reward in order; keeps the ones that fail for a later retry.</summary>
        public static async Task Drain(Action<int, int> onGranted, int attempts = 3)
        {
            var claim = ClientGateway.ClaimLevelReward;
            if (claim == null || _busy) return;
            _busy = true;
            try
            {
                for (int attempt = 0; attempt < attempts; attempt++)
                {
                    var list = Pending();
                    if (list.Count == 0) return;
                    bool failed = false;
                    foreach (var (index, id) in list)
                    {
                        int granted;
                        try { granted = await claim(index, id); }
                        catch (Exception) { failed = true; continue; }
                        var left = Pending();
                        left.RemoveAll(p => p.eventId == id);
                        Save(left);
                        if (granted > 0) onGranted?.Invoke(index, granted);
                    }
                    if (!failed || attempt == attempts - 1) return;   // never wait after the last attempt
                    await Task.Delay(attempt == 0 ? 2000 : 8000);
                }
            }
            finally { _busy = false; }
        }
    }
}
