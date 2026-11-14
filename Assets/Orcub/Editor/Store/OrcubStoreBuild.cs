#if UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.iOS.Xcode;

namespace Orcub.EditorTools
{
    public static class OrcubStoreBuild
    {
        [UnityEditor.Callbacks.PostProcessBuild(60)]
        public static void Configure(BuildTarget target, string path)
        {
            if (target != BuildTarget.iOS) return;
            File.Copy("Assets/Orcub/Scripts/Store/Orcub.storekit", Path.Combine(path, "Orcub.storekit"), true);
            var projectPath = PBXProject.GetPBXProjectPath(path);
            var project = new PBXProject(); project.ReadFromFile(projectPath);
            project.AddFile("Orcub.storekit", "Orcub.storekit", PBXSourceTree.Source);
            var framework = project.GetUnityFrameworkTargetGuid();
            // Swift source is kept under Editor so Unity cannot also auto-add it as a plug-in.
            File.Copy("Assets/Orcub/Editor/Store/OrcubStore.swift", Path.Combine(path, "OrcubStore.swift"), true);
            var swift = project.FindFileGuidByProjectPath("OrcubStore.swift");
            if (string.IsNullOrEmpty(swift)) swift = project.AddFile("OrcubStore.swift", "OrcubStore.swift", PBXSourceTree.Source);
            project.AddFileToBuild(framework, swift);
            project.AddFrameworkToProject(framework, "StoreKit.framework", false);
            project.SetBuildProperty(framework, "SWIFT_VERSION", "5.0");
            project.SetBuildProperty(framework, "CLANG_ENABLE_MODULES", "YES");
            project.SetBuildProperty(project.GetUnityMainTargetGuid(), "ALWAYS_EMBED_SWIFT_STANDARD_LIBRARIES", "YES");
            project.WriteToFile(projectPath);
        }
    }
}
#endif
