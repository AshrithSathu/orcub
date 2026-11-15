using System;
using Orcub.Core;

namespace Orcub.Game
{
    /// <summary>
    /// The board's end-of-attempt decisions as small pure functions (review i: M1, M2, M3, M4), so the ordering and
    /// the dead-board rules are tested without a scene.
    /// </summary>
    public static class FailFlow
    {
        public enum Card { None, DeadEnd, OutOfMoves }

        /// <summary>
        /// M1: a dead board is a dead end whether or not moves remain (the last move can rust a ring solid), and a
        /// dead board never gets the "+N moves" continue. Out of moves on a live board: the normal card.
        /// </summary>
        public static Card Decide(int movesLeft, bool dead) =>
            dead ? Card.DeadEnd : movesLeft <= 0 ? Card.OutOfMoves : Card.None;

        /// <summary>
        /// M3: spend a life, THEN record the loss and restart. With no life to spend nothing is recorded, so backing
        /// out of the out-of-lives sheet returns to a still-open attempt (a later win still sends its level_end).
        /// </summary>
        public static bool SpendThen(Func<bool> trySpendLife, Action recordLoss, Action restart, Action outOfLives)
        {
            if (!trySpendLife()) { outOfLives?.Invoke(); return false; }
            recordLoss?.Invoke();
            restart?.Invoke();
            return true;
        }

        /// <summary>
        /// M2: the link an Unclamp should open on a dead board: a frozen ring's own clamp first (freeing it makes the
        /// frozen ring pullable), otherwise the booster's usual pick. -1 when nothing can be opened.
        /// </summary>
        public static int RescueLink(PuzzleModel m)
        {
            foreach (var r in m.Rings)
            {
                if (r.Removed || !r.Frozen) continue;
                foreach (int id in r.Incoming)
                    if (m.Links[id].Active) return id;
            }
            return Unclamp.PickLink(m);
        }

        /// <summary>
        /// M4: a board's rules, or false when the board is broken (invalid gear group, unsolvable, too many rings):
        /// the caller then starts the ready stand-in instead of a half-built level.
        /// </summary>
        public static bool TryRules(LevelDef def, int levelIndex, int extraMoves, out LevelRules rules)
        {
            try
            {
                _ = new PuzzleModel(def);   // invalid gear groups / self links throw here, not in the rules
                rules = LevelRules.For(def, tutorial: levelIndex < 3, extraMoves: extraMoves);
                return true;
            }
            catch (Exception) { rules = default; return false; }
        }
    }
}
