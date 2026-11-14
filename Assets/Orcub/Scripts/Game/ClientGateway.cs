using System;
using Orcub.Core;

namespace Orcub.Game
{
    // SDK-free boundary: gameplay remains usable before any client has initialised.
    public static class ClientGateway
    {
        public static Action<string, object> Event;
        public static Action<Adaptive> Configure;
        public static Action Save;
        public static Action<double, int, int> AdCaps;
        public static Action<string, long, string, string, string, string> Paid;
        public static void Track(string kind, object data) => Event?.Invoke(kind, data);
    }
}
