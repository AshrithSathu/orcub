using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Orcub.Net;

namespace Orcub.Tests
{
    public class ClientTests
    {
        string directory, path;
        [SetUp] public void Setup() { directory = Path.Combine(Path.GetTempPath(), "orcub-tests-" + Guid.NewGuid()); Directory.CreateDirectory(directory); path = Path.Combine(directory, "events.json"); }
        [TearDown] public void Cleanup() => Directory.Delete(directory, true);
        [Test] public async Task QueueSurvivesRestartAndRetryWithSameIds()
        {
            var q = new EventQueue(path); q.Add("session_start", new { session_id = Guid.NewGuid().ToString() });
            await q.Drain(); var first = q.Batch(); var restarted = new EventQueue(path);
            Assert.AreEqual(first[0].ToString(), restarted.Batch()[0].ToString());
            restarted.Add("error", new { code = "test", context = "test" }); restarted.Remove(first);
            await restarted.Drain(); Assert.AreEqual(1, new EventQueue(path).Count);
        }
        [Test] public async Task GzipUsesExactContractEnvelope()
        {
            var q = new EventQueue(path); q.Add("ad_paid", new { placement = "rewarded_continue", network = "admob", value_micros = 1234, currency = "USD", precision = "estimated" });
            await q.Drain();
            using var source = new MemoryStream(EventQueue.Serialize("player", q.Batch()));
            using var gzip = new GZipStream(source, CompressionMode.Decompress); using var reader = new StreamReader(gzip);
            var json = JObject.Parse(reader.ReadToEnd());
            CollectionAssert.AreEquivalent(new[] { "player_id", "events" }, json.Properties().Select(p => p.Name));
            Assert.AreEqual(1234, (int)json["events"][0]["data"]["value_micros"]);
            Assert.True(Guid.TryParse((string)json["events"][0]["event_id"], out _));
        }
        [Test] public void CorruptQueueDoesNotBreakStartup() { File.WriteAllText(path, "broken"); Assert.AreEqual(0, new EventQueue(path).Count); }
        [Test] public async Task ConsentHoldsAndReleasesOptionalEventsWithoutStarvingProofs()
        {
            AnalyticsConsent.Resolve(false); var q=new EventQueue(path);
            q.Add("session_start",new { session_id=Guid.NewGuid().ToString() }); q.Add("level_end",new { level=1 });
            Assert.AreEqual(1,q.Batch(AnalyticsConsent.BackendAllowed).Length); Assert.AreEqual(2,q.Count);
            AnalyticsConsent.Resolve(true); Assert.AreEqual(2,q.Batch(AnalyticsConsent.BackendAllowed).Length);
            AnalyticsConsent.Resolve(false); await q.Stop();
        }
        [Test] public void ParseableCorruptQueueSkipsMissingAndDuplicateIds()
        {
            var id=Guid.NewGuid().ToString(); File.WriteAllText(path,"[{event_id:null},{event_id:'"+id+"'},{event_id:'"+id+"'}]");
            Assert.AreEqual(1,new EventQueue(path).Count);
        }
        [Test] public void OversizeEventIsRejected() { var q = new EventQueue(path); q.Add("bad", new { text = new string('x', EventQueue.MaxBatchBytes) }); Assert.AreEqual(0, q.Count); }
        [Test] public void ConflictNeverRestoresSpentConsumables()
        {
            var local = JObject.Parse("{highest_level:8,stars:{'0':3},inventory:{hint:8},lives:5,settings:{sound:true},workshop:{stars_spent:0},skill:{rating:1000}}");
            var server = JObject.Parse("{highest_level:4,stars:{'0':1,'1':2},inventory:{hint:1},lives:2,settings:{sound:false},workshop:{stars_spent:3},skill:{rating:1100}}");
            var merged = SaveSnapshot.MergeProgress(local, server);
            Assert.AreEqual(8, (int)merged["highest_level"]); Assert.AreEqual(3, (int)merged["stars"]["0"]); Assert.AreEqual(2, (int)merged["stars"]["1"]);
            foreach (var key in new[] { "inventory", "lives", "settings", "workshop", "skill" }) Assert.True(JToken.DeepEquals(server[key], merged[key]), key);
            Assert.AreEqual(1, (int)server["stars"]["0"], "Inputs remain unchanged");
        }
        [Test] public void FirstCloudSaveKeepsLocalSnapshot() { var local = JObject.Parse("{highest_level:2,stars:{}}"); Assert.True(JToken.DeepEquals(local, SaveSnapshot.MergeProgress(local, null))); }
    }
}
