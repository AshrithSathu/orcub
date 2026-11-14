using System;
using System.Threading.Tasks;

namespace Orcub.Net
{
    public static class AccountDeletion
    {
        public static async Task Prepare(Func<Task> save, Func<Task> flush)
        {
            try { await Task.WhenAny(save(), Task.Delay(2000)); } catch(Exception) { }
            try { await Task.WhenAny(flush(), Task.Delay(5000)); } catch(Exception) { }
        }
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
