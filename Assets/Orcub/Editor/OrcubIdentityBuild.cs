#if UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
namespace Orcub.EditorTools
{
    public static class OrcubIdentityBuild
    {
        [PostProcessBuild(101)]
        public static void Patch(BuildTarget target, string path)
        {
            if (target != BuildTarget.iOS) return;
            var config = new PlistDocument(); config.ReadFromFile("Assets/GoogleService-Info.plist");
            var plist = new PlistDocument(); var file = Path.Combine(path, "Info.plist"); plist.ReadFromFile(file);
            var types = plist.root.values.TryGetValue("CFBundleURLTypes", out var existing) ? existing.AsArray() : plist.root.CreateArray("CFBundleURLTypes");
            plist.root.SetBoolean("FIREBASE_ANALYTICS_COLLECTION_ENABLED", true);
            types.AddDict().CreateArray("CFBundleURLSchemes").AddString(config.root["REVERSED_CLIENT_ID"].AsString());
            plist.WriteToFile(file);
            var projectFile = PBXProject.GetPBXProjectPath(path); var project = new PBXProject(); project.ReadFromFile(projectFile);
            project.AddFrameworkToProject(project.GetUnityFrameworkTargetGuid(), "Security.framework", false);
            project.SetBuildProperty(project.GetUnityMainTargetGuid(), "DEBUG_INFORMATION_FORMAT", "dwarf-with-dsym"); project.WriteToFile(projectFile);
        }
    }
}
#endif
