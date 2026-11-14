using System;
using System.Threading.Tasks;
using Orcub.Game;
using UnityEngine;

namespace Orcub.Net
{
    /// <summary>Exposes <see cref="AccountService"/> to the SDK-free UI through <see cref="AccountGateway"/>.</summary>
    public sealed class AccountBridge : IAccountService
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install() => AccountGateway.Service = new AccountBridge();

        // ClientServices is recreated after sign-out/delete, so always read the live instance.
        static AccountService A => ClientServices.Instance != null ? ClientServices.Instance.Account : null;

        public bool Ready => A?.User != null;
        public bool IsGuest => A == null || A.IsAnonymous;

        public string Provider
        {
            get
            {
                var user = A?.User;
                if (user == null) return null;
                foreach (var p in user.ProviderData)
                {
                    if (p.ProviderId == "google.com") return "Google";
                    if (p.ProviderId == "apple.com") return "Apple";
                }
                return null;
            }
        }

        public string Email => A?.User?.Email;
        public DateTime? LastCloudSave => A?.LastSaved;
        public bool HasConflict => A != null && A.PendingCloud != null;
        public SaveSummary Cloud => A?.PendingCloud != null ? SaveSnapshot.Summary(A.PendingCloud) : default;
        public SaveSummary Local => SaveSnapshot.Summary(SaveSnapshot.Capture());

        public Task SignInWithGoogle() => A != null ? A.SignInWithGoogle() : Task.FromException(new InvalidOperationException("Offline"));
        public Task SignOut() => A != null ? A.SignOut() : Task.FromException(new InvalidOperationException("Offline"));
        public Task DeleteAccount() => A != null ? A.DeleteAccount() : Task.FromException(new InvalidOperationException("Offline"));
        public Task ResolveConflict(bool keepCloud) => A != null ? A.ResolveConflict(keepCloud) : Task.CompletedTask;
    }
}
