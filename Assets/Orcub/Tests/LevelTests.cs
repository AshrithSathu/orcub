using System.Collections.Generic;
using NUnit.Framework;
using Orcub.Core;
using UnityEngine;

namespace Orcub.Tests
{
    public class LevelTests
    {
        [Test]
        public void Generator_IsDeterministic()
        {
            var s = new LevelGenerator.Settings { Rings = 8, Symmetric = true, ExtraLinkChance = 0.3f };
            LevelData a = null, b = null;
            for (int seed = 1; seed < 400 && a == null; seed++)
            {
                a = LevelGenerator.TryGenerate(seed, s);
                if (a != null) b = LevelGenerator.TryGenerate(seed, s);
            }
            Assert.IsNotNull(a, "generator produced nothing in 400 seeds");
            Assert.AreEqual(JsonUtility.ToJson(a), JsonUtility.ToJson(b));
        }

        [Test]
        public void Generator_ProducesSolvableBoardsAcrossSizes()
        {
            for (int rings = 4; rings <= 13; rings++)
            {
                int ok = 0;
                for (int seed = 0; seed < 600 && ok < 5; seed++)
                {
                    var lvl = LevelGenerator.TryGenerate(seed * 31 + rings, new LevelGenerator.Settings { Rings = rings, Symmetric = seed % 2 == 0 });
                    if (lvl == null) continue;
                    Assert.AreEqual(rings, lvl.rings.Length);
                    Assert.IsTrue(PuzzleModel.IsSolvable(lvl.ToDef(), out _), $"unsolvable {rings}-ring board, seed {lvl.seed}");
                    ok++;
                }
                Assert.Greater(ok, 0, $"no valid {rings}-ring boards");
            }
        }

        [Test]
        public void Tutorials_AreTiny_AndSolvable()
        {
            for (int t = 0; t < 3; t++)
            {
                var lvl = LevelGenerator.Tutorial(t);
                Assert.LessOrEqual(lvl.rings.Length, 3);
                Assert.IsTrue(PuzzleModel.IsSolvable(lvl.ToDef(), out _));
            }
        }

        [Test]
        public void ShippedPack_EveryLevelIsSolvableAndClean()
        {
            LevelLibrary.Reload();
            Assert.GreaterOrEqual(LevelLibrary.Count, 150, "level pack missing or too small");
            var seen = new HashSet<string>();
            for (int i = 0; i < LevelLibrary.Count; i++)
            {
                var lvl = LevelLibrary.Get(i);
                Assert.AreEqual(i + 1, lvl.id);
                var def = lvl.ToDef();
                Assert.IsTrue(PuzzleModel.IsSolvable(def, out _), $"level {lvl.id} unsolvable");
                var m = new PuzzleModel(def);
                foreach (var l in m.Links)
                    Assert.IsFalse(m.ClampInGap(l), $"level {lvl.id}: clamp {l.Owner}->{l.Target} starts in a gap");
                for (int a = 0; a < m.Rings.Count; a++)
                for (int b = a + 1; b < m.Rings.Count; b++)
                {
                    float d = (m.Rings[a].Center - m.Rings[b].Center).magnitude;
                    Assert.Greater(d, m.Rings[a].Radius + m.Rings[b].Radius + 0.9f, $"level {lvl.id}: rings {a},{b} teeth collide");
                }
                Assert.IsTrue(seen.Add(lvl.seed.ToString()), $"level {lvl.id} duplicates another level's board (seed {lvl.seed})");
            }
        }

        [Test]
        public void ShippedPack_DifficultyRisesOverall()
        {
            LevelLibrary.Reload();
            int n = LevelLibrary.Count;
            float firstTenth = 0f, lastTenth = 0f;
            int k = Mathf.Max(1, n / 10);
            for (int i = 3; i < 3 + k; i++) firstTenth += LevelLibrary.Get(i).difficulty;
            for (int i = n - k; i < n; i++) lastTenth += LevelLibrary.Get(i).difficulty;
            Assert.Greater(lastTenth / k, firstTenth / k * 1.5f);
        }

        /// <summary>Simulates a player who only taps: each tap spins the ring the short way to its nearest clamp.</summary>
        static int TapOnlyMoves(LevelDef def)
        {
            var m = new PuzzleModel(def);
            int moves = 0, guard = 0;
            while (!m.IsSolved && guard++ < 500)
            {
                for (int i = 0; i < m.Rings.Count; i++) if (m.IsFree(i)) m.TryRemove(i);
                if (m.IsSolved) break;
                int ring = -1;
                for (int i = 0; i < m.Rings.Count && ring < 0; i++)
                {
                    if (!m.CanRotate(i)) continue;
                    foreach (var id in m.Rings[i].Incoming) if (m.Links[id].Active) { ring = i; break; }
                }
                if (ring < 0) return int.MaxValue;
                var r = m.Rings[ring];
                float best = float.MaxValue, delta = 60f;
                foreach (var id in r.Incoming)
                {
                    var l = m.Links[id];
                    if (!l.Active) continue;
                    float d = Mathf.DeltaAngle(r.GapAngle, l.ClampAngle);
                    float need = Mathf.Max(0f, Mathf.Abs(d) - (PuzzleModel.GapHalfAngle - l.ClampHalfAngle)) + 3f;
                    if (need < best) { best = need; delta = Mathf.Sign(d) * need; }
                }
                foreach (var id in r.Incoming)
                    if (m.Links[id].Active && m.Links[id].Clay && m.Links[id].InGap) delta = 360;
                if (!m.DirectionAllowed(ring, delta)) delta += delta < 0 ? 360 : -360;
                int mate = m.LinkedPartner(ring);
                if (mate >= 0 && !m.DirectionAllowed(mate, -delta)) delta += delta < 0 ? 360 : -360;
                m.Rotate(ring, delta);
                m.CompleteMove();
                moves++;
            }
            return moves;
        }

        [Test]
        public void ShippedPack_MoveLimitsAreFair()
        {
            LevelLibrary.Reload();
            for (int i = 0; i < LevelLibrary.Count; i++)
            {
                var lvl = LevelLibrary.Get(i);
                var def = lvl.ToDef();
                var rules = LevelRules.For(def, tutorial: i < 3);
                int taps = TapOnlyMoves(def);
                Assert.LessOrEqual(taps, rules.MoveLimit, $"level {lvl.id}: tap-only play needs {taps} moves, limit {rules.MoveLimit}");
                Assert.GreaterOrEqual(rules.ThreeStar, rules.MinMoves, $"level {lvl.id}: 3 stars unreachable");
                Assert.AreEqual(3, rules.StarsFor(rules.MinMoves));
                Assert.AreEqual(1, rules.StarsFor(rules.MoveLimit + 10));
            }
        }

        [Test]
        public void Stars_RewardFewerMoves()
        {
            var rules = LevelRules.For(Levels.Basic10());
            Assert.AreEqual(3, rules.StarsFor(rules.ThreeStar));
            Assert.AreEqual(2, rules.StarsFor(rules.ThreeStar + 1));
            Assert.AreEqual(1, rules.StarsFor(rules.TwoStar + 1));
        }
    }
}
