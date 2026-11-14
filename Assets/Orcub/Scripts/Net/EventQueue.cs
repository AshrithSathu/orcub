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
                    events.AddRange(JArray.Load(reader).OfType<JObject>());
                } }
            catch (Exception) { /* A corrupt queue must not block startup. */ }
            foreach (var e in events) { int n = Encoding.UTF8.GetByteCount(e.ToString(Formatting.None)); sizes[(string)e["event_id"]] = n; bytes += n; }
            Trim();
        }
        public void Add(string kind, object data)
        {
            var e = new JObject { ["event_id"] = Guid.NewGuid().ToString(), ["kind"] = kind,
                ["occurred_at"] = DateTime.UtcNow.ToString("o"), ["data"] = JToken.FromObject(data) };
            if (Encoding.UTF8.GetByteCount(e.ToString(Formatting.None)) > MaxBatchBytes / 2) return;
            if (stopped) return;
            int size = Encoding.UTF8.GetByteCount(e.ToString(Formatting.None));
            sizes[(string)e["event_id"]] = size; bytes += size;
            events.Add(e); Trim(); Persist();
        }
        void Trim()
        {
            while (events.Count > MaxEvents || (events.Count > 0 && bytes > MaxDiskBytes)) { string id = (string)events[0]["event_id"]; bytes -= sizes[id]; sizes.Remove(id); events.RemoveAt(0); }
        }
        public JObject[] Batch()
        {
            var result = new List<JObject>(); int size = 256;
            foreach (var e in events.Take(100))
            {
                size += Encoding.UTF8.GetByteCount(e.ToString(Formatting.None)) + 1;
                if (size > MaxBatchBytes) break;
                result.Add(e);
            }
            return result.ToArray();
        }
        public static byte[] Serialize(string player, JObject[] batch)
        {
            var json = new JObject { ["player_id"] = player, ["events"] = new JArray(batch.Select(e => e.DeepClone())) }.ToString(Formatting.None);
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
