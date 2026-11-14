#if UNITY_IOS
using System.IO;
using System.Linq;
using UnityEditor.Build;
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
            var natives=Directory.GetFiles(path,"OrcubIdentity.mm",SearchOption.AllDirectories);
            if(natives.Length==0) throw new BuildFailedException("OrcubIdentity.mm missing from export");
            foreach (var native in natives)
            {
                if((File.GetAttributes(native)&FileAttributes.ReparsePoint)!=0) throw new BuildFailedException("Identity patch requires copied sources; disable Symlink Sources");
                var source = File.ReadAllText(native).Replace("\r\n","\n");
                if(!source.Contains("SecItemDelete((__bridge CFDictionaryRef)q);\n    if (!value || !strlen(value)) return true;") && !source.Contains("OSStatus updated = SecItemUpdate")) throw new BuildFailedException("Identity keychain patch source drift");
                if(!source.Contains("error ? \"\" : result.user.idToken.tokenString.UTF8String") && !source.Contains("result.user.idToken.tokenString.UTF8String ?: \"\"")) throw new BuildFailedException("Identity null-token patch source drift");
                source = source.Replace("SecItemDelete((__bridge CFDictionaryRef)q);\n    if (!value || !strlen(value)) return true;", "if (!value || !strlen(value)) { OSStatus deleted = SecItemDelete((__bridge CFDictionaryRef)q); return deleted == errSecSuccess || deleted == errSecItemNotFound; }\n    NSData *data = [[NSString stringWithUTF8String:value] dataUsingEncoding:NSUTF8StringEncoding];\n    OSStatus updated = SecItemUpdate((__bridge CFDictionaryRef)q, (__bridge CFDictionaryRef)@{(__bridge id)kSecValueData:data});\n    if (updated != errSecItemNotFound) return updated == errSecSuccess;");
                source = source.Replace("error ? \"\" : result.user.idToken.tokenString.UTF8String", "error ? \"\" : (result.user.idToken.tokenString.UTF8String ?: \"\")");
                File.WriteAllText(native, source);
            }
            var config = new PlistDocument(); config.ReadFromFile("Assets/GoogleService-Info.plist");
            var plist = new PlistDocument(); var file = Path.Combine(path, "Info.plist"); plist.ReadFromFile(file);
            var types = plist.root.values.TryGetValue("CFBundleURLTypes", out var existing) ? existing.AsArray() : plist.root.CreateArray("CFBundleURLTypes");
            plist.root.SetBoolean("FIREBASE_ANALYTICS_COLLECTION_ENABLED", false);
            plist.root.SetBoolean("FirebaseCrashlyticsCollectionEnabled", false);
            string scheme=config.root["REVERSED_CLIENT_ID"].AsString();
            if(!types.values.Any(v=>v.AsDict().values.TryGetValue("CFBundleURLSchemes",out var schemes) && schemes.AsArray().values.Any(x=>x.AsString()==scheme))) types.AddDict().CreateArray("CFBundleURLSchemes").AddString(scheme);
            plist.WriteToFile(file);
            var projectFile = PBXProject.GetPBXProjectPath(path); var project = new PBXProject(); project.ReadFromFile(projectFile);
            project.AddFrameworkToProject(project.GetUnityFrameworkTargetGuid(), "Security.framework", false);
            project.SetBuildProperty(project.GetUnityMainTargetGuid(), "DEBUG_INFORMATION_FORMAT", "dwarf-with-dsym"); project.WriteToFile(projectFile);
        }
    }
}
#endif
