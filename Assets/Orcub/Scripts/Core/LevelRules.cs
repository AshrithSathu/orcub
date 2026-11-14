using UnityEngine;

namespace Orcub.Core
{
    /// <summary>
    /// Move limit and star thresholds for a level, derived from its structure.
    ///
    /// A "move" is one gesture that turns a ring (a drag, or a tap that auto-spins to the next clamp).
    ///  - Best case (drags): every ring that has clamps on it is turned once  -> MinMoves.
    ///  - Tap players release one clamp per tap                               -> Links moves.
    /// The limit gives tap players room plus a buffer; stars reward getting close to the best case.
    /// </summary>
    public readonly struct LevelRules
    {
        public readonly int MinMoves;     // rings that must turn at least once
        public readonly int Links;
        public readonly int MoveLimit;
        public readonly int ThreeStar;    // moves <= this -> 3 stars
        public readonly int TwoStar;      // moves <= this -> 2 stars

        public LevelRules(int minMoves, int links, int moveLimit, int threeStar, int twoStar)
        {
            MinMoves = minMoves;
            Links = links;
            MoveLimit = moveLimit;
            ThreeStar = threeStar;
            TwoStar = twoStar;
        }

        /// <param name="tutorial">Early levels get a very generous limit (they teach, not test).</param>
        /// <param name="extraMoves">Adaptive-difficulty / struggle bonus, added on top.</param>
        public static LevelRules For(LevelDef def, bool tutorial = false, int extraMoves = 0)
        {
            var turned = new bool[def.Rings.Count];
            foreach (var l in def.Links) turned[l.Target] = true;
            int min = 0;
            foreach (bool t in turned) if (t) min++;
            if (MechanicSolver.HasMechanics(def))
            {
                if (!MechanicSolver.Solve(def, out var measured, out _)) throw new System.ArgumentException("Unsolvable board");
                min = measured.MinimumMoves;
            }
            int links = def.Links.Count;

            int buffer = tutorial ? 5 : Mathf.Max(2, Mathf.CeilToInt(links * 0.3f));
            int limit = Mathf.Max(min, links) + buffer + (def.IsBoss ? 8 : 0) + Mathf.Max(0, extraMoves);
            int three = min + Mathf.FloorToInt(min * 0.15f);
            int two = Mathf.Max(three + 1, links + 1);
            return new LevelRules(min, links, limit, three, two);
        }

        public int StarsFor(int movesUsed) =>
            movesUsed <= ThreeStar ? 3 : movesUsed <= TwoStar ? 2 : 1;
    }

    /// <summary>Saved progress: current level and best stars per level (PlayerPrefs).</summary>
    public static class Progress
    {
        const string LevelKey = "orcub.level";
        const string StarsPrefix = "orcub.stars.";
        const string HighestKey = "orcub.highest";

        public static int CurrentLevel
        {
            get => Mathf.Max(0, PlayerPrefs.GetInt(LevelKey, 0));
            set { PlayerPrefs.SetInt(LevelKey, Mathf.Max(0, value)); PlayerPrefs.Save(); }
        }

        /// <summary>Highest level index ever unlocked (for the map).</summary>
        public static int HighestUnlocked
        {
            get => Mathf.Max(CurrentLevel, PlayerPrefs.GetInt(HighestKey, 0));
            set { if (value > PlayerPrefs.GetInt(HighestKey, 0)) { PlayerPrefs.SetInt(HighestKey, value); PlayerPrefs.Save(); } }
        }

        public static int BestStars(int levelIndex) => PlayerPrefs.GetInt(StarsPrefix + levelIndex, 0);

        /// <summary>Records a win; keeps the best star count. Returns true if this is a new best.</summary>
        public static bool RecordWin(int levelIndex, int stars)
        {
            bool better = stars > BestStars(levelIndex);
            if (better) PlayerPrefs.SetInt(StarsPrefix + levelIndex, stars);
            HighestUnlocked = levelIndex + 1;
            PlayerPrefs.Save();
            return better;
        }
    }
}
