using UnityEditor;

namespace Orcub.EditorTools
{
    /// <summary>Sprite import settings for the daily / tutorial kit (build_feature_kit.py writes no .meta files).</summary>
    public sealed class FeatureArtImporter : AssetPostprocessor
    {
        const string Dir = "Assets/Orcub/Art/Features/";

        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(Dir) || !assetPath.EndsWith(".png")) return;
            var ti = (TextureImporter)assetImporter;
            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Single;
            ti.alphaIsTransparency = true;
            ti.mipmapEnabled = false;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.spritePixelsPerUnit = 100;
        }
    }
}
