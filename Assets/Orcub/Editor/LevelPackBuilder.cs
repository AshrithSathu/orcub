using System.IO;
using Orcub.Core;
using UnityEditor;
using UnityEngine;

namespace Orcub.EditorTools
{
    /// <summary>Preserves chapter 1 and builds five solver-validated choices for every later saga slot.</summary>
    public static class LevelPackBuilder
    {
        public const string PackPath = "Assets/Orcub/Resources/Orcub/levels.json";
        const int LevelCount = 200;

        [MenuItem("Orcub/Levels/Build Level Pack")]
        public static void Build() => Build(LevelCount, 1234);

        public static string Build(int count, int baseSeed)
        {
            if (!File.Exists(PackPath)) throw new System.InvalidOperationException("The shipped chapter 1 pack is required.");
            var previous = LevelPack.FromJson(File.ReadAllText(PackPath));
            var levels = new LevelData[count];
            var variants = new LevelVariants[count];
            for (int slot = 0; slot < count; slot++)
            {
                if (slot < 20)
                {
                    levels[slot] = previous.levels[slot];
                    variants[slot] = previous.variants[slot];
                    continue;
                }
                var choices = new LevelData[5];
                for (int variant = 0; variant < 5; variant++) choices[variant] = SagaGenerator.Generate(slot, variant, baseSeed);
                levels[slot] = choices[0];
                variants[slot] = new LevelVariants { choices = choices };
            }
            var pack = new LevelPack { version = 3, levels = levels, variants = variants };
            File.WriteAllText(PackPath, pack.ToJson());
            AssetDatabase.ImportAsset(PackPath);
            LevelLibrary.Reload();
            string summary = $"level pack: {count} levels, {count * 5} validated variants, {new FileInfo(PackPath).Length} bytes";
            Debug.Log("[Orcub] " + summary);
            return summary;
        }
    }
}
