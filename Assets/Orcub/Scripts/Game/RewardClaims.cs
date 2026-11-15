using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using UnityEngine;

namespace Orcub.Game
{
    /// <summary>
    /// The server refused a level-reward claim because the win's level_end isn't on the server for this player
    /// (409 reward_pending). The server is reachable; only this claim is stuck. Thrown by the Store binding.
    /// </summary>
    public sealed class RewardPendingException : Exception
    {
        public RewardPendingException(string message = "reward_pending") : base(message) { }
    }

    /// <summary>
    /// First-clear gold (GAME_WIRING.md, reviews f / g / i / m / n): after a win's level_end is queued, claim the reward
    /// with that event's id. The server is idempotent per event id and uses a frontier: a later claim advances past
    /// levels it has no claim for, and pays retained wins and deferred (daily-capped) levels itself. So this queue:
    ///  - keeps ONE claim per level, in level order, with no lossy cap; a newer win of a level replaces its evidence;
    ///  - claims each one independently, in order (review m, H2): a refused or deferred claim never holds back the
    ///    claims after it. Only a failure that looks like the network (two claims in a row failing the same way)
    ///    ends the pass;
    ///  - tags each claim with the player it was won by; after a sign-out or account switch a foreign claim gets one
    ///    try under the new player (a merge may have carried its event) and is dropped if refused;
    ///  - ages out a claim the server keeps refusing with reward_pending (its evidence never arrived): after
    ///    <see cref="AgeOut"/> since the first refusal and at least <see cref="MinRefusals"/> refusals;
    ///  - keeps a deferred claim (the server queued the level behind today's cap) until the next UTC day, then
    ///    replays it once so the server pays the backlog;
    ///  - drops a claim once paid, or when the server said it pays nothing (replay / final refusal).
    /// Retried right away a few times, then on the next boot / foreground resume / win.
    /// </summary>
    public static class RewardClaims
    {
        public const string PendingKey = "orcub.reward.pending";

        /// <summary>
        /// <see cref="ClientGateway.ClaimLevelReward"/> result meaning "deferred by today's cap, nothing else paid".
        /// Any value below zero is deferred; <see cref="DeferredWith"/> carries gold paid in the same call (backlog).
        /// 0 = nothing to pay (replay or final), &gt; 0 = gold granted.
        /// </summary>
        public const int Deferred = -1;

        /// <summary>A deferred claim whose response still paid <paramref name="granted"/> gold (earlier levels).</summary>
        public static int DeferredWith(int granted) => Deferred - Math.Max(0, granted);
        public static bool IsDeferred(int result) => result < 0;
        /// <summary>Gold actually paid by a claim result (deferred or not).</summary>
        public static int GoldOf(int result) => result < 0 ? Deferred - result : result;

        public static readonly TimeSpan AgeOut = TimeSpan.FromHours(24);
        public const int MinRefusals = 3;

        /// <summary>UTC clock (tests move it across midnight).</summary>
        public static Func<DateTimeOffset> Clock = () => DateTimeOffset.UtcNow;

        static bool _busy, _dirty;

        public struct Claim
        {
            public int Index;
            public string EventId;
            /// <summary>yyyy-MM-dd (UTC): not before this day (deferred); empty = now.</summary>
            public string NotBefore;
            /// <summary>Player id the win belongs to; empty = unknown (older saves: treated as the current player).</summary>
            public string Player;
            /// <summary>Unix seconds of the first reward_pending refusal; 0 = never refused.</summary>
            public long FirstRefusedUtc;
            public int Refusals;
        }

        static string Day(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        static string CurrentPlayer()
        {
            try { return ClientGateway.PlayerId?.Invoke() ?? ""; }
            catch (Exception) { return ""; }
        }

        /// <summary>Pending claims ("index:eventId[:notBefore[:player[:firstRefused[:refusals]]]];..."), in level order.</summary>
        public static List<Claim> Pending()
        {
            var list = new List<Claim>();
            foreach (var part in PlayerPrefs.GetString(PendingKey, "").Split(';'))
            {
                var f = part.Split(':');
                if (f.Length < 2 || !int.TryParse(f[0], out int index) || f[1].Length == 0) continue;
                if (list.Exists(c => c.Index == index)) continue;   // one per level (older saves could hold two)
                var c = new Claim { Index = index, EventId = f[1], NotBefore = f.Length > 2 ? f[2] : "", Player = f.Length > 3 ? f[3] : "" };
                if (f.Length > 4) long.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out c.FirstRefusedUtc);
                if (f.Length > 5) int.TryParse(f[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out c.Refusals);
                list.Add(c);
            }
            list.Sort((a, b) => a.Index.CompareTo(b.Index));
            return list;
        }

        static void Save(List<Claim> list)
        {
            list.Sort((a, b) => a.Index.CompareTo(b.Index));
            var parts = new string[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                string s = c.Index + ":" + c.EventId;
                bool extra = c.FirstRefusedUtc > 0 || c.Refusals > 0;
                if (!string.IsNullOrEmpty(c.NotBefore) || !string.IsNullOrEmpty(c.Player) || extra) s += ":" + c.NotBefore;
                if (!string.IsNullOrEmpty(c.Player) || extra) s += ":" + c.Player;
                if (extra) s += ":" + c.FirstRefusedUtc.ToString(CultureInfo.InvariantCulture) + ":" + c.Refusals.ToString(CultureInfo.InvariantCulture);
                parts[i] = s;
            }
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

        /// <summary>Adds (or refreshes) a level's claim, tagged with the current player. Public for tests and tools.</summary>
        public static void Add(int levelIndex, string eventId) => Add(levelIndex, eventId, CurrentPlayer());

        public static void Add(int levelIndex, string eventId, string player)
        {
            var list = Pending();
            int at = list.FindIndex(c => c.Index == levelIndex);
            // fresh evidence restarts the refusal clock; a deferral stays (the server already queued that level)
            var claim = new Claim { Index = levelIndex, EventId = eventId, NotBefore = at >= 0 ? list[at].NotBefore : "", Player = player ?? "" };
            if (at >= 0) list[at] = claim; else list.Add(claim);
            Save(list);
            _dirty = true;
        }

        /// <summary>Sign-out / account switch: claims won by another player get one try under the new one.</summary>
        static bool Foreign(Claim c, string player) =>
            !string.IsNullOrEmpty(player) && !string.IsNullOrEmpty(c.Player) && c.Player != player;

        /// <summary>Claims pending rewards in level order, each on its own; keeps what fails or is deferred for later.</summary>
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
                    bool transient = false;
                    int failuresInARow = 0;
                    string today = Day(Clock());
                    string player = CurrentPlayer();
                    var tried = new HashSet<string>();
                    foreach (var c in Pending())
                    {
                        if (!string.IsNullOrEmpty(c.NotBefore) && string.CompareOrdinal(today, c.NotBefore) < 0) continue;   // deferred: tomorrow
                        tried.Add(c.Index + ":" + c.EventId);
                        int result;
                        Exception error = null;
                        try { result = await claim(c.Index, c.EventId); }
                        catch (Exception e) { error = e; result = 0; }

                        var left = Pending();
                        int at = left.FindIndex(p => p.Index == c.Index);
                        if (at < 0 || left[at].EventId != c.EventId) continue;   // fresh evidence arrived meanwhile: the next pass claims it

                        if (error is RewardPendingException)
                        {
                            failuresInARow = 0;   // the server answered: this claim alone is stuck
                            if (Foreign(c, player)) { left.RemoveAt(at); Save(left); continue; }   // another player's win
                            var r = left[at];
                            long now = Clock().ToUnixTimeSeconds();
                            if (r.FirstRefusedUtc <= 0) r.FirstRefusedUtc = now;
                            r.Refusals++;
                            if (r.Refusals >= MinRefusals && now - r.FirstRefusedUtc >= (long)AgeOut.TotalSeconds)
                            {
                                left.RemoveAt(at);   // its evidence never reached the server: a later claim pays past it
                                Debug.LogWarning($"Level {c.Index + 1} reward aged out (no evidence on the server)");
                            }
                            else left[at] = r;
                            Save(left);
                            continue;
                        }
                        if (error != null)
                        {
                            transient = true;
                            if (++failuresInARow >= 2) break;   // offline / store not ready: stop hammering
                            continue;   // a claim-specific failure never blocks the next claim
                        }
                        failuresInARow = 0;
                        if (IsDeferred(result))
                        {
                            var d = left[at];
                            if (string.IsNullOrEmpty(d.NotBefore)) { d.NotBefore = Day(Clock().AddDays(1)); left[at] = d; }
                            else left.RemoveAt(at);   // the next-day replay deferred again: the server owns the debt
                            Save(left);
                        }
                        else { left.RemoveAt(at); Save(left); }
                        int gold = GoldOf(result);
                        if (gold > 0) onGranted?.Invoke(c.Index, gold);
                    }
                    if (_dirty && !transient && extraPasses-- > 0)
                    {
                        bool fresh = Pending().Exists(p => !tried.Contains(p.Index + ":" + p.EventId) && (string.IsNullOrEmpty(p.NotBefore) || string.CompareOrdinal(today, p.NotBefore) >= 0));
                        if (fresh) { attempt--; continue; }   // a claim queued during the pass
                    }
                    if (!transient || attempt == attempts - 1) return;   // never wait after the last attempt
                    await Task.Delay(attempt == 0 ? 2000 : 8000);
                }
            }
            finally { _busy = false; }
        }
    }
}
