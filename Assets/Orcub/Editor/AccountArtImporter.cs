using UnityEditor;
using UnityEngine;

namespace Orcub.EditorTools
{
    /// <summary>Sprite import settings for the account kit (build_account_kit.py writes no .meta files).</summary>
    public sealed class AccountArtImporter : AssetPostprocessor
    {
        const string Dir = "Assets/Orcub/Art/Account/";

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
            // Flat provider button: 9-slice so its corner radius stays fixed at any width.
            ti.spriteBorder = assetPath.EndsWith("/g_button.png") ? new Vector4(40, 40, 40, 40) : Vector4.zero;
        }
    }
}
