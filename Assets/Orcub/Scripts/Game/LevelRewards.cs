namespace Orcub.Game
{
    /// <summary>
    /// Gold for a level's first clear, paid by the server wallet (review f: 50 for a boss, i.e. index % 20 == 19,
    /// 5 otherwise; replays pay 0). The level-start sheet shows the approved boss_level_A panel with these real
    /// amounts. <see cref="Live"/> is true once the Store binds <see cref="ClientGateway.ClaimLevelReward"/>, so the
    /// sheet never promises gold the game can't pay.
    /// </summary>
    public static class LevelRewards
    {
        public const int Regular = 5, Boss = 50;

        static bool? _forced;

        /// <summary>True when wins are paid; tools may force it for captures.</summary>
        public static bool Live
        {
            get => _forced ?? (ClientGateway.ClaimLevelReward != null && ClientGateway.LastLevelEndEventId != null);
            set => _forced = value;
        }

        public static bool IsBoss(int levelIndex) => levelIndex % 20 == 19;
        public static int GoldFor(bool boss) => boss ? Boss : Regular;
    }
}
