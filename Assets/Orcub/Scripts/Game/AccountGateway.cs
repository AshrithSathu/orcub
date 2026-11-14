using System;
using System.Threading.Tasks;

namespace Orcub.Game
{
    /// <summary>Progress summary shown on the cloud-restore and welcome-back cards.</summary>
    public struct SaveSummary
    {
        public int Level;   // 1-based frontier level
        public int Stars;   // total stars earned
        public SaveSummary(int level, int stars) { Level = level; Stars = stars; }
        public bool AheadOf(SaveSummary other) => Level != other.Level ? Level > other.Level : Stars > other.Stars;
    }

    /// <summary>
    /// SDK-free account boundary. Orcub.Net installs the Firebase-backed implementation; UI in this assembly
    /// never references Firebase. Everything here must tolerate a missing or offline service.
    /// </summary>
    public interface IAccountService
    {
        bool Ready { get; }                 // Firebase Auth has a user (anonymous or signed in)
        bool IsGuest { get; }
        string Provider { get; }            // "Google" (or "Apple" later)
        string Email { get; }
        DateTime? LastCloudSave { get; }    // UTC, last confirmed round trip with the cloud save
        bool HasConflict { get; }
        SaveSummary Cloud { get; }
        SaveSummary Local { get; }
        Task SignInWithGoogle();
        Task SignOut();
        Task DeleteAccount();
        Task ResolveConflict(bool keepCloud);
    }

    /// <summary>
    /// Optional account-recovery surface (review f). Kept separate from <see cref="IAccountService"/> so the Net
    /// bridge keeps compiling until it implements it; the UI checks <c>AccountGateway.Service as IAccountRecovery</c>.
    /// </summary>
    public interface IAccountRecovery
    {
        /// <summary>A delete intent for this user was never confirmed: offer "Finish deleting your account?".</summary>
        bool DeletionNeedsConfirmation { get; }
        /// <summary>Sign out; <c>discardUnsyncedProgress</c> only after the protective default failed and the player chose it.</summary>
        Task SignOut(bool discardUnsyncedProgress);
        /// <summary>Cancel a pending native reauthentication prompt.</summary>
        void CancelReauthentication();
    }

    public static class AccountGateway
    {
        /// <summary>Sign in with Apple stays hidden until the owner joins the paid Apple programme.</summary>
        public const bool ShowAppleSignIn = false;

        public static IAccountService Service { get; set; }

        /// <summary>Real boot progress, written by Orcub.Net while it starts (read by the loader).</summary>
        public static class Boot
        {
            public static volatile bool FirebaseReady, AuthDone, ConfigDone, SaveChecked, NetworkFailed;
            /// <summary>Set when a fresh phone silently picked up a cloud save (welcome back).</summary>
            public static SaveSummary? Restored;

            public static void Reset()
            {
                FirebaseReady = AuthDone = ConfigDone = SaveChecked = NetworkFailed = false;
                Restored = null;
            }
        }
    }
}
