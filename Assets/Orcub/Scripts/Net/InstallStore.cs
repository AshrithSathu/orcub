using System.Runtime.InteropServices;
using UnityEngine;

namespace Orcub.Net
{
    public static class InstallStore
    {
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] static extern string OrcubSecretRead();
        [DllImport("__Internal")] static extern bool OrcubSecretWrite(string value);
#endif
        public static string Read()
        {
#if UNITY_IOS && !UNITY_EDITOR
            return OrcubSecretRead();
#else
            return PlayerPrefs.GetString("orcub.install.credentials", "");
#endif
        }
        public static void Write(string value)
        {
#if UNITY_IOS && !UNITY_EDITOR
            if (!OrcubSecretWrite(value)) throw new System.IO.IOException("Keychain write failed");
#else
            PlayerPrefs.SetString("orcub.install.credentials", value); PlayerPrefs.Save();
#endif
        }
    }
}
