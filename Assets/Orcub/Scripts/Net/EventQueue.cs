using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Orcub.Net
{
    public sealed class EventQueue
    {
        public const int MaxEvents = 2000, MaxDiskBytes = 4 * 1024 * 1024, MaxBatchBytes = 1024 * 1024;
        readonly string path;
        readonly object diskGate = new object();
        JObject[] waiting;
        readonly List<JObject> events = new List<JObject>();
        public string LastAddedId { get; private set; }
        public int Count => events.Count;
        int bytes;
        bool stopped;
        Task writer = Task.CompletedTask;
        readonly System.Collections.Generic.Dictionary<string, int> sizes = new System.Collections.Generic.Dictionary<string, int>();
        public Task Drain() => writer;
        public async Task Stop() { stopped = true; await writer; }
        public EventQueue(string path)
        {
            this.path = path;
            try { if (File.Exists(path))
                {
                    using var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { DateParseHandling = DateParseHandling.None };
                    events.AddRange(JArray.Load(reader).OfType<JObject>().Where(e=>e["event_id"]?.Type==JTokenType.String && Guid.TryParse((string)e["event_id"],out _) && (e["kind"]==null || e["kind"].Type==JTokenType.String)).GroupBy(e=>(string)e["event_id"]).Select(g=>g.First()));
                } }
            catch (Exception) { events.Clear(); try { foreach(var old in Directory.GetFiles(Path.GetDirectoryName(path),Path.GetFileName(path)+".corrupt.*")) File.Delete(old); File.Move(path,path+".corrupt.latest"); } catch(Exception) { } }
            foreach (var e in events) { int n = Encoding.UTF8.GetByteCount(e.ToString(Formatting.None)); sizes[(string)e["event_id"]] = n; bytes += n; }
            Trim();
        }
        public void Add(string kind, object data, bool dev=false)
        => AddCaptured(kind,data,dev,1);
        public void AddCaptured(string kind, object data, bool dev, int consentState)
        {
            var e = new JObject { ["_consent"]=consentState, ["event_id"] = Guid.NewGuid().ToString(), ["kind"] = kind, ["dev"]=dev,
                ["occurred_at"] = DateTime.UtcNow.ToString("o"), ["data"] = JToken.FromObject(data) };
            if (Encoding.UTF8.GetByteCount(e.ToString(Formatting.None)) > MaxBatchBytes / 2) return;
            if (stopped) return;
            int size = Encoding.UTF8.GetByteCount(e.ToString(Formatting.None));
            sizes[(string)e["event_id"]] = size; bytes += size;
            LastAddedId=(string)e["event_id"];
            events.Add(e); Trim(); Persist();
        }
        void Trim()
        {
            while (events.Count > MaxEvents || (events.Count > 0 && bytes > MaxDiskBytes)) { string id = (string)events[0]["event_id"]; bytes -= sizes[id]; sizes.Remove(id); events.RemoveAt(0); }
        }
        public JObject[] Batch(Func<string,bool> allowed = null)
        {
            var result = new List<JObject>(); int size = 256;
            foreach (var e in events.Where(e=>(AnalyticsConsent.Essential((string)e["kind"]) || (int?)e["_consent"]!=0) && (allowed==null || allowed((string)e["kind"]))).Take(100))
            {
                size += Encoding.UTF8.GetByteCount(e.ToString(Formatting.None)) + 1;
                if (size > MaxBatchBytes) break;
                result.Add(e);
            }
            return result.ToArray();
        }
        public async Task IsolateRejected(JObject[] batch, Func<JObject,Task<long>> send, Action<string,long> rejected)
        {
            foreach(var item in batch) {
                long code=await send(item);
                if(code>=200 && code<300) Remove(new[] {item});
                else if(code==400 || code==422) { Remove(new[] {item}); rejected((string)item["kind"],code); }
                else break;
            }
        }
        public static byte[] Serialize(string player, JObject[] batch)
        {
            var json = new JObject { ["player_id"] = player, ["events"] = new JArray(batch.Select(e => { var copy=(JObject)e.DeepClone(); copy.Remove("_consent"); return copy; })) }.ToString(Formatting.None);
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionMode.Compress, true))
            { var bytes = Encoding.UTF8.GetBytes(json); gzip.Write(bytes, 0, bytes.Length); }
            return output.ToArray();
        }
        public void Remove(JObject[] batch)
        {
            if (stopped) return;
            var ids = new HashSet<string>(batch.Select(e => (string)e["event_id"]));
            foreach (var id in ids) if (sizes.TryGetValue(id, out int n)) { bytes -= n; sizes.Remove(id); }
            events.RemoveAll(e => ids.Contains((string)e["event_id"])); Persist();
        }
        public void PurgeOptional() => Remove(events.Where(e=>!AnalyticsConsent.Essential((string)e["kind"])).ToArray());
        public void Clear() { events.Clear(); sizes.Clear(); bytes = 0; Persist(); }
        public void Persist()
        {
            if (stopped) return;
            lock (diskGate)
            {
                waiting = events.ToArray();
                if (writer.IsCompleted) writer = Task.Run(WritePending);
            }
        }
        void WritePending()
        {
            while (true)
            {
                JObject[] snapshot;
                lock (diskGate)
                {
                    snapshot = waiting; waiting = null;
                    if (snapshot == null) { writer = Task.CompletedTask; return; }
                }
                Write(snapshot);
            }
        }
        void Write(JObject[] snapshot)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path + ".tmp", new JArray(snapshot.Select(e => e.DeepClone())).ToString(Formatting.None));
                if (File.Exists(path)) File.Replace(path + ".tmp", path, null); else File.Move(path + ".tmp", path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
