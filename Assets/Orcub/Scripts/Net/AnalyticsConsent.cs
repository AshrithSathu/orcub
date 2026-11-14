namespace Orcub.Net
{
    // Denied until a successful privacy resolution; errors and startup never imply consent.
    public static class AnalyticsConsent
    {
        public static bool Allowed { get; private set; }
        public static event System.Action<bool> Changed;
        public static void Resolve(bool allowed) { Allowed = allowed; Changed?.Invoke(allowed); }
        public static bool BackendAllowed(string kind) => Allowed || kind == "error";
    }
}
