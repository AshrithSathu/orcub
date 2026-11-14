#if ORCUB_MOBILE_NOTIFICATIONS
using System;
using System.Threading.Tasks;
using Unity.Notifications.iOS;

namespace Orcub.Daily
{
    public sealed class IosLocalNotifications : ILocalNotifications
    {
        public bool Authorized => iOSNotificationCenter.GetNotificationSettings().AuthorizationStatus == AuthorizationStatus.Authorized;
        public string OpenedId => iOSNotificationCenter.GetLastRespondedNotification()?.Identifier;
        public async Task<bool> RequestPermission()
        {
            using var request = new AuthorizationRequest(AuthorizationOption.Alert | AuthorizationOption.Sound, false);
            while (!request.IsFinished) await Task.Yield();
            return request.Granted;
        }
        public void Cancel(string id) => iOSNotificationCenter.RemoveScheduledNotification(id);
        public void Schedule(string id, DateTimeOffset at, string title, string body)
        {
            iOSNotificationCenter.ScheduleNotification(new iOSNotification
            {
                Identifier = id, Title = title, Body = body, ShowInForeground = false,
                Trigger = new iOSNotificationTimeIntervalTrigger { TimeInterval = at - DateTimeOffset.UtcNow, Repeats = false }
            });
        }
    }
}
#endif
