using System;
using Newtonsoft.Json.Linq;
using Orcub.Core;
using Orcub.Game;
using UnityEngine;

namespace Orcub.Net
{
    public enum SaveDiff { Same, LocalAhead, Fresh, Conflict }

    public static class SaveSnapshot
    {
        /// <summary>Frontier level (1-based) and total earned stars of a snapshot.</summary>
        public static SaveSummary Summary(JObject s)
        {
            int stars = 0;
            if (s?["stars"] is JObject st) foreach (var p in st.Properties()) stars += (int)p.Value;
            return new SaveSummary(((int?)s?["highest_level"] ?? 0) + 1, stars);
        }

        /// <summary>
        /// How a cloud snapshot relates to this phone. Fresh: this phone has no progress (take the cloud quietly).
        /// LocalAhead/Same: nothing in the cloud is missing here (normal autosave lag; merge quietly).
        /// Conflict: the cloud holds progress this phone lacks while this phone has its own progress.
        /// </summary>
        public static SaveDiff Compare(JObject local, JObject cloud)
        {
            if (cloud == null) return SaveDiff.LocalAhead;
            int lh = (int?)local["highest_level"] ?? 0, ch = (int?)cloud["highest_level"] ?? 0;
            var ls = local["stars"] as JObject ?? new JObject();
            bool cloudHasMore = ch > lh;
            if (cloud["stars"] is JObject cs)
                foreach (var p in cs.Properties()) if ((int)p.Value > ((int?)ls[p.Name] ?? 0)) cloudHasMore = true;
            if (!cloudHasMore) return lh > ch || ls.Count > 0 ? SaveDiff.LocalAhead : SaveDiff.Same;
            if (lh == 0 && !ls.HasValues) return SaveDiff.Fresh;
            var a = Summary(local); var b = Summary(cloud);
            return a.Level == b.Level && a.Stars == b.Stars ? SaveDiff.Same : SaveDiff.Conflict;
        }

        public static JObject Capture()
        {
            var stars = new JObject();
            for (int i = 0; i <= Math.Min(100000, Math.Max(LevelLibrary.Count, Progress.HighestUnlocked)); i++) if (Progress.BestStars(i) > 0) stars[i.ToString()] = Progress.BestStars(i);
            var workshop = new JObject { ["stars_spent"] = StarBank.Spent };
            foreach (var task in Workshop.Tasks) workshop[task.Id] = Workshop.IsRestored(task.Id) ? 1 : 0;
            var skill = new PlayerSkill(); var recent = new JArray();
            foreach (var s in skill.Recent) recent.Add(new JObject { ["won"] = s.Won, ["efficiency"] = s.Efficiency, ["seconds_per_move"] = s.SecondsPerMove, ["boosters"] = s.Boosters, ["continues"] = s.Continues });
            return new JObject { ["daily"] = CaptureDaily(), ["highest_level"] = Progress.HighestUnlocked, ["stars"] = stars, ["workshop"] = workshop,
                ["settings"] = new JObject { ["sound"] = GameSettings.SoundOn, ["haptics"] = GameSettings.HapticsOn },
                ["skill"] = new JObject { ["rating"] = skill.Rating, ["games"] = skill.Games, ["recent"] = recent } };
        }
        static JObject CaptureDaily()
        {
            var json=ClientGateway.DailyCapture?.Invoke();
            return string.IsNullOrEmpty(json) ? null : JObject.Parse(json);
        }
        public static JObject MergeDaily(JObject local, JObject server)
        {
            if(server==null) return (JObject)local?.DeepClone();
            var merged=(JObject)server.DeepClone(); if(local==null) return merged;
            foreach(var key in new[]{"Completed","PendingPuzzleGrants"}) {
                var union=new System.Collections.Generic.SortedSet<string>();
                foreach(var item in local[key] as JArray ?? new JArray()) union.Add((string)item);
                foreach(var item in server[key] as JArray ?? new JArray()) union.Add((string)item);
                merged[key]=new JArray(union);
            }
            foreach(var key in new[]{"HighestUtcDay","LastGiftDay","LastPuzzleDay"})
                if(string.CompareOrdinal((string)local[key],(string)merged[key])>0) merged[key]=local[key].DeepClone();
            foreach(var day in (JArray)merged["PendingPuzzleGrants"])
                if(string.CompareOrdinal((string)day,(string)merged["LastPuzzleDay"])>0) merged["LastPuzzleDay"]=day.DeepClone();
            return merged;
        }
        // On conflict only monotonic progress is replayed; server owns the accepted mutable snapshot.
        public static JObject MergeProgress(JObject local, JObject server)
        {
            if (server == null) return (JObject)local.DeepClone();
            var merged = (JObject)server.DeepClone();
            merged["highest_level"] = Math.Max((int)local["highest_level"], (int)server["highest_level"]);
            var stars = (JObject)merged["stars"];
            foreach (var p in ((JObject)local["stars"]).Properties()) stars[p.Name] = Math.Max((int?)stars[p.Name] ?? 0, (int)p.Value);
            merged["daily"]=MergeDaily(local["daily"] as JObject,server["daily"] as JObject);
            return merged;
        }
        public static void Apply(JObject s)
        {
            if(s["daily"] is JObject daily) ClientGateway.DailyRestore?.Invoke(daily.ToString());
            int oldHighest=Progress.HighestUnlocked;
            PlayerPrefs.SetInt("orcub.highest", (int)s["highest_level"]); PlayerPrefs.SetInt("orcub.level", (int)s["highest_level"]);
            for (int i = 0; i <= Math.Min(100000, Math.Max(LevelLibrary.Count, Math.Max(oldHighest, (int)s["highest_level"]))); i++) PlayerPrefs.SetInt("orcub.stars." + i, (int?)s["stars"]?[i.ToString()] ?? 0);
            PlayerPrefs.SetInt("orcub.stars.spent", (int?)s["workshop"]?["stars_spent"] ?? 0);
            foreach (var task in Workshop.Tasks) PlayerPrefs.SetInt("orcub.workshop." + task.Id, (int?)s["workshop"]?[task.Id] ?? 0);
            PlayerPrefs.SetInt("orcub.sound", (bool)s["settings"]["sound"] ? 1 : 0); PlayerPrefs.SetInt("orcub.haptics", (bool)s["settings"]["haptics"] ? 1 : 0);
            var recent = new JArray();
            foreach (var r in (JArray)s["skill"]["recent"]) recent.Add(new JObject { ["Won"] = r["won"], ["Efficiency"] = r["efficiency"], ["SecondsPerMove"] = r["seconds_per_move"], ["Boosters"] = r["boosters"], ["Continues"] = r["continues"] });
            PlayerPrefs.SetString(PlayerSkill.DefaultPrefix + "state", new JObject { ["Rating"] = s["skill"]["rating"], ["Games"] = s["skill"]["games"], ["Recent"] = recent }.ToString());
            PlayerPrefs.Save(); GameSettings.Apply();
        }
    }
}
