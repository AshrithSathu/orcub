using System;
using System.Threading.Tasks;
namespace Orcub.Daily
{
    public readonly struct DailyReward
    {
        public readonly string Kind;
        public readonly int Amount;
        public DailyReward(string kind, int amount) { Kind = kind; Amount = amount; }
    }
    public sealed class DailyGiftService
    {
        readonly DailyContext c;
        readonly IDailyWallet wallet;
        bool claiming;
        public event Action Changed;
        public event Action<DailyReward> Claimed;
        public DailyGiftService(DailyContext context, IDailyWallet wallet)
        { c = context; this.wallet = wallet; c.Changed += () => Changed?.Invoke(); }
        public bool CanClaim => !claiming && !c.ClockRolledBack && string.CompareOrdinal(c.Today, c.State.LastGiftDay) > 0;
        public DateTimeOffset NextAt => DailyContext.Parse(string.CompareOrdinal(c.Today, c.State.LastGiftDay) >= 0 ? c.Today : c.State.LastGiftDay).AddDays(1);
        public static DailyReward RewardFor(string day)
        {
            int slot = (int)(DailyContext.Parse(day) - DailyContext.Parse("1970-01-01")).TotalDays;
            switch ((slot % 3 + 3) % 3)
            { case 0: return new DailyReward("gold", 20); case 1: return new DailyReward("hint", 1); default: return new DailyReward("unlimited_lives_minutes", 30); }
        }
        public async Task<bool> Claim()
        {
            c.Observe(); if (!CanClaim) return false;
            string day = c.Today; claiming = true; Changed?.Invoke();
            try
            {
                await wallet.Grant("daily_gift", day, DailyContext.GrantKey("daily_gift", day));
                c.State.LastGiftDay = day; c.Save();
                c.Track("daily_gift_claim", new { metric_success = true }); Claimed?.Invoke(RewardFor(day)); return true;
            }
            finally { claiming = false; Changed?.Invoke(); }
        }
    }
}
