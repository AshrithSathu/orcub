using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Firebase.Auth;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

namespace Orcub.Net
{
    public sealed class AccountService
    {
        readonly ClientServices client;
        readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        FirebaseAuth auth;
        TaskCompletionSource<string> google;
        TaskCompletionSource<bool> disconnect;
        bool linked, deleting;
        public bool Linked => linked;
        string lastSaved;
        DateTime nextSave;
        const string PendingDelete = "orcub.delete.firebase_uid";
        long revision;
        public event Action Changed;
        /// <summary>Deletion finished locally; Firebase cleanup will retry on matching authenticated UID.</summary>
        public event Action IdentityDeletionDeferred;
        public event Action<string> Failed;
        public FirebaseUser User => auth?.CurrentUser;
        public bool IsAnonymous => User == null || User.IsAnonymous;
        /// <summary>UTC time of the last confirmed cloud round trip (link or save).</summary>
        public DateTime? LastSaved { get; private set; }
        /// <summary>Cloud snapshot that differs from this phone; saves pause until the player picks one.</summary>
        public JObject PendingCloud { get; private set; }
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void OrcubGoogleSignIn();
        [DllImport("__Internal")] static extern void OrcubGoogleSignOut();
        [DllImport("__Internal")] static extern void OrcubGoogleDisconnect();
#endif
        public AccountService(ClientServices client) { this.client = client; }
        public async Task Initialize()
        {
            auth = FirebaseAuth.DefaultInstance;
            if (User == null) await auth.SignInAnonymouslyAsync();
            if (UnityEngine.PlayerPrefs.GetInt("orcub.delete.pending", 0) == 1) { await DeleteAccount(); return; }
            if (await RetryIdentityDeletion()) return;
            await Link(); Changed?.Invoke();
        }
        public async Task Link(string previous = null)
        {
            if (User == null || !client.Ready || deleting || client.Stopped) return;
            if (UnityEngine.PlayerPrefs.GetString(PendingDelete, "") == User.UserId || UnityEngine.PlayerPrefs.GetInt("orcub.delete.pending", 0) == 1) return;
            await gate.WaitAsync();
            try
            {
                if (client.Stopped) return;
                using var req = await Authorized("/v1/link", "POST", new { player_id = client.PlayerId }, previous);
                Ensure(req);
                if (client.Stopped) return;
                client.SetPlayer((string)JObject.Parse(req.downloadHandler.text)["player_id"]); linked = true;
                using var save = await Authorized("/v1/save", "GET"); Ensure(save);
                if (client.Stopped) return;
                var state = JObject.Parse(save.downloadHandler.text); revision = (long)state["revision"];
                if (state["data"] is JObject data)
                {
                    var local = SaveSnapshot.Capture();
                    var diff = SaveSnapshot.Compare(local, data);
                    if (diff == SaveDiff.Conflict) PendingCloud = data;   // the player chooses (cloud restore sheet)
                    else
                    {
                        SaveSnapshot.Apply(SaveSnapshot.MergeProgress(local, data));
                        if (diff == SaveDiff.Fresh) Orcub.Game.AccountGateway.Boot.Restored = SaveSnapshot.Summary(data);
                    }
                }
                LastSaved = DateTime.UtcNow;
                Orcub.Game.AccountGateway.Boot.SaveChecked = true;
                Changed?.Invoke();
            }
            finally { gate.Release(); }
        }
        public async Task SignInWithGoogle()
        {
            if (auth == null) throw new InvalidOperationException("Firebase is not ready");
            if (google != null) throw new InvalidOperationException("Sign-in already pending");
            google = new TaskCompletionSource<string>();
            try
            {
#if UNITY_IOS && !UNITY_EDITOR
                OrcubGoogleSignIn();
#else
                throw new PlatformNotSupportedException("Native Google sign-in requires iOS");
#endif
#pragma warning disable CS0162
                var token = await google.Task;
                if (string.IsNullOrEmpty(token)) throw new OperationCanceledException("Google sign-in cancelled");
                var credential = GoogleAuthProvider.GetCredential(token, null);
                var oldUid = User?.UserId;
                google = null;
                var oldToken = User != null && User.IsAnonymous && linked ? await User.TokenAsync(true) : null;
                try { if (User != null && User.IsAnonymous) await User.LinkWithCredentialAsync(credential); else await auth.SignInWithCredentialAsync(credential); }
                catch (Firebase.FirebaseException e) when ((AuthError)e.ErrorCode == AuthError.CredentialAlreadyInUse)
                { await auth.SignInWithCredentialAsync(credential); }
                if (UnityEngine.PlayerPrefs.GetInt("orcub.delete.google_revoke", 0) == 1) await RevokeGoogle();
                if (await RetryIdentityDeletion(true)) return;
                if (User.UserId == oldUid) oldToken = null;
                await Link(oldToken); Changed?.Invoke();
#pragma warning restore CS0162
            }
            catch (Exception) { Failed?.Invoke("google_sign_in_failed"); throw; }
            finally { google = null; }
        }
        public void GoogleResult(string token) => google?.TrySetResult(token);
        public async Task SignOut()
        {
            if (auth == null) return;
            await Save();
            await gate.WaitAsync();
            try
            {
                deleting = true; auth.SignOut(); linked = false;
#if UNITY_IOS && !UNITY_EDITOR
                OrcubGoogleSignOut();
#endif
                await client.ResetLocalAsync();
            }
            finally { deleting = false; gate.Release(); }
        }
        public void GoogleDisconnected(string result) => disconnect?.TrySetResult(string.IsNullOrEmpty(result));
        async Task RevokeGoogle()
        {
#if UNITY_IOS && !UNITY_EDITOR
            UnityEngine.PlayerPrefs.SetInt("orcub.delete.google_revoke", 1); UnityEngine.PlayerPrefs.Save();
            disconnect = new TaskCompletionSource<bool>(); OrcubGoogleDisconnect();
            var completed = await Task.WhenAny(disconnect.Task, Task.Delay(15000));
            if (completed == disconnect.Task && await disconnect.Task)
            { UnityEngine.PlayerPrefs.DeleteKey("orcub.delete.google_revoke"); UnityEngine.PlayerPrefs.Save(); }
            disconnect = null;
#else
            await Task.CompletedTask;
#endif
        }
        async Task Reauthenticate()
        {
            if (User.IsAnonymous) return;
            if (google != null) throw new InvalidOperationException("Sign-in pending");
            google = new TaskCompletionSource<string>();
            try
            {
#if UNITY_IOS && !UNITY_EDITOR
                OrcubGoogleSignIn();
#else
                throw new PlatformNotSupportedException("Google reauthentication requires iOS");
#endif
#pragma warning disable CS0162
                var token = await google.Task;
                if (string.IsNullOrEmpty(token)) throw new OperationCanceledException();
                await User.ReauthenticateAsync(GoogleAuthProvider.GetCredential(token, null));
#pragma warning restore CS0162
            }
            finally { google = null; }
        }
        async Task<bool> RetryIdentityDeletion(bool recentlyAuthenticated = false)
        {
            if (User == null || UnityEngine.PlayerPrefs.GetString(PendingDelete, "") != User.UserId) return false;
            try
            {
                if (!recentlyAuthenticated) await Reauthenticate();
                await User.DeleteAsync();
                UnityEngine.PlayerPrefs.DeleteKey(PendingDelete); UnityEngine.PlayerPrefs.Save();
            }
            catch (Exception) { IdentityDeletionDeferred?.Invoke(); }
            auth.SignOut();
            await RevokeGoogle();
            Firebase.Analytics.FirebaseAnalytics.ResetAnalyticsData();
            await client.ResetLocalAsync(); return true;
        }
        public async Task DeleteAccount()
        {
            if (User == null) throw new InvalidOperationException("No account");
            await gate.WaitAsync(); deleting = true;
            string uid = User.UserId;
            try
            {
                await AccountDeletion.Run(() => UnityEngine.PlayerPrefs.GetInt("orcub.delete.pending", 0) == 1 ? Task.CompletedTask : Reauthenticate(), async () => {
                    if (UnityEngine.PlayerPrefs.GetInt("orcub.delete.pending", 0) == 0)
                    {
                        using var req = await Authorized("/v1/account", "DELETE"); Ensure(req);
                        UnityEngine.PlayerPrefs.SetInt("orcub.delete.pending", 1); UnityEngine.PlayerPrefs.Save();
                    }
                }, () => User.DeleteAsync(), () => {
                    UnityEngine.PlayerPrefs.SetString(PendingDelete, uid); UnityEngine.PlayerPrefs.Save(); IdentityDeletionDeferred?.Invoke();
                }, async () => {
                    auth.SignOut(); linked = false;
                    Firebase.Analytics.FirebaseAnalytics.ResetAnalyticsData();
                    await RevokeGoogle();
                    await client.ResetLocalAsync(); Changed?.Invoke();
                });
            }
            catch (Exception) { Failed?.Invoke("account_delete_failed"); throw; }
            finally { deleting = false; gate.Release(); }
        }
        public async void RequestSave() { try { await Save(); } catch (Exception) { Failed?.Invoke("cloud_save_failed"); } }
        public async Task Save()
        {
            if (!linked || deleting || User == null || PendingCloud != null || !await gate.WaitAsync(0)) return;
            try
            {
                var local = SaveSnapshot.Capture();
                string json = local.ToString(Newtonsoft.Json.Formatting.None);
                if (json == lastSaved || DateTime.UtcNow < nextSave) return;
                nextSave = DateTime.UtcNow.AddSeconds(30);
                using var req = await Authorized("/v1/save", "PUT", new { revision, data = local });
                if (req.responseCode == 409)
                {
                    var current = JObject.Parse(req.downloadHandler.text); revision = (long)current["revision"];
                    var merged = SaveSnapshot.MergeProgress(SaveSnapshot.Capture(), current["data"] as JObject);
                    using var retry = await Authorized("/v1/save", "PUT", new { revision, data = merged }); Ensure(retry);
                    var accepted = JObject.Parse(retry.downloadHandler.text); revision = (long)accepted["revision"];
                    SaveSnapshot.Apply((JObject)accepted["data"]); json = accepted["data"].ToString(Newtonsoft.Json.Formatting.None); Changed?.Invoke();
                }
                else if (req.responseCode == 0 || req.responseCode >= 500)
                {
                    // A lost acknowledgement may already have committed. Resolve before another write.
                    using var read = await Authorized("/v1/save", "GET"); Ensure(read);
                    var current = JObject.Parse(read.downloadHandler.text); revision = (long)current["revision"];
                    var merged = SaveSnapshot.MergeProgress(SaveSnapshot.Capture(), current["data"] as JObject);
                    using var retry = await Authorized("/v1/save", "PUT", new { revision, data = merged }); Ensure(retry);
                    revision = (long)JObject.Parse(retry.downloadHandler.text)["revision"];
                    SaveSnapshot.Apply(merged); json = merged.ToString(Newtonsoft.Json.Formatting.None);
                }
                else { Ensure(req); revision = (long)JObject.Parse(req.downloadHandler.text)["revision"]; }
                lastSaved = json;
                LastSaved = DateTime.UtcNow;
            }
            finally { gate.Release(); }
        }
        /// <summary>Cloud restore choice. Keeping this phone writes it over the cloud at the revision just read.</summary>
        public async Task ResolveConflict(bool keepCloud)
        {
            var cloud = PendingCloud;
            if (cloud == null) return;
            PendingCloud = null;
            if (keepCloud) SaveSnapshot.Apply(cloud);
            Changed?.Invoke();
            if (!keepCloud) await Save();
        }
        async Task<UnityWebRequest> Authorized(string path, string method, object body = null, string previous = null)
        {
            var req = await client.Request(path, method, body, await User.TokenAsync(false), previous: previous);
            if (req.responseCode != 401) return req;
            req.Dispose(); return await client.Request(path, method, body, await User.TokenAsync(true), previous: previous);
        }
        static void Ensure(UnityWebRequest req) { if (req.responseCode < 200 || req.responseCode >= 300) throw new InvalidOperationException("Account request failed: " + req.responseCode); }
    }
}
