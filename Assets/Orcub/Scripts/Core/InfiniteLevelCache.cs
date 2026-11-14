using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace Orcub.Core
{
    /// <summary>One serial worker owns disk IO, search and eviction. Unity paths/config are captured by caller.</summary>
    public sealed class InfiniteLevelCache
    {
        readonly object gate = new object();
        readonly Dictionary<int, LevelData[]> ready = new Dictionary<int, LevelData[]>();
        readonly Dictionary<int, Task<LevelData[]>> pending = new Dictionary<int, Task<LevelData[]>>();
        readonly Dictionary<int, InfiniteCurve> frozen = new Dictionary<int, InfiniteCurve>();
        readonly string directory;
        Task tail = Task.CompletedTask;
        InfiniteCurve curve;
        public Exception LastError { get; private set; }
        public double LastGenerationMilliseconds { get; private set; }

        public InfiniteLevelCache(string root, InfiniteCurve settings)
        {
            directory = Path.Combine(root, "levels", "v" + InfiniteGenerator.Version);
            curve = Copy(settings);
        }
        static InfiniteCurve Copy(InfiniteCurve value)
        {
            value.Validate();
            return new InfiniteCurve { BaseRings = value.BaseRings, MaxRings = value.MaxRings, ChaptersPerRing = value.ChaptersPerRing,
                SpotlightEvery = value.SpotlightEvery, GenerationBudgetMs = value.GenerationBudgetMs, CacheEntries = value.CacheEntries, PrefetchCount = value.PrefetchCount };
        }
        public void Configure(InfiniteCurve settings) { lock (gate) curve = Copy(settings); }
        public bool TryGet(int index, out LevelData[] levels) { lock (gate) return ready.TryGetValue(index, out levels); }
        public Task<LevelData[]> PrepareAsync(int index)
        {
            if (index < 200 || index == int.MaxValue) throw new ArgumentOutOfRangeException(nameof(index));
            lock (gate)
            {
                if (ready.TryGetValue(index, out var levels)) return Task.FromResult(levels);
                if (pending.TryGetValue(index, out var job)) return job;
                var snapshot = Copy(curve);
                // Chain instead of flooding the pool when map screens request many indices.
                var next = tail.ContinueWith(_ => Load(index, snapshot), TaskScheduler.Default);
                pending[index] = next;
                tail = next.ContinueWith(_ => { lock (gate) pending.Remove(index); }, TaskScheduler.Default);
                return next;
            }
        }
        public void Prefetch(int index)
        {
            for (int offset = 0; offset <= 5 && (long)index + offset < int.MaxValue; offset++)
                if (index + offset >= 200) _ = PrepareAsync(index + offset);
        }
        LevelData[] Load(int index, InfiniteCurve snapshot)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var levels = new LevelData[5];
            try
            {
                Directory.CreateDirectory(directory);
                string pin = Path.Combine(directory, index + ".curve.json");
                // Small permanent recipe survives LRU board eviction. Never evict an identity's recipe.
                if (File.Exists(pin)) snapshot = JsonUtility.FromJson<InfiniteCurve>(File.ReadAllText(pin));
                else AtomicWrite(pin, JsonUtility.ToJson(snapshot));
                snapshot.Validate();
                lock (gate) frozen[index] = snapshot;
                string path = Path.Combine(directory, index + ".json");
                if (File.Exists(path))
                {
                    try
                    {
                        var stored = JsonUtility.FromJson<LevelVariants>(File.ReadAllText(path));
                        if (stored?.choices?.Length == 5 && Enumerable.Range(0, 5).All(v =>
                            JsonUtility.ToJson(stored.choices[v]) == JsonUtility.ToJson(InfiniteGenerator.Construct(index, v, snapshot)))) levels = stored.choices;
                        else throw new InvalidDataException("Invalid cached board");
                    }
                    catch (ArgumentException) { levels = new LevelData[5]; }
                    catch (InvalidDataException) { levels = new LevelData[5]; }
                }
                if (levels[0] == null)
                {
                    for (int v = 0; v < 5; v++) levels[v] = InfiniteGenerator.Generate(index, v, snapshot);
                    AtomicWrite(path, JsonUtility.ToJson(new LevelVariants { choices = levels }));
                }
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                foreach (var old in new DirectoryInfo(directory).GetFiles("*.json").Where(f => !f.Name.EndsWith(".curve.json"))
                    .OrderByDescending(f => f.LastWriteTimeUtc).Skip(snapshot.CacheEntries)) old.Delete();
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                LastError = error;
                // Read-only/full storage must not prevent play. Keep the session's recipe in memory.
                lock (gate) { if (frozen.TryGetValue(index, out var prior)) snapshot = prior; else frozen[index] = snapshot; }
                for (int v = 0; v < 5; v++) levels[v] = InfiniteGenerator.Generate(index, v, snapshot);
            }
            lock (gate)
            {
                ready[index] = levels;
                // Retain only a bounded hot window; disk recipes preserve historical content.
                foreach (int old in ready.Keys.Where(k => Math.Abs((long)k - index) > 10).ToArray()) ready.Remove(old);
                LastGenerationMilliseconds = clock.Elapsed.TotalMilliseconds;
            }
            return levels;
        }
        static void AtomicWrite(string path, string json)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, json);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }
    }
}
