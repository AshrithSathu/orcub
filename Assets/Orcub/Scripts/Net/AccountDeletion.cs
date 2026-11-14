using System;
using System.Threading.Tasks;

namespace Orcub.Net
{
    public static class AccountDeletion
    {
        // Backend deletion is the irreversible boundary. Identity failure must not retain local data.
        public static async Task Run(Func<Task> reauthenticate, Func<Task> backendDelete,
            Func<Task> identityDelete, Action queueIdentityDelete, Func<Task> reset)
        {
            await reauthenticate();
            await backendDelete();
            try { await identityDelete(); }
            catch (Exception) { queueIdentityDelete(); }
            await reset();
        }
    }
}
