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
        public const string BaseUrl = "https://api-production-08259b35.up.railway.app";
        public AccountService Account { get; private set; }
        public string PlayerId => (string)credentials?["player_id"];
        public bool Ready => credentials != null;
        JObject credentials, config, remote;
        EventQueue queue;
        bool sending, registering, firebaseReady, sessionOpen, stopped;
        string session;
        double sessionAt, nextFlush, nextSave;
        int failures;
        string ConfigPath => Path.Combine(Application.persistentDataPath, "orcub-config.json");
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Boot() { if (Instance == null) new GameObject("ClientServices").AddComponent<ClientServices>(); }
        async void Awake()
        {
            Instance = this; DontDestroyOnLoad(gameObject);
            AccountGateway.Boot.Reset();
            queue = new EventQueue(Path.Combine(Application.persistentDataPath, "orcub-events.json"));
            try { var stored = InstallStore.Read(); if (!string.IsNullOrEmpty(stored)) credentials = JObject.Parse(stored); }
            catch (Exception) { Debug.LogWarning("Install credentials unavailable"); }
            try { if (File.Exists(ConfigPath)) config = JObject.Parse(File.ReadAllText(ConfigPath)); } catch (Exception) { }
            ClientGateway.Event = Track;
            ClientGateway.Configure = Configure;
            ClientGateway.Paid = Paid;
            ClientGateway.Save = () => Account?.RequestSave();
            Application.logMessageReceived += OnLog;
            StartSession();
            Account = new AccountService(this);
            _ = InitializeFirebase();
            await Register();
        }
        async Task InitializeFirebase()
        {
            try
            {
                if (await FirebaseApp.CheckAndFixDependenciesAsync() != DependencyStatus.Available) return;
                firebaseReady = true; AccountGateway.Boot.FirebaseReady = true;
                Crashlytics.IsCrashlyticsCollectionEnabled = true;
                Crashlytics.ReportUncaughtExceptionsAsFatal = true;
                var rc = FirebaseRemoteConfig.DefaultInstance;
                await rc.SetDefaultsAsync(new Dictionary<string, object> { ["tuning"] = JsonUtility.ToJson(new Tuning()), ["skill_model"] = JsonUtility.ToJson(PlayerModel.Load()), ["ad_policy"] = "{\"interstitial_min_interval_seconds\":150,\"interstitial_daily_cap\":6,\"rewarded_daily_cap\":20}", ["version"] = "bundled", ["experiments"] = "{}" });
                try { await Account.Initialize(); }
                catch (Exception) { AccountGateway.Boot.NetworkFailed = true; Track("error", new { code = "anonymous_auth_failed", context = "startup" }); }
                AccountGateway.Boot.AuthDone = true;
                try
                {
                    await rc.FetchAsync(TimeSpan.FromHours(1)); await rc.ActivateAsync();
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
                        build = string.IsNullOrEmpty(Application.buildGUID) ? "editor" : Application.buildGUID, platform = Application.isEditor ? "editor" : "ios", os_version = SystemInfo.operatingSystem,
                        device_model = SystemInfo.deviceModel, device_ram_mb = SystemInfo.systemMemorySize,
                        screen = new { w = Screen.width, h = Screen.height, dpi = Screen.dpi }, locale = culture.Name,
                        language = culture.TwoLetterISOLanguageName, timezone = TimeZoneInfo.Local.Id, first_open_ts = DateTime.UtcNow.ToString("o") };
                    using var req = await Request("/v1/install", "POST", payload, install: false);
                    if (req.responseCode != 200 && req.responseCode != 201) { AccountGateway.Boot.NetworkFailed = true; nextFlush = Time.realtimeSinceStartupAsDouble + 30; return; }
                    var value = JObject.Parse(req.downloadHandler.text);
                    InstallStore.Write(value.ToString(Formatting.None)); credentials = value;
                }
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
                var value = JObject.Parse(req.downloadHandler.text);
                // Validate the adaptive pair before replacing the last good cache.
                new Adaptive().Configure(value["tuning"].ToString(), value["skill_model"].ToString());
                config = value;
                File.WriteAllText(ConfigPath, value.ToString(Formatting.None));
                PlayerPrefs.SetString("orcub.config.etag", req.GetResponseHeader("ETag") ?? ""); PlayerPrefs.Save();
                ApplyCaps();
            }
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
            var p = Value("ad_policy"); if (p == null) return;
            ClientGateway.AdCaps?.Invoke((double?)p["interstitial_min_interval_seconds"] ?? 150, (int?)p["interstitial_daily_cap"] ?? 6, (int?)p["rewarded_daily_cap"] ?? 20);
        }
        public void Track(string kind, object data)
        {
            if (data is AttemptRecord record) data = JObject.Parse(JsonUtility.ToJson(record));
            queue.Add(kind, data);
            if (!firebaseReady) return;
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
            if (firebaseReady) FirebaseAnalytics.LogEvent("ad_impression", new Parameter("ad_platform", "AdMob"), new Parameter("ad_source", network),
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
            if (type == LogType.Exception || type == LogType.Error) queue.Add("error", new { code = "unity_error", context = "runtime" });
        }
        void Update()
        {
            if (!stopped && Time.realtimeSinceStartupAsDouble >= nextSave) { nextSave = Time.realtimeSinceStartupAsDouble + 30; Account?.RequestSave(); }
            if (!stopped && Time.realtimeSinceStartupAsDouble >= nextFlush && !sending) _ = Flush();
        }
        public async Task Flush()
        {
            if (sending || stopped) return; sending = true;
            try
            {
                if (!Ready) { await Register(); return; }
                if (queue.Count == 0) return;
                var batch = queue.Batch();
                using var req = await Request("/v1/events", "POST", bytes: EventQueue.Serialize(PlayerId, batch));
                if (req.responseCode >= 200 && req.responseCode < 300) { queue.Remove(batch); failures = 0; }
                else if (req.responseCode == 400 || req.responseCode == 422) { queue.Remove(batch); failures = 0; Debug.LogWarning("Telemetry batch rejected by schema"); }
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
        void OnApplicationPause(bool paused) { if (paused) { EndSession(); queue.Persist(); _ = Flush(); Account?.RequestSave(); } else StartSession(); }
        void OnApplicationQuit() { EndSession(); queue.Persist(); }
        void OnDestroy() { Application.logMessageReceived -= OnLog; if (Instance == this) { Instance = null; ClientGateway.Event = null; ClientGateway.Configure = null; ClientGateway.Paid = null; ClientGateway.Save = null; } }
        public void SetPlayer(string id) { credentials["player_id"] = id; InstallStore.Write(credentials.ToString(Formatting.None)); }
        public void ResetLocal()
        {
            stopped = true; queue.Clear(); InstallStore.Write(""); credentials = null;
            PlayerPrefs.DeleteAll(); PlayerPrefs.Save();
            foreach (var file in new[] { "orcub-attempts.json", "orcub-config.json" }) { var p = Path.Combine(Application.persistentDataPath, file); if (File.Exists(p)) File.Delete(p); }
            UnityEngine.SceneManagement.SceneManager.LoadScene("Game");
            Destroy(gameObject);
            Instance = null; Boot();
        }
        public void GoogleResult(string token) => Account.GoogleResult(token);
    }
}
