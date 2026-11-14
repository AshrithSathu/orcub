namespace Orcub.Net
{
    public static class AnalyticsConsent
    {
        public static bool Allowed => UnityEngine.PlayerPrefs.GetInt("Ads.AnalyticsConsent",0)==1;
        public static event System.Action<bool> Changed;
        public static void Resolve(bool allowed) { UnityEngine.PlayerPrefs.SetInt("Ads.AnalyticsConsent",allowed?1:0); UnityEngine.PlayerPrefs.Save(); Changed?.Invoke(allowed); }
        // These first-party proofs are essential to server-authoritative gameplay rewards.
        public static bool BackendAllowed(string kind) => Allowed || kind=="error" || kind=="level_start" || kind=="level_end";
    }
}
