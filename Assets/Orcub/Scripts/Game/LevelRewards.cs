namespace Orcub.Game
{
    /// <summary>
    /// Gold for clearing a level, shown on the level-start sheet (approved boss_level_A: boss 150, regular 50).
    /// The grant itself belongs to the server wallet; until the wallet integration sets <see cref="Live"/> the
    /// sheet hides the reward panel so it never promises gold the game does not pay out.
    /// </summary>
    public static class LevelRewards
    {
        public const int Regular = 50, Boss = 150;

        /// <summary>Set by the wallet integration once level wins grant gold.</summary>
        public static bool Live;

        public static int GoldFor(bool boss) => boss ? Boss : Regular;
    }
}
