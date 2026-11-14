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
            foreach (var native in Directory.GetFiles(path, "OrcubIdentity.mm", SearchOption.AllDirectories))
            {
                var source = File.ReadAllText(native);
                source = source.Replace("SecItemDelete((__bridge CFDictionaryRef)q);\n    if (!value || !strlen(value)) return true;", "if (!value || !strlen(value)) { OSStatus deleted = SecItemDelete((__bridge CFDictionaryRef)q); return deleted == errSecSuccess || deleted == errSecItemNotFound; }\n    NSData *data = [[NSString stringWithUTF8String:value] dataUsingEncoding:NSUTF8StringEncoding];\n    OSStatus updated = SecItemUpdate((__bridge CFDictionaryRef)q, (__bridge CFDictionaryRef)@{(__bridge id)kSecValueData:data});\n    if (updated != errSecItemNotFound) return updated == errSecSuccess;");
                source = source.Replace("error ? \"\" : result.user.idToken.tokenString.UTF8String", "error ? \"\" : (result.user.idToken.tokenString.UTF8String ?: \"\")");
                File.WriteAllText(native, source);
            }
            var config = new PlistDocument(); config.ReadFromFile("Assets/GoogleService-Info.plist");
            var plist = new PlistDocument(); var file = Path.Combine(path, "Info.plist"); plist.ReadFromFile(file);
            var types = plist.root.values.TryGetValue("CFBundleURLTypes", out var existing) ? existing.AsArray() : plist.root.CreateArray("CFBundleURLTypes");
            plist.root.SetBoolean("FIREBASE_ANALYTICS_COLLECTION_ENABLED", false);
            plist.root.SetBoolean("FirebaseCrashlyticsCollectionEnabled", false);
            types.AddDict().CreateArray("CFBundleURLSchemes").AddString(config.root["REVERSED_CLIENT_ID"].AsString());
            plist.WriteToFile(file);
            var projectFile = PBXProject.GetPBXProjectPath(path); var project = new PBXProject(); project.ReadFromFile(projectFile);
            project.AddFrameworkToProject(project.GetUnityFrameworkTargetGuid(), "Security.framework", false);
            project.SetBuildProperty(project.GetUnityMainTargetGuid(), "DEBUG_INFORMATION_FORMAT", "dwarf-with-dsym"); project.WriteToFile(projectFile);
        }
    }
}
#endif
