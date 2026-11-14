using System;
using System.Threading.Tasks;
namespace Orcub.Net
{
    public static class InstallRetry
    {
        public static async Task<T> Run<T>(Func<Task<T>> send, Func<T,long> status, Action<T> dispose, Func<Task> renew)
        {
            var response=await send();
            if(status(response)!=401) return response;
            dispose(response); await renew(); return await send();
        }
    }
}
