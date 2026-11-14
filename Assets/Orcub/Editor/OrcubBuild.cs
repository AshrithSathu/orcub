using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Callbacks;
using UnityEngine;
#if UNITY_IOS
using UnityEditor.iOS.Xcode;
#endif

namespace Orcub.EditorTools
{
    /// <summary>
    /// Mobile player settings + build entry points.
    ///   Menu:     Orcub/Build iOS (Xcode project), Orcub/Configure Mobile Player Settings
    ///   Batch:    Unity -batchmode -quit -projectPath . -buildTarget iOS -executeMethod Orcub.EditorTools.OrcubBuild.BuildIOS
    /// The Xcode project lands in Builds/iOS; xcodebuild / devicectl take it from there.
    /// </summary>
    public static class OrcubBuild
    {
        public const string BundleId = "com.orcub.puzzle";
        public const string AppleTeamId = "3HB3HKUZ4Q"; // Personal Team (free provisioning)
        public const string IOSOutput = "Builds/iOS-v2";
        public const string AndroidOutput = "Builds/Android/Orcub.apk";

        [MenuItem("Orcub/Configure Mobile Player Settings")]
        public static void ConfigurePlayer()
        {
            PlayerSettings.companyName = "Orcub";
            PlayerSettings.productName = "Orcub";
            PlayerSettings.bundleVersion = "0.1.0";

            // Portrait only.
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
            PlayerSettings.allowedAutorotateToPortrait = true;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = false;
            PlayerSettings.allowedAutorotateToLandscapeRight = false;

            // iOS
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.iOS, BundleId);
            PlayerSettings.iOS.appleDeveloperTeamID = AppleTeamId;
            PlayerSettings.iOS.appleEnableAutomaticSigning = true;
            PlayerSettings.iOS.targetOSVersionString = "15.0";
            PlayerSettings.iOS.requiresFullScreen = true;
            PlayerSettings.iOS.hideHomeButton = false;
            PlayerSettings.iOS.buildNumber = DateTime.UtcNow.ToString("yyMMddHHmm");

            // Android
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, BundleId);
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;

            // Our own splash plays in-game; Unity's splash is off (allowed on all plans since Unity 6).
            PlayerSettings.SplashScreen.show = false;
            PlayerSettings.SplashScreen.showUnityLogo = false;

            // App icon (Unity generates every platform size from the 1024 master).
            var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(OrcubAutomation.IconPath);
            if (icon != null) PlayerSettings.SetIcons(NamedBuildTarget.Unknown, new[] { icon }, IconKind.Any);

            ApplyLaunchScreen();
            AssetDatabase.SaveAssets();
            Debug.Log("[Orcub] mobile player settings configured");
        }

        /// <summary>
        /// iOS launch screen = the splash's first frame (captured from the game), on a matching plum, so the
        /// launch screen -> animated splash handoff is seamless.
        /// </summary>
        public static void ApplyLaunchScreen()
        {
            var ti = (TextureImporter)AssetImporter.GetAtPath(OrcubAutomation.LaunchPath);
            if (ti == null) return;
            ti.textureType = TextureImporterType.Default;
            ti.mipmapEnabled = false;
            ti.npotScale = TextureImporterNPOTScale.None;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.maxTextureSize = 4096;
            ti.SaveAndReimport();
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(OrcubAutomation.LaunchPath);
            PlayerSettings.iOS.SetiPhoneLaunchScreenType(iOSLaunchScreenType.ImageAndBackgroundRelative);
            PlayerSettings.iOS.SetLaunchScreenImage(tex, iOSLaunchScreenImageType.iPhonePortraitImage);
            var settings = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]);
            settings.FindProperty("iOSLaunchScreenBackgroundColor").colorValue = new Color32(0x1F, 0x0A, 0x10, 0xFF);
            settings.FindProperty("iOSLaunchScreenFillPct").floatValue = 100f;
            settings.ApplyModifiedPropertiesWithoutUndo();
        }

        static string[] Scenes() => new[] { OrcubAutomation.ScenePath };

        [MenuItem("Orcub/Build iOS (Xcode project)")]
        public static void BuildIOS()
        {
            ConfigurePlayer();
            Run(new BuildPlayerOptions
            {
                scenes = Scenes(),
                locationPathName = IOSOutput,
                target = BuildTarget.iOS,
                targetGroup = BuildTargetGroup.iOS,
                options = BuildOptions.None,
            });
        }

        [MenuItem("Orcub/Build Android (APK)")]
        public static void BuildAndroid()
        {
            ConfigurePlayer();
            Directory.CreateDirectory(Path.GetDirectoryName(AndroidOutput));
            Run(new BuildPlayerOptions
            {
                scenes = Scenes(),
                locationPathName = AndroidOutput,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None,
            });
        }

        /// <summary>
        /// Clear a previous build folder before building. Unity's own replace step fails with "Directory not empty"
        /// when macOS Finder drops a .DS_Store into the folder mid-delete, so retry a few times.
        /// </summary>
        static void CleanOutput(string path)
        {
            if (!Directory.Exists(path)) return;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    Directory.Delete(path, true);
                    return;
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(250);
                }
            }
            Debug.LogWarning("[Orcub] could not fully clear " + path + "; Unity will try to replace it");
        }

        static void Run(BuildPlayerOptions options)
        {
            if (options.target == BuildTarget.iOS) CleanOutput(options.locationPathName);
            var report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;
            Debug.Log($"[Orcub] build {summary.result}: {summary.outputPath} ({summary.totalSize / (1024 * 1024)} MB, {summary.totalErrors} errors)");
            if (Application.isBatchMode)
                EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 1);
        }

        /// <summary>Info.plist tweaks the Unity settings UI doesn't expose.</summary>
        [PostProcessBuild(100)]
        public static void OnPostprocessBuild(BuildTarget target, string path)
        {
#if UNITY_IOS
            if (target != BuildTarget.iOS) return;
            string plistPath = Path.Combine(path, "Info.plist");
            var plist = new PlistDocument();
            plist.ReadFromFile(plistPath);
            var root = plist.root;
            root.SetBoolean("CADisableMinimumFrameDurationOnPhone", true); // allow 120 Hz on ProMotion iPhones
            root.SetBoolean("ITSAppUsesNonExemptEncryption", false);
            root.SetBoolean("UIStatusBarHidden", true);
            root.SetBoolean("UIViewControllerBasedStatusBarAppearance", false);
            plist.WriteToFile(plistPath);
            Debug.Log("[Orcub] Info.plist patched");
#endif
        }
    }
}
