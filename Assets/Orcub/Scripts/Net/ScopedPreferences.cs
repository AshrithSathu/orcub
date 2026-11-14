using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
namespace Orcub.Net
{
    public static class ScopedPreferences
    {
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void OrcubClearPreferences();
#endif
        public static void Clear()
        {
#if UNITY_IOS && !UNITY_EDITOR
            OrcubClearPreferences();
#elif UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            var domain = Environment.GetFolderPath(Environment.SpecialFolder.Personal) + "/Library/Preferences/" + (Application.isEditor ? "unity." + Application.companyName + "." + Application.productName : Application.identifier) + ".plist";
            // Unity has no PlayerPrefs key enumeration API. Read only our app domain on macOS.
            var p = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo("/usr/bin/plutil") { Arguments = "-convert xml1 -o - \"" + domain + "\"", RedirectStandardOutput = true, UseShellExecute = false } };
            p.Start(); var xml = p.StandardOutput.ReadToEnd(); p.WaitForExit();
            if (p.ExitCode == 0) { var doc = new System.Xml.XmlDocument(); doc.LoadXml(xml); foreach (System.Xml.XmlNode key in doc.SelectNodes("/plist/dict/key")) if (key.InnerText.StartsWith("orcub.", StringComparison.Ordinal)) PlayerPrefs.DeleteKey(key.InnerText); }
#endif
            PlayerPrefs.Save();
        }
    }
}
