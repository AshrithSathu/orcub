using NUnit.Framework;
using Orcub.Ads;

namespace Orcub.Tests
{
    public class AdPolicyTests
    {
        AdsConfig config;
        AdPolicy policy;
        [SetUp] public void Setup()
        {
            config = new AdsConfig(); policy = new AdPolicy(config);
            Win(3);
        }
        void Win(int count) { for (int i = 0; i < count; i++) policy.RecordWin(); }
        [TestCase(1, false)] [TestCase(7, false)] [TestCase(8, true)] [TestCase(9, true)]
        public void FirstLevelGate(int level, bool allowed) => Assert.AreEqual(allowed, policy.CanShowInterstitial(level, true, 150));
        [Test] public void LossNeverShows() => Assert.IsFalse(policy.CanShowInterstitial(100, false, 9999));
        [Test] public void KillSwitch() { config.adsEnabled = false; Assert.IsFalse(policy.CanShowInterstitial(8, true, 150)); }
        [TestCase(149.99, false)] [TestCase(150, true)]
        public void SessionTimeGate(double now, bool allowed) => Assert.AreEqual(allowed, policy.CanShowInterstitial(8, true, now));
        [Test] public void TimeAndLevelGatesAreBothRequired()
        {
            policy.RecordInterstitial(150); Win(2);
            Assert.IsFalse(policy.CanShowInterstitial(9, true, 300));
            Win(1); Assert.IsFalse(policy.CanShowInterstitial(9, true, 299.99));
            Assert.IsTrue(policy.CanShowInterstitial(9, true, 300));
        }
        [Test] public void SessionCap()
        {
            for (int i = 0; i < 6; i++)
            {
                double now = (i + 1) * 150;
                Assert.IsTrue(policy.CanShowInterstitial(8, true, now));
                policy.RecordInterstitial(now); Win(3);
            }
            Assert.AreEqual(6, policy.SessionInterstitials);
            Assert.IsFalse(policy.CanShowInterstitial(100, true, 9999));
        }
        [TestCase(209.99, false)] [TestCase(210, true)]
        public void RewardCooldown(double now, bool allowed)
        { policy.RecordRewarded(150); Assert.AreEqual(allowed, policy.CanShowInterstitial(8, true, now)); }
        [Test] public void OverridesAreUsed()
        {
            config.firstInterstitialLevel = 10; config.levelsBetweenInterstitials = 4;
            config.interstitialIntervalSeconds = 200; config.maxInterstitialsPerSession = 1;
            config.rewardedCooldownSeconds = 90;
            Assert.IsFalse(policy.CanShowInterstitial(10, true, 200));
            Win(1); Assert.IsFalse(policy.CanShowInterstitial(9, true, 200));
            Assert.IsFalse(policy.CanShowInterstitial(10, true, 199));
            policy.RecordRewarded(150); Assert.IsFalse(policy.CanShowInterstitial(10, true, 239));
            Assert.IsTrue(policy.CanShowInterstitial(10, true, 240));
            policy.RecordInterstitial(240); Win(4);
            Assert.IsFalse(policy.CanShowInterstitial(10, true, 999));
        }
        [Test] public void NewSessionResetsCap()
        { policy.RecordInterstitial(150); policy = new AdPolicy(config, 1000); Win(3); Assert.IsTrue(policy.CanShowInterstitial(8, true, 1150)); }
    }
}
