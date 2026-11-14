using NUnit.Framework;
using Orcub.QA;

namespace Orcub.Tests.QA
{
    public sealed class LevelQATests
    {
        [Test]
        public void QA_AllLevelsSolvable()
        {
            var report = LevelQA.Run();
            TestContext.WriteLine(report.Summary);
            Assert.That(report.HardFailures, Is.Empty, string.Join("\n", report.HardFailures));
        }
    }
}
