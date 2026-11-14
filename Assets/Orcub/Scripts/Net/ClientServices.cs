using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Firebase;
using Firebase.Analytics;
using Firebase.Crashlytics;
using Firebase.RemoteConfig;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orcub.Core;
using Orcub.Game;
using UnityEngine;
using UnityEngine.Networking;

namespace Orcub.Net
{
    public sealed class ClientServices : MonoBehaviour
    {
        public static ClientServices Instance { get; private set; }
        public const string ProductionUrl = "https://api-production-08259b35.up.railway.app";
        public static string BaseUrl => Application.isEditor || Debug.isDebugBuild
            ? PlayerPrefs.GetString("orcub.dev.api", ProductionUrl) : ProductionUrl;
        public AccountService Account { get; private set; }
        public string PlayerId => (string)credentials?["player_id"];
        public bool Stopped => stopped;
        public bool Ready => credentials != null;
        JObject credentials, config, remote;
        EventQueue queue;
        bool sending, registering, firebaseReady, sessionOpen, stopped;
        string session;
        double sessionAt, nextFlush, nextSave;
        int failures, errorCount;
        double nextError, nextLink;
        bool linking, renewedInstall;
        string ConfigPath => Path.Combine(Application.persistentDataPath, "orcub-config.json");
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Boot() { if (Instance == null) new GameObject("ClientServices").AddComponent<ClientServices>(); }
        async void Awake()
        {
            Instance = this; DontDestroyOnLoad(gameObject);
            AccountGateway.Boot.Reset();
            string queuePath = Path.Combine(Application.persistentDataPath, "orcub-events.json");
            queue = await Task.Run(() => new EventQueue(queuePath));
            try { var stored = InstallStore.Read(); if (!string.IsNullOrEmpty(stored)) credentials = JObject.Parse(stored); }
            catch (Exception) { Debug.LogWarning("Install credentials unavailable"); }
            try { if (File.Exists(ConfigPath)) config = JObject.Parse(File.ReadAllText(ConfigPath)); } catch (Exception) { }
            ClientGateway.Event = Track;
            ClientGateway.Configure = Configure;
            ClientGateway.Paid = Paid;
            ClientGateway.Save = () => Account?.RequestSave();
            Application.logMessageReceived += OnLog;
            AnalyticsConsent.Changed += ApplyConsent;
            StartSession();
            Account = new AccountService(this);
            _ = InitializeFirebase();
            await Register();
        }
        async Task InitializeFirebase()
        {
            try
            {
                if (await FirebaseApp.CheckAndFixDependenciesAsync() != DependencyStatus.Available || stopped) return;
                firebaseReady = true; AccountGateway.Boot.FirebaseReady = true;
                ApplyConsent(AnalyticsConsent.Allowed);
                Crashlytics.ReportUncaughtExceptionsAsFatal = true;
                var rc = FirebaseRemoteConfig.DefaultInstance;
                await rc.SetDefaultsAsync(new Dictionary<string, object> { ["tuning"] = JsonUtility.ToJson(new Tuning()), ["skill_model"] = JsonUtility.ToJson(PlayerModel.Load()), ["ad_policy"] = "{\"interstitial_min_interval_seconds\":150,\"interstitial_daily_cap\":6,\"rewarded_daily_cap\":20}", ["version"] = "bundled", ["experiments"] = "{}" });
                try { await Account.Initialize(); }
                catch (Exception) { AccountGateway.Boot.NetworkFailed = true; Track("error", new { code = "anonymous_auth_failed", context = "startup" }); }
                if (stopped) return;
                AccountGateway.Boot.AuthDone = true;
                try
                {
                    await rc.FetchAsync(TimeSpan.FromHours(1)); await rc.ActivateAsync();
                    if (stopped) return;
                    remote = new JObject();
                    foreach (var key in new[] { "tuning", "skill_model", "ad_policy" })
                    {
                        var v = rc.GetValue(key);
                        if (v.Source == ValueSource.RemoteValue && !string.IsNullOrEmpty(v.StringValue)) remote[key] = JObject.Parse(v.StringValue);
                    }
                    ApplyCaps();
                }
                catch (Exception) { Track("error", new { code = "remote_config_failed", context = "startup" }); }
            }
            catch (Exception) { AccountGateway.Boot.AuthDone = true; Track("error", new { code = "firebase_init_failed", context = "startup" }); }
        }
        async Task Register()
        {
            if (registering) return;
            registering = true;
            try
            {
                if (!Ready)
                {
                    var culture = CultureInfo.CurrentCulture;
                    var payload = new { install_id = Guid.NewGuid().ToString(), app_version = Application.version,
                        build = string.IsNullOrEmpty(Application.buildGUID) ? "editor" : Application.buildGUID, platform = Application.isEditor ? "editor" : Debug.isDebugBuild ? "ios-dev" : "ios", os_version = SystemInfo.operatingSystem,
                        device_model = SystemInfo.deviceModel, device_ram_mb = SystemInfo.systemMemorySize,
                        screen = new { w = Screen.width, h = Screen.height, dpi = Screen.dpi }, locale = culture.Name,
                        language = culture.TwoLetterISOLanguageName, timezone = TimeZoneInfo.Local.Id, first_open_ts = DateTime.UtcNow.ToString("o") };
                    using var req = await Request("/v1/install", "POST", payload, install: false);
                    if (req.responseCode != 200 && req.responseCode != 201) { AccountGateway.Boot.NetworkFailed = true; nextFlush = Time.realtimeSinceStartupAsDouble + 30; return; }
                    var value = JObject.Parse(req.downloadHandler.text);
                    if (stopped) return;
                    InstallStore.Write(value.ToString(Formatting.None)); credentials = value;
                }
                if (stopped) return;
                await Account.Link();
                if (stopped) return;
                await FetchConfig();
                AccountGateway.Boot.ConfigDone = true;
                await Account.Link();
            }
            catch (Exception) { AccountGateway.Boot.NetworkFailed = true; nextFlush = Time.realtimeSinceStartupAsDouble + 30; }
            finally { registering = false; }
        }
        public async Task<UnityWebRequest> Request(string path, string method, object body = null, string token = null, bool install = true, byte[] bytes = null, string previous = null, string etag = null)
        {
            var req = new UnityWebRequest(BaseUrl + path, method) { downloadHandler = new DownloadHandlerBuffer(), timeout = 15 };
            if (body != null || bytes != null)
            {
                req.uploadHandler = new UploadHandlerRaw(bytes ?? Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(body)));
                req.SetRequestHeader("Content-Type", "application/json");
            }
            if (bytes != null) req.SetRequestHeader("Content-Encoding", "gzip");
            if (install && Ready) req.SetRequestHeader("X-Install-Token", (string)credentials["install_token"]);
            if (token != null) req.SetRequestHeader("Authorization", "Bearer " + token);
            if (previous != null) req.SetRequestHeader("X-Previous-Firebase-Token", previous);
            if (etag != null) req.SetRequestHeader("If-None-Match", etag);
            var op = req.SendWebRequest(); while (!op.isDone) await Task.Yield();
            return req;
        }
        async Task FetchConfig()
        {
            using var req = await Request("/v1/config?player_id=" + PlayerId, "GET", etag: PlayerPrefs.GetString("orcub.config.etag", ""));
            if (req.responseCode == 200)
            {
                if (stopped) return;
                var value = JObject.Parse(req.downloadHandler.text);
                // Validate the adaptive pair before replacing the last good cache.
                new Adaptive().Configure(value["tuning"].ToString(), value["skill_model"].ToString());
                config = value;
                File.WriteAllText(ConfigPath, value.ToString(Formatting.None));
                PlayerPrefs.SetString("orcub.config.etag", req.GetResponseHeader("ETag") ?? ""); PlayerPrefs.Save();
                ApplyCaps();
            }
        }
        void ApplyConsent(bool allowed)
        {
            if (!firebaseReady) return;
            FirebaseAnalytics.SetAnalyticsCollectionEnabled(allowed);
            Crashlytics.IsCrashlyticsCollectionEnabled = allowed;
        }
        void Start() => ApplyCaps();
        JToken Value(string key) => remote?[key] ?? config?[key];
        public void Configure(Adaptive adaptive)
        {
            try { if (Value("tuning") != null) adaptive.Configure(Value("tuning").ToString(), Value("skill_model")?.ToString()); }
            catch (Exception) { Track("error", new { code = "config_invalid", context = "level_start" }); }
        }
        void ApplyCaps()
        {
            var p = config?["ad_policy"] ?? remote?["ad_policy"]; if (p == null) return;
            ClientGateway.AdCaps?.Invoke((double?)p["interstitial_min_interval_seconds"] ?? 150, (int?)p["interstitial_daily_cap"] ?? 6, (int?)p["rewarded_daily_cap"] ?? 20);
        }
        public void Track(string kind, object data)
        {
            if (stopped || queue == null) return;
            if (kind == "level_start") { ConsumableGateway.BeginAttempt?.Invoke(Guid.NewGuid().ToString()); ConsumableGateway.LevelStarted?.Invoke(); }
            if(kind=="level_end") ConsumableGateway.LevelEnded?.Invoke();
            // Hold optional pre-consent events durably; Flush selects eligible events.
            if (data is AttemptRecord record) data = JObject.Parse(JsonUtility.ToJson(record));
            queue.Add(kind, data, Application.isEditor || Debug.isDebugBuild);
            if (!firebaseReady || !AnalyticsConsent.Allowed) return;
            var fields = JObject.FromObject(data);
            var parameters = new List<Parameter>();
            foreach (var p in fields.Properties())
            {
                if (p.Value.Type == JTokenType.Integer) parameters.Add(new Parameter(p.Name, (long)p.Value));
                else if (p.Value.Type == JTokenType.Float) parameters.Add(new Parameter(p.Name, (double)p.Value));
                else if (p.Value.Type == JTokenType.Boolean) parameters.Add(new Parameter(p.Name, (bool)p.Value ? 1L : 0L));
                else if (p.Value.Type == JTokenType.String) parameters.Add(new Parameter(p.Name, ((string)p.Value).Substring(0, Math.Min(100, ((string)p.Value).Length))));
            }
            FirebaseAnalytics.LogEvent("orcub_" + kind, parameters.ToArray());
        }
        void Paid(string placement, long micros, string currency, string precision, string unit, string network)
        {
            Track("ad_paid", new { placement, network = "admob", value_micros = micros, currency, precision });
            if (firebaseReady && AnalyticsConsent.Allowed) FirebaseAnalytics.LogEvent("ad_impression", new Parameter("ad_platform", "AdMob"), new Parameter("ad_source", network),
                new Parameter("ad_unit_name", unit), new Parameter("ad_format", placement.StartsWith("rewarded_") ? "Rewarded" : "Interstitial"),
                new Parameter("value", micros / 1000000.0), new Parameter("currency", currency));
        }
        void StartSession()
        {
            if (sessionOpen) return;
            sessionOpen = true; session = Guid.NewGuid().ToString(); sessionAt = Time.realtimeSinceStartupAsDouble;
            Track("session_start", new { session_id = session });
        }
        void EndSession()
        {
            if (!sessionOpen) return;
            sessionOpen = false; Track("session_end", new { session_id = session, duration = Time.realtimeSinceStartupAsDouble - sessionAt });
        }
        void OnLog(string message, string stack, LogType type)
        {
            if ((type == LogType.Exception || type == LogType.Error) && Time.realtimeSinceStartupAsDouble >= nextError && errorCount < 20)
            { nextError = Time.realtimeSinceStartupAsDouble + 60; errorCount++; Track("error", new { code = "unity_error", context = "runtime" }); }
        }
        async Task RetryLink()
        {
            linking = true;
            try { await Account.Link(); } catch (Exception) { }
            finally { linking = false; nextLink = Time.realtimeSinceStartupAsDouble + 30; }
        }
        void Update()
        {
            if (!stopped && Ready && Account?.User != null && !Account.Linked && !linking && Time.realtimeSinceStartupAsDouble >= nextLink) _ = RetryLink();
            if (!stopped && Time.realtimeSinceStartupAsDouble >= nextSave) { nextSave = Time.realtimeSinceStartupAsDouble + 30; Account?.RequestSave(); }
            if (!stopped && Time.realtimeSinceStartupAsDouble >= nextFlush && !sending) _ = Flush();
        }
        public async Task Flush()
        {
            if (sending || stopped || queue == null) return; sending = true;
            try
            {
                if (!Ready) { await Register(); return; }
                if (queue.Count == 0) return;
                var batch = System.Array.FindAll(queue.Batch(AnalyticsConsent.BackendAllowed), e => AnalyticsConsent.BackendAllowed((string)e["kind"]));
                if (batch.Length == 0) return;
                using var req = await Request("/v1/events", "POST", bytes: EventQueue.Serialize(PlayerId, batch));
                if (req.responseCode >= 200 && req.responseCode < 300) { queue.Remove(batch); failures = 0; renewedInstall = false; }
                else if (req.responseCode == 400 || req.responseCode == 422)
                {
                    // Isolate the rejected event without discarding valid neighbours.
                    foreach (var item in batch)
                    {
                        if (stopped) return;
                        var single = new[] { item };
                        using var check = await Request("/v1/events", "POST", bytes: EventQueue.Serialize(PlayerId, single));
                        if ((check.responseCode >= 200 && check.responseCode < 300) || check.responseCode == 400 || check.responseCode == 422) queue.Remove(single);
                        else break;
                    }
                }
                else if (req.responseCode == 401 && !renewedInstall)
                {
                    renewedInstall = true; await RenewInstall();
                }
                else
                {
                    failures = Math.Min(failures + 1, 8);
                    double.TryParse(req.GetResponseHeader("Retry-After"), out var retry);
                    nextFlush = Time.realtimeSinceStartupAsDouble + Math.Max(retry, Math.Pow(2, failures) + UnityEngine.Random.value);
                }
            }
            catch (Exception) { failures = Math.Min(failures + 1, 8); nextFlush = Time.realtimeSinceStartupAsDouble + Math.Pow(2, failures); }
            finally { sending = false; nextFlush = Math.Max(nextFlush, Time.realtimeSinceStartupAsDouble + 5); }
        }
        void OnApplicationPause(bool paused) { if (stopped || queue == null) return; if (paused) { EndSession(); queue.Persist(); _ = Flush(); _ = SaveOnPause(); } else StartSession(); }
        async Task SaveOnPause() { try { if(Account!=null) await Account.Save(true); } catch(Exception) { } }
        void OnApplicationQuit() { if (stopped || queue == null) return; EndSession(); queue.Persist(); }
        void OnDestroy() { AnalyticsConsent.Changed -= ApplyConsent; Application.logMessageReceived -= OnLog; if (Instance == this) { Instance = null; ClientGateway.Event = null; ClientGateway.Configure = null; ClientGateway.Paid = null; ClientGateway.Save = null; } }
        public void SetPlayer(string id) { Orcub.Core.ConsumableGateway.MigrateSpends?.Invoke(PlayerId,id); credentials["player_id"] = id; InstallStore.Write(credentials.ToString(Formatting.None)); }
        public async Task RenewInstall()
        {
            var old=PlayerId; credentials=null; InstallStore.Write(""); await Register();
            if(Ready) Orcub.Core.ConsumableGateway.MigrateSpends?.Invoke(old,PlayerId);
        }
        public async Task ResetLocalAsync()
        {
            stopped = true;
            // Stopped continuations discard responses; the old writer drains before Boot.
            queue.Clear(); await queue.Stop();
            string pendingUid = PlayerPrefs.GetString("orcub.delete.firebase_uid", "");
            string revoke = PlayerPrefs.GetString("orcub.delete.google_revoke_uid", "");
            ScopedPreferences.Clear();
            if (pendingUid.Length > 0) PlayerPrefs.SetString("orcub.delete.firebase_uid", pendingUid);
            if (revoke.Length>0) PlayerPrefs.SetString("orcub.delete.google_revoke_uid", revoke);
            PlayerPrefs.Save(); InstallStore.Write(""); credentials = null;
            foreach (var file in new[] { "orcub-attempts.json", "orcub-config.json", "orcub-events.json" })
            { var p = Path.Combine(Application.persistentDataPath, file); if (File.Exists(p)) File.Delete(p); }

            Destroy(gameObject); Instance = null; Boot();
            UnityEngine.SceneManagement.SceneManager.LoadScene("Game");
        }
        public void GoogleDisconnected(string result) => Account?.GoogleDisconnected(result);
        public void GoogleResult(string token) => Account.GoogleResult(token);
    }
}
