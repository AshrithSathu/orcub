using System;
using System.Threading.Tasks;
namespace Orcub.Net
{
    public static class FinalAccountSave
    {
        // A failed final save must leave local progress intact.
        public static async Task Run(Func<bool,Task> save, Func<Task> flush)
        {
            await save(true);
            await flush();
        }
    }
}
