using NUnit.Framework;
using Orcub.Core;

namespace Orcub.Tests
{
    public class LivesTests
    {
        [Test]
        public void SpendingStopsAtZero_AndRefillRestoresFive()
        {
            int count = 5;
            ConsumableGateway.Count = _ => count;
            ConsumableGateway.Spend = _ => { if (count == 0) return false; count--; return true; };
            ConsumableGateway.Refill = () => count = 5;
            ConsumableGateway.SecondsToNext = () => count == 5 ? 0 : 1800;
            Lives.Refill();
            Assert.AreEqual(Lives.Max, Lives.Count);
            Assert.AreEqual(0, Lives.SecondsToNext, "full lives: no timer");
            for (int i = 0; i < Lives.Max; i++) Assert.IsTrue(Lives.TrySpend());
            Assert.AreEqual(0, Lives.Count);
            Assert.IsFalse(Lives.TrySpend(), "cannot spend below zero");
            Assert.Greater(Lives.SecondsToNext, Lives.RegenSeconds - 5, "regen timer started when dropping below full");
            Lives.Refill();
            Assert.AreEqual(Lives.Max, Lives.Count);
        }

        [TearDown] public void Reset() { ConsumableGateway.Count = null; ConsumableGateway.Spend = null; ConsumableGateway.Refill = null; ConsumableGateway.SecondsToNext = null; }
        [Test]
        public void TimerFormat() => Assert.AreEqual("24:31", Lives.FormatTimer(24 * 60 + 31));
    }
}
