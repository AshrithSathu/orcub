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
        string linkedUid;
        public bool DeletionNeedsConfirmation => UnityEngine.PlayerPrefs.GetString("orcub.delete.intent", "").Length>0;
        public bool Linked => linked && linkedUid==User?.UserId;
        string lastSaved;
        DateTime nextSave;
        const string PendingDelete = "orcub.delete.firebase_uid";
        bool resumingDeletion;
        bool HasDeletionIntent => User!=null && UnityEngine.PlayerPrefs.GetString("orcub.delete.intent", "")==User.UserId;
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
            var legacyProof=UnityEngine.PlayerPrefs.GetString("orcub.link.proof","");
            if(!string.IsNullOrEmpty(legacyProof) && string.IsNullOrEmpty(client.MergeProof)) client.SetMergeProof(legacyProof,"legacy");
            UnityEngine.PlayerPrefs.DeleteKey("orcub.link.previous"); UnityEngine.PlayerPrefs.DeleteKey("orcub.link.proof"); UnityEngine.PlayerPrefs.Save();
            var intent=UnityEngine.PlayerPrefs.GetString("orcub.delete.intent", "");
            if(await AccountDeletion.Resume(intent.Length>0, UnityEngine.PlayerPrefs.GetInt("orcub.delete.pending",0)==1,
                async () => {
                    // With the original account still signed in, resuming the (idempotent) DELETE below is the
                    // reliable path; the probe is only for when that identity is already gone. Only the server's
                    // own 401 body is proof: an edge 404 or a proxy page must never wipe a live player.
                    if(!client.Ready || User?.UserId==intent) return false;
                    using var probe=await client.Request("/v1/config?player_id="+client.PlayerId,"GET");
                    return probe.responseCode==401 && (probe.downloadHandler?.text ?? "").Contains("\"unauthorized\"");
                }, async () => {
                    if(User?.UserId==intent) { UnityEngine.PlayerPrefs.SetString(PendingDelete,intent); UnityEngine.PlayerPrefs.Save(); }
                    await RevokeGoogle(intent); auth.SignOut();
                    Firebase.Analytics.FirebaseAnalytics.ResetAnalyticsData();
                    await client.ResetLocalAsync();
                })) return;
            if(intent.Length>0) {
                // An offline probe keeps the durable intent; it must not fail boot.
                if(User?.UserId==intent) {
                    resumingDeletion=true;
                    try { await DeleteAccount(); } catch(Exception) { } finally { resumingDeletion=false; }
                }
                Changed?.Invoke(); return;
            }
            if (User == null) await auth.SignInAnonymouslyAsync();
            if (await RetryIdentityDeletion()) return;
            await Link(); Changed?.Invoke();
        }
        public async Task Link(string previous = null)
        {
            if(client.MergeProofUid==User?.UserId) client.SetMergeProof(null,null);
            previous = previous ?? client.MergeProof;
            if(HasDeletionIntent) return;
            if (User == null || !client.Ready || deleting || client.Stopped || (Linked && previous==null)) return;
            if (UnityEngine.PlayerPrefs.GetString(PendingDelete, "") == User.UserId || UnityEngine.PlayerPrefs.GetInt("orcub.delete.pending", 0) == 1) return;
            await gate.WaitAsync();
            try
            {
                if (client.Stopped) return;
                if(Orcub.Core.ConsumableGateway.FlushSpends!=null) { await FlushSpends(); if(Orcub.Core.ConsumableGateway.HasPendingSpends?.Invoke()==true) throw new InvalidOperationException("guest_spends_pending"); }
                using var req = await Authorized("/v1/link", "POST", new { player_id = client.PlayerId }, previous);
                if(req.responseCode>=200 && req.responseCode<300) client.SetMergeProof(null,null);
                Ensure(req);
                if (client.Stopped) return;
                client.SetPlayer((string)JObject.Parse(req.downloadHandler.text)["player_id"]); linked = true; linkedUid=User.UserId;

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
            await DrainBeforeSwitch(FlushSpends, () => Orcub.Core.ConsumableGateway.HasPendingSpends?.Invoke()==true);
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
                await DrainBeforeSwitch(FlushSpends, () => Orcub.Core.ConsumableGateway.HasPendingSpends?.Invoke()==true);
                var credential = GoogleAuthProvider.GetCredential(token, null);
                var oldUid = User?.UserId;
                google = null;
                if(User!=null && User.IsAnonymous) {
                    await Link();
                    using var prepare=await Authorized("/v1/link/prepare","POST",new { player_id=client.PlayerId }); Ensure(prepare);
                    client.SetMergeProof((string)JObject.Parse(prepare.downloadHandler.text)["merge_proof"],User.UserId);
                }
                try { if (User != null && User.IsAnonymous) await User.LinkWithCredentialAsync(credential); else await auth.SignInWithCredentialAsync(credential); }
                catch (Firebase.FirebaseException e) when ((AuthError)e.ErrorCode == AuthError.CredentialAlreadyInUse)
                { await auth.SignInWithCredentialAsync(credential); }
                if (UnityEngine.PlayerPrefs.GetString("orcub.delete.google_revoke_uid", "") == User.UserId) await RevokeGoogle();
                if (await RetryIdentityDeletion(true)) { await Link(); Changed?.Invoke(); return; }
                if (User.UserId == oldUid) client.SetMergeProof(null,null);
                await Link(); Changed?.Invoke();
#pragma warning restore CS0162
            }
            catch (Exception) { Failed?.Invoke("google_sign_in_failed"); throw; }
            finally { google = null; }
        }
        public static async Task DrainBeforeSwitch(Func<Task> flush, Func<bool> pending)
        {
            try { var drain=flush(); await Task.WhenAny(drain,Task.Delay(5000)); } catch(Exception) { }
            if(pending()) throw new InvalidOperationException("guest_spends_pending");
        }
        public void GoogleResult(string token) => google?.TrySetResult(token);
        async Task FlushSpends() { try { var task=Orcub.Core.ConsumableGateway.FlushSpends?.Invoke(); if(task!=null) await Task.WhenAny(task,Task.Delay(5000)); } catch(Exception) { } }
        public Task SignOut() => SignOut(false);
        public async Task SignOut(bool discardUnsyncedProgress)
        {
            if (auth == null) return;
            if(!discardUnsyncedProgress) await FinalAccountSave.Run(Save,FlushSpends);
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
        async Task RevokeGoogle(string targetUid = null)
        {
#if UNITY_IOS && !UNITY_EDITOR
            UnityEngine.PlayerPrefs.SetString("orcub.delete.google_revoke_uid", targetUid ?? User?.UserId ?? ""); UnityEngine.PlayerPrefs.Save();
            disconnect = new TaskCompletionSource<bool>(); OrcubGoogleDisconnect();
            var completed = await Task.WhenAny(disconnect.Task, Task.Delay(15000));
            if (completed == disconnect.Task && await disconnect.Task)
            { UnityEngine.PlayerPrefs.DeleteKey("orcub.delete.google_revoke_uid"); UnityEngine.PlayerPrefs.Save(); }
            disconnect = null;
#else
            await Task.CompletedTask;
#endif
        }
        public void CancelReauthentication() => google?.TrySetResult(null);
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
            string deletedUid=User.UserId;
            try
            {
                if (!recentlyAuthenticated) { IdentityDeletionDeferred?.Invoke(); return false; }
                await User.DeleteAsync();
                UnityEngine.PlayerPrefs.DeleteKey(PendingDelete); UnityEngine.PlayerPrefs.Save();
            }
            catch (Exception) { IdentityDeletionDeferred?.Invoke(); }
            await RevokeGoogle(deletedUid);
            auth.SignOut();
            Firebase.Analytics.FirebaseAnalytics.ResetAnalyticsData();
            linked=false; await auth.SignInAnonymouslyAsync(); return true;
        }
        public async Task DeleteAccount()
        {
            if (User == null) throw new InvalidOperationException("No account");
            string uid = User.UserId;
            if(!resumingDeletion) await AccountDeletion.Prepare(() => Save(true), FlushSpends);
            await gate.WaitAsync(); deleting = true;
            bool identityDeleted=false;
            try
            {
                await AccountDeletion.Run(async () => {
                    if(resumingDeletion || UnityEngine.PlayerPrefs.GetInt("orcub.delete.pending",0)==1) return;
                    try { await Reauthenticate(); }
                    catch(Firebase.FirebaseException e) when (IdentityGone(e)) {
                        identityDeleted=true;
                        UnityEngine.PlayerPrefs.SetString("orcub.delete.intent",uid);
                        UnityEngine.PlayerPrefs.SetInt("orcub.delete.pending",1); UnityEngine.PlayerPrefs.Save();
                    }
                    catch(Firebase.FirebaseException e) when ((AuthError)e.ErrorCode==AuthError.UserDisabled) { }
                }, async () => {
                    if (UnityEngine.PlayerPrefs.GetInt("orcub.delete.pending", 0) == 0)
                    {
                        UnityEngine.PlayerPrefs.SetString("orcub.delete.intent",uid); UnityEngine.PlayerPrefs.Save();
                        identityDeleted=await AccountDeletion.ConfirmBackendDeletion(async () => {
                            using var req=await Authorized("/v1/account", "DELETE");
                            if(req.responseCode>=400 && req.responseCode<500 && req.responseCode!=401 && req.responseCode!=404) { UnityEngine.PlayerPrefs.DeleteKey("orcub.delete.intent"); UnityEngine.PlayerPrefs.Save(); }
                            Ensure(req);
                            return (bool?)JObject.Parse(req.downloadHandler.text)["identity_deleted"]==true;
                        });
                        UnityEngine.PlayerPrefs.SetInt("orcub.delete.pending", 1); UnityEngine.PlayerPrefs.Save();
                    }
                }, () => identityDeleted ? Task.CompletedTask : User.DeleteAsync(), () => {
                    UnityEngine.PlayerPrefs.SetString(PendingDelete, uid); UnityEngine.PlayerPrefs.Save(); IdentityDeletionDeferred?.Invoke();
                }, async () => {
                    await RevokeGoogle(uid); auth.SignOut(); linked = false;
                    Firebase.Analytics.FirebaseAnalytics.ResetAnalyticsData();
                    await client.ResetLocalAsync(); Changed?.Invoke();
                });
            }
            catch (Exception) { Failed?.Invoke("account_delete_failed"); throw; }
            finally { deleting = false; gate.Release(); }
        }
        static bool IdentityGone(Firebase.FirebaseException e) => (AuthError)e.ErrorCode==AuthError.UserNotFound;
        public async void RequestSave() { try { await Save(); } catch (Exception) { Failed?.Invoke("cloud_save_failed"); } }
        public async Task Save(bool force = false)
        {
            if (deleting || HasDeletionIntent || User == null) return;
            if(!Linked) { if(!force) return; await Link(); if(!Linked) throw new InvalidOperationException("final_save_requires_link"); }
            if(PendingCloud!=null) { if(force) throw new InvalidOperationException("resolve_cloud_conflict_before_signout"); return; }
            if(force) await gate.WaitAsync(); else if(!await gate.WaitAsync(0)) return;
            try
            {
                var local = SaveSnapshot.Capture();
                string json = local.ToString(Newtonsoft.Json.Formatting.None);
                if (json == lastSaved || (!force && DateTime.UtcNow < nextSave)) return;
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
            if (!keepCloud) await Save(true);
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
