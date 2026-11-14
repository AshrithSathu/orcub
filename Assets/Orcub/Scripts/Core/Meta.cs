using UnityEngine;

namespace Orcub.Core
{
    public static class StarBank
    {
        const string SpentKey = "orcub.stars.spent";

        public static int TotalEarned
        {
            get
            {
                int total = 0;
                for (int i = 0; i < Mathf.Max(LevelLibrary.CuratedCount, Progress.HighestUnlocked); i++) total += Progress.BestStars(i);
                return total;
            }
        }

        public static int Spent => PlayerPrefs.GetInt(SpentKey, 0);
        public static int Available => TotalEarned - Spent;

        public static bool TrySpend(int n)
        {
            if (n < 0 || n > Available) return false;
            PlayerPrefs.SetInt(SpentKey, Spent + n);
            PlayerPrefs.Save();
            return true;
        }
    }

    public sealed class WorkshopTask
    {
        public string Id, Name;
        public int Cost;
    }

    public static class Workshop
    {
        public static readonly WorkshopTask[] Tasks =
        {
            new WorkshopTask { Id = "lamp", Name = "Fix the lamp", Cost = 3 },
            new WorkshopTask { Id = "clock", Name = "Wind the clock", Cost = 6 },
            new WorkshopTask { Id = "toolbox", Name = "Sort the toolbox", Cost = 9 },
            new WorkshopTask { Id = "musicbox", Name = "Tune the music box", Cost = 12 },
        };

        public static bool IsRestored(string id) => PlayerPrefs.GetInt("orcub.workshop." + id, 0) != 0;

        public static WorkshopTask Next
        {
            get
            {
                foreach (var task in Tasks)
                    if (!IsRestored(task.Id)) return task;
                return null;
            }
        }

        public static bool TryRestore(string id)
        {
            var task = Next;
            if (task == null || task.Id != id || !StarBank.TrySpend(task.Cost)) return false;
            PlayerPrefs.SetInt("orcub.workshop." + id, 1);
            PlayerPrefs.Save();
            return true;
        }
    }
}
