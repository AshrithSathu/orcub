using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Orcub.Game;
using Orcub.Net;

namespace Orcub.Tests
{
    /// <summary>Cloud-restore decision rules and account display helpers.</summary>
    public class AccountFlowTests
    {
        static JObject Save(int highest, params (int level, int stars)[] stars)
        {
            var s = new JObject();
            foreach (var (level, n) in stars) s[level.ToString()] = n;
            return new JObject { ["highest_level"] = highest, ["stars"] = s };
        }

        [Test] public void FreshPhoneTakesCloudQuietly()
            => Assert.AreEqual(SaveDiff.Fresh, SaveSnapshot.Compare(Save(0), Save(41, (0, 3), (1, 2))));

        [Test] public void UnsavedLocalProgressIsNotAConflict()
            => Assert.AreEqual(SaveDiff.LocalAhead, SaveSnapshot.Compare(Save(12, (0, 3), (1, 3)), Save(11, (0, 3), (1, 2))));

        [Test] public void IdenticalSavesAreSame()
            => Assert.AreEqual(SaveDiff.Same, SaveSnapshot.Compare(Save(0), Save(0)));

        [Test] public void CloudAheadOfPlayedPhoneAsksThePlayer()
            => Assert.AreEqual(SaveDiff.Conflict, SaveSnapshot.Compare(Save(11, (0, 3)), Save(41, (0, 3), (5, 2))));

        [Test] public void DivergedStarsAskThePlayer()
            => Assert.AreEqual(SaveDiff.Conflict, SaveSnapshot.Compare(Save(5, (0, 1), (1, 3)), Save(5, (0, 3), (1, 1), (2, 2))));

        [Test] public void SummaryIsFrontierLevelAndStarTotal()
        {
            var s = SaveSnapshot.Summary(Save(41, (0, 3), (1, 2)));
            Assert.AreEqual(42, s.Level);
            Assert.AreEqual(5, s.Stars);
        }

        [Test] public void FurtherAheadComparesLevelThenStars()
        {
            Assert.IsTrue(new SaveSummary(42, 10).AheadOf(new SaveSummary(12, 30)));
            Assert.IsTrue(new SaveSummary(12, 31).AheadOf(new SaveSummary(12, 30)));
            Assert.IsFalse(new SaveSummary(12, 30).AheadOf(new SaveSummary(12, 30)));
        }

        [Test] public void EmailIsMasked()
        {
            Assert.AreEqual("a••••@gmail.com", SettingsScreen.MaskEmail("alex@gmail.com"));
            Assert.AreEqual("Google account", SettingsScreen.MaskEmail(null));
        }
    }
}
