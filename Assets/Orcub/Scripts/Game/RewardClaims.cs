using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using UnityEngine;

namespace Orcub.Game
{
    /// <summary>
    /// First-clear gold (GAME_WIRING.md, reviews f / g / i): after a win's level_end is queued, claim the reward with
    /// that event's id. The server is idempotent per event id and pays first clears in level order, so this queue:
    ///  - keeps ONE claim per level, in level order, with no lossy cap (it never drops the head: a dropped claim
    ///    would block every later first clear). A newer win of the same level replaces the evidence (fresh event);
    ///  - claims strictly in order and stops a pass at the first failure (later claims would only be refused);
    ///  - keeps a claim refused by the daily cap and retries it on a later UTC day (<see cref="Deferred"/>);
    ///  - drops a claim only after it was paid, or the server said it pays nothing (replay / final refusal).
    /// Retried right away a few times, then on the next boot / foreground resume / win.
    /// </summary>
    public static class RewardClaims
    {
        public const string PendingKey = "orcub.reward.pending";

        /// <summary>
        /// <see cref="ClientGateway.ClaimLevelReward"/> result meaning "refused by today's cap: keep it and claim again
        /// on a later UTC day" (410 reward_cap). 0 = nothing to pay (replay or final), &gt; 0 = gold granted.
        /// </summary>
        public const int Deferred = -1;

        /// <summary>UTC clock (tests move it across midnight).</summary>
        public static Func<DateTimeOffset> Clock = () => DateTimeOffset.UtcNow;

        static bool _busy, _dirty;

        public struct Claim
        {
            public int Index;
            public string EventId;
            /// <summary>yyyy-MM-dd (UTC): not before this day (daily cap); empty = now.</summary>
            public string NotBefore;
        }

        static string Day(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        /// <summary>Pending claims ("index:eventId[:notBefore];..."), in level order.</summary>
        public static List<Claim> Pending()
        {
            var list = new List<Claim>();
            foreach (var part in PlayerPrefs.GetString(PendingKey, "").Split(';'))
            {
                var f = part.Split(':');
                if (f.Length < 2 || !int.TryParse(f[0], out int index) || f[1].Length == 0) continue;
                if (list.Exists(c => c.Index == index)) continue;   // one per level (older saves could hold two)
                list.Add(new Claim { Index = index, EventId = f[1], NotBefore = f.Length > 2 ? f[2] : "" });
            }
            list.Sort((a, b) => a.Index.CompareTo(b.Index));
            return list;
        }

        static void Save(List<Claim> list)
        {
            list.Sort((a, b) => a.Index.CompareTo(b.Index));
            var parts = new string[list.Count];
            for (int i = 0; i < list.Count; i++)
                parts[i] = list[i].Index + ":" + list[i].EventId + (string.IsNullOrEmpty(list[i].NotBefore) ? "" : ":" + list[i].NotBefore);
            PlayerPrefs.SetString(PendingKey, string.Join(";", parts));
            PlayerPrefs.Save();
        }

        /// <summary>Queue a win's claim (its level_end event id) and start claiming. No-op when unbound.</summary>
        public static void Enqueue(int levelIndex, Action<int, int> onGranted)
        {
            string id = ClientGateway.LastLevelEndEventId?.Invoke();
            if (ClientGateway.ClaimLevelReward == null || string.IsNullOrEmpty(id)) return;
            Add(levelIndex, id);
            _ = Drain(onGranted);
        }

        /// <summary>Adds (or refreshes) a level's claim. Public for tests and tools.</summary>
        public static void Add(int levelIndex, string eventId)
        {
            var list = Pending();
            int at = list.FindIndex(c => c.Index == levelIndex);
            var claim = new Claim { Index = levelIndex, EventId = eventId, NotBefore = at >= 0 ? list[at].NotBefore : "" };
            if (at >= 0) list[at] = claim; else list.Add(claim);
            Save(list);
            _dirty = true;
        }

        /// <summary>Claims pending rewards in level order; keeps what fails or is deferred for a later retry.</summary>
        public static async Task Drain(Action<int, int> onGranted, int attempts = 3)
        {
            var claim = ClientGateway.ClaimLevelReward;
            if (claim == null) return;
            if (_busy) { _dirty = true; return; }   // the running pass picks it up (review i, L7)
            _busy = true;
            try
            {
                int extraPasses = 4;
                for (int attempt = 0; attempt < attempts; attempt++)
                {
                    _dirty = false;
                    bool failed = false;
                    string today = Day(Clock());
                    foreach (var c in Pending())
                    {
                        if (!string.IsNullOrEmpty(c.NotBefore) && string.CompareOrdinal(today, c.NotBefore) < 0) break;   // capped today
                        int granted;
                        try { granted = await claim(c.Index, c.EventId); }
                        catch (Exception) { failed = true; break; }   // keep the order: later claims would be refused
                        var left = Pending();
                        int at = left.FindIndex(p => p.Index == c.Index);
                        if (at >= 0 && left[at].EventId != c.EventId) break;   // fresh evidence arrived meanwhile: the next pass claims it
                        if (granted == Deferred)
                        {
                            if (at >= 0) { var d = left[at]; d.NotBefore = Day(Clock().AddDays(1)); left[at] = d; Save(left); }
                            break;
                        }
                        if (at >= 0) { left.RemoveAt(at); Save(left); }
                        if (granted > 0) onGranted?.Invoke(c.Index, granted);
                    }
                    if (_dirty && !failed && extraPasses-- > 0) { attempt--; continue; }   // a claim queued during the pass
                    if (!failed || attempt == attempts - 1) return;   // never wait after the last attempt
                    await Task.Delay(attempt == 0 ? 2000 : 8000);
                }
            }
            finally { _busy = false; }
        }
    }
}
