using System;
using System.Threading.Tasks;
using NUnit.Framework;
using Orcub.Ads;
using Orcub.Net;

namespace Orcub.Tests
{
    public class ReviewGTests
    {
        [TestCase("refill_lives")]
        [TestCase("out_of_lives")]
        [TestCase("retry_lives")]
        [TestCase("rewarded_lives")]
        public void LivesPlacementUsesValidNoncePlacement(string placement)
            => Assert.AreEqual("rewarded_lives", AdService.NormalizeRewardedPlacement(placement));

        [Test] public async Task ReturningSignInDrainsBeforeSwitchAndMerge()
        {
            bool queued=true, switched=false, merged=false;
            await AccountService.DrainBeforeSwitch(async () => {
                await Task.Yield(); Assert.IsFalse(switched); queued=false;
            }, () => queued);
            switched=true;
            Assert.IsFalse(queued); merged=true;
            Assert.IsTrue(switched && merged);
        }

        [Test] public async Task OfflineDrainKeepsGuestUntilLaterRetry()
        {
            bool queued=true;
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await AccountService.DrainBeforeSwitch(() => Task.FromException(new Exception("offline")), () => queued));
            Assert.IsTrue(queued);
            await AccountService.DrainBeforeSwitch(() => { queued=false; return Task.CompletedTask; }, () => queued);
            Assert.IsFalse(queued);
        }
    }
}
