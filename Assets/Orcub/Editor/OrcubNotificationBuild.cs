using Unity.Notifications;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
namespace Orcub.EditorTools
{
    public sealed class OrcubNotificationBuild : IPreprocessBuildWithReport
    {
        public int callbackOrder => -1000;
        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.iOS) return;
            NotificationSettings.iOSSettings.RequestAuthorizationOnAppLaunch = false;
            NotificationSettings.iOSSettings.AddRemoteNotificationCapability = false;
            NotificationSettings.iOSSettings.NotificationRequestAuthorizationForRemoteNotificationsOnAppLaunch = false;
        }
    }
}
