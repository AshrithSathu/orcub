using System;
using System.Threading.Tasks;
using Orcub.Core;

namespace Orcub.Daily
{
    public enum DailyPuzzleState { Available, Completed, Missed, Upcoming }
    public sealed class DailyPuzzle
    {
        public string Day { get; }
        public LevelData Level { get; }
        public DailyPuzzle(string day, LevelData level) { Day = day; Level = level; }
    }
    public sealed class DailyPuzzleService
    {
        readonly DailyContext c;
        readonly IDailyWallet wallet;
        readonly Func<Task> flush;
        Task<DailyPuzzle> preparing;
        string preparingDay;
        bool granting;
        int rewardFailures;
        DateTimeOffset nextRewardRetry;
        string startedDay;
        public event Action Changed;
        public event Action<string> RewardGranted;
        public DailyPuzzleService(DailyContext context, IDailyWallet wallet, Func<Task> flush = null)
        { c = context; this.wallet = wallet; this.flush = flush ?? (() => Task.CompletedTask); c.Changed += () => Changed?.Invoke(); }
        public int Streak
        {
            get
            {
                if (string.IsNullOrEmpty(c.State.LastPuzzleDay)) return 0;
                int gap = (int)(DailyContext.Parse(c.Today) - DailyContext.Parse(c.State.LastPuzzleDay)).TotalDays;
                return gap > 2 || (gap == 2 && !c.State.Freeze) ? 0 : c.State.PuzzleStreak;
            }
        }
        public bool HasFreeze => c.State.Freeze;
        public static DailyReward MilestoneReward(int streak)
        {
            switch (streak)
            {
                case 3: return new DailyReward("gold", 30);
                case 7: return new DailyReward("hint", 1);
                case 14: return new DailyReward("undo", 2);
                case 30: return new DailyReward("gold", 100);
                default: return new DailyReward("gold", 0);
            }
        }
        public DailyPuzzleState StateFor(string day)
        {
            DailyContext.Parse(day);
            if (c.State.Completed.Contains(day)) return DailyPuzzleState.Completed;
            int comparison = string.CompareOrdinal(day, c.Today);
            return comparison < 0 ? DailyPuzzleState.Missed : comparison > 0 ? DailyPuzzleState.Upcoming : DailyPuzzleState.Available;
        }
        public bool CanPlay => !c.ClockRolledBack && StateFor(c.Today) == DailyPuzzleState.Available;
        public Task<DailyPuzzle> PrepareAsync()
        {
            c.Observe();
            if (!CanPlay) throw new InvalidOperationException("Daily puzzle unavailable");
            string day = c.Today;
            if (preparing == null || preparingDay != day || preparing.IsFaulted)
            { preparingDay = day; preparing = Task.Run(() => new DailyPuzzle(day, Generate(day))); }
            return preparing;
        }
        public bool TryGetPrepared(out DailyPuzzle puzzle)
        {
            puzzle = preparing != null && preparing.Status == TaskStatus.RanToCompletion && preparingDay == c.Today ? preparing.Result : null;
            return puzzle != null;
        }
        public void Start(DailyPuzzle puzzle)
        {
            if (puzzle.Day != c.Today || !CanPlay) throw new InvalidOperationException("Stale daily puzzle");
            startedDay = puzzle.Day;
            c.Track("daily_puzzle_start", new { metric_day = (int)(DailyContext.Parse(puzzle.Day) - DailyContext.Parse("1970-01-01")).TotalDays });
        }
        public async Task Complete(DailyPuzzle puzzle, bool won)
        {
            if (startedDay != puzzle.Day) throw new InvalidOperationException("Daily puzzle not started");
            if (!won) { c.Track("daily_puzzle_end", new { metric_success = false, metric_day = (int)(DailyContext.Parse(puzzle.Day) - DailyContext.Parse("1970-01-01")).TotalDays }); return; }
            c.Observe();
            if (!c.State.Completed.Contains(puzzle.Day))
            {
                // A board started before UTC midnight may finish the following day, never later.
                var age = (DailyContext.Parse(c.Today) - DailyContext.Parse(puzzle.Day)).TotalDays;
                if (c.ClockRolledBack || age < 0 || age > 1 || string.CompareOrdinal(puzzle.Day, c.State.LastPuzzleDay) <= 0)
                    throw new InvalidOperationException("Daily date expired");
                var s = c.State;
                int gap = s.LastPuzzleDay == "" ? 0 : (int)(DailyContext.Parse(puzzle.Day) - DailyContext.Parse(s.LastPuzzleDay)).TotalDays;
                if (gap == 2 && s.Freeze) { s.Freeze = false; s.PuzzleStreak++; }
                else s.PuzzleStreak = gap == 1 ? s.PuzzleStreak + 1 : 1;
                if (s.PuzzleStreak >= 7 && !s.FreezeEarned) { s.FreezeEarned = true; s.Freeze = true; }
                s.LastPuzzleDay = puzzle.Day; s.Completed.Add(puzzle.Day); s.PendingPuzzleGrants.Add(puzzle.Day);
                s.FirstDailyCompletion = true; c.Save();
                c.Track("daily_puzzle_end", new { metric_success = true, metric_streak = s.PuzzleStreak, metric_day = (int)(DailyContext.Parse(puzzle.Day) - DailyContext.Parse("1970-01-01")).TotalDays });
            }
            await RetryRewards();
        }
        public async Task RetryRewards()
        {
            if (granting || c.Clock.UtcNow < nextRewardRetry) return;
            granting = true;
            try
            {
                if (c.State.PendingPuzzleGrants.Count == 0) return;
                await flush();
                c.State.PendingPuzzleGrants.Sort(StringComparer.Ordinal);
                foreach (string day in c.State.PendingPuzzleGrants.ToArray())
                {
                    try { await wallet.Grant("daily_puzzle", day, DailyContext.GrantKey("daily_puzzle", day)); }
                    catch (DailyGrantException error) when (error.Permanent)
                    { c.State.PendingPuzzleGrants.Remove(day); c.Save(); continue; }
                    c.State.PendingPuzzleGrants.Remove(day); c.Save(); RewardGranted?.Invoke(day);
                }
            }
            catch (Exception) {
                rewardFailures = Math.Min(rewardFailures + 1, 8);
                nextRewardRetry = c.Clock.UtcNow.AddSeconds(Math.Pow(2, rewardFailures) * 5);
                throw;
            }
            finally { granting = false; if(c.State.PendingPuzzleGrants.Count == 0) { rewardFailures=0; nextRewardRetry=default; } }
        }
        public static uint Seed(string day)
        {
            DailyContext.Parse(day);
            uint hash = 2166136261;
            foreach (char ch in "orcub/daily/v1/" + day) hash = unchecked((hash ^ ch) * 16777619);
            return hash;
        }
        public static LevelData Generate(string day)
        {
            uint seed = Seed(day);
            var rng = new LevelPcg(seed);
            // Reuse the public solver-validated infinite construction, with a frozen moderate curve.
            int recipe = 200 + rng.Range(140);
            if (recipe % 20 == 19) recipe--;
            var level = InfiniteGenerator.Generate(recipe, 0, new InfiniteCurve { BaseRings = 6, MaxRings = 6 });
            int palette = rng.Range(10); bool mirror = rng.Range(2) == 0;
            for (int i = 0; i < level.rings.Length; i++)
            {
                level.rings[i].color = (level.rings[i].color + palette) % 10;
                if (mirror) { level.rings[i].x = -level.rings[i].x; level.rings[i].gap = (180 - level.rings[i].gap + 360) % 360; level.rings[i].ratchet = -level.rings[i].ratchet; }
            }
            for (int i = 0; i < level.links.Length; i++) level.links[i].color = (level.links[i].color + palette) % 10;
            level.id = -(int)(DailyContext.Parse(day) - DailyContext.Parse("0001-01-01")).TotalDays - 1;
            level.gen_version = "daily/v1/infinite-" + InfiniteGenerator.Version; level.seed = unchecked((int)seed);
            InfiniteGenerator.VerifyWitness(level);
            return level;
        }
    }
}
