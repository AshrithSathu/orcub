using Orcub.Ads;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Orcub.EditorTools
{
    public sealed class OrcubAdsBuild : IPreprocessBuildWithReport
    {
        public int callbackOrder => -1000;
        public void OnPreprocessBuild(BuildReport report) => SyncSettings();

        // EDM generates the Podfile at order 40 and installs pods at order 50.
        [UnityEditor.Callbacks.PostProcessBuild(45)]
        public static void PatchPodfile(BuildTarget target, string path)
        {
            if (target != BuildTarget.iOS) return;
            var podfile = System.IO.Path.Combine(path, "Podfile");
            if (!System.IO.File.Exists(podfile)) return;
            const string hook = @"
post_install do |installer|
  installer.pods_project.targets.each do |target|
    target.build_configurations.each do |config|
      config.build_settings['IPHONEOS_DEPLOYMENT_TARGET'] = '15.0'
    end
  end
end
";
            var text = System.IO.File.ReadAllText(podfile);
            if (!text.Contains("post_install do |installer|")) System.IO.File.AppendAllText(podfile, hook);
            else Debug.LogWarning("Existing Podfile post_install hook: verify every pod deployment target is at least iOS 15.0");
        }

        [MenuItem("Orcub/Sync Ads Settings")]
        public static void SyncSettings()
        {
            var config = AdsConfig.Load();
            var type = System.Type.GetType("GoogleMobileAds.Editor.GoogleMobileAdsSettings, GoogleMobileAds.Editor", true);
            const string path = "Assets/GoogleMobileAds/Resources/GoogleMobileAdsSettings.asset";
            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
            if (asset == null)
            {
                System.IO.Directory.CreateDirectory("Assets/GoogleMobileAds/Resources");
                asset = ScriptableObject.CreateInstance(type);
                AssetDatabase.CreateAsset(asset, path);
            }
            var settings = new SerializedObject(asset);
            settings.FindProperty("adMobIOSAppId").stringValue = OrcubBuild.ReleaseBuild ? config.liveAppId : config.iosAppId;
            settings.FindProperty("userTrackingUsageDescription").stringValue = config.trackingUsageDescription;
            settings.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
        }
    }
}
