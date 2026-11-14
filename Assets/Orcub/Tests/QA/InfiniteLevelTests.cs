using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Orcub.Core;
using UnityEngine;

namespace Orcub.Tests.QA
{
    public sealed class InfiniteLevelTests
    {
        [Test]
        public void DeterministicAcrossThreadContexts()
        {
            foreach (int index in new[] { 200, 219, 999, 1999, 1000000 })
                for (int v = 0; v < 5; v++)
                {
                    int variant = v;
                    string first = JsonUtility.ToJson(InfiniteGenerator.Generate(index, v));
                    Assert.That(Task.Run(() => JsonUtility.ToJson(InfiniteGenerator.Generate(index, variant))).Result, Is.EqualTo(first));
                }
        }
        [Test]
        public void BossBreatherAndSoftCap()
        {
            var curve = new InfiniteCurve();
            for (int index = 200; index < 2000; index++)
            {
                Assert.That(InfiniteGenerator.RingCount(index, 4, curve), Is.InRange(4, curve.MaxRings));
                if (index % 20 == 19)
                {
                    Assert.That(InfiniteGenerator.Generate(index, 0).is_boss, Is.True);
                    Assert.That(InfiniteGenerator.IsBreather(index + 1), Is.True);
                    Assert.That(InfiniteGenerator.RingCount(index + 1, 0, curve), Is.LessThan(InfiniteGenerator.RingCount(index, 0, curve)));
                }
            }
        }
        [Test]
        public void CacheRoundTripAndEvictionPreserveFrozenTuning()
        {
            string root = Path.Combine(Path.GetTempPath(), "orcub-level-test-" + Guid.NewGuid());
            try
            {
                var cache = new InfiniteLevelCache(root, new InfiniteCurve { CacheEntries = 5 });
                string first = JsonUtility.ToJson(new LevelVariants { choices = cache.PrepareAsync(200).Result });
                var changed = new InfiniteCurve { BaseRings = 8, CacheEntries = 5 };
                cache.Configure(changed);
                for (int i = 201; i < 208; i++) cache.PrepareAsync(i).Wait();
                Assert.That(File.Exists(Path.Combine(root, "levels/v1/200.json")), Is.False);
                var restarted = new InfiniteLevelCache(root, changed);
                Assert.That(JsonUtility.ToJson(new LevelVariants { choices = restarted.PrepareAsync(200).Result }), Is.EqualTo(first));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        [Test]
        public void PrefetchReturnsWithoutWaitingForWorker()
        {
            string root = Path.Combine(Path.GetTempPath(), "orcub-prefetch-" + Guid.NewGuid());
            var cache = new InfiniteLevelCache(root, new InfiniteCurve());
            var timer = System.Diagnostics.Stopwatch.StartNew();
            cache.Prefetch(200);
            Assert.That(timer.ElapsedMilliseconds, Is.LessThan(100));
            cache.PrepareAsync(205).Wait();
            Directory.Delete(root, true);
        }
        [Test]
        public void CuratedPackRemainsTheSourceForAllChoices()
        {
            var pack = LevelPack.FromJson(Resources.Load<TextAsset>("Orcub/levels").text);
            for (int index = 0; index < 200; index++)
            {
                Assert.That(JsonUtility.ToJson(LevelLibrary.Get(index)), Is.EqualTo(JsonUtility.ToJson(pack.levels[index])));
                for (int v = 0; v < 5; v++) Assert.That(JsonUtility.ToJson(LevelLibrary.Get(index, v)), Is.EqualTo(JsonUtility.ToJson(pack.variants[index].choices[v])));
            }
        }
        [Test]
        public void SampleGenerationTimings()
        {
            var samples = new System.Collections.Generic.List<double>();
            for (int index = 200; index < 2000; index += 7)
                for (int v = 0; v < 5; v++)
                {
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    InfiniteGenerator.Generate(index, v);
                    samples.Add(timer.Elapsed.TotalMilliseconds);
                }
            samples.Sort();
            TestContext.WriteLine($"{samples.Count} generated: mean {samples.Average():F3} ms; p95 {samples[(int)(samples.Count * .95)]:F3} ms; max {samples.Last():F3} ms");
        }
    }
}
