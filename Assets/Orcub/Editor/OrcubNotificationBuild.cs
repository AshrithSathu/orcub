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
            if(NotificationSettings.iOSSettings.RequestAuthorizationOnAppLaunch || NotificationSettings.iOSSettings.AddRemoteNotificationCapability || NotificationSettings.iOSSettings.NotificationRequestAuthorizationForRemoteNotificationsOnAppLaunch)
                throw new BuildFailedException("Disable iOS notification launch authorization and remote capability in Project Settings before exporting");
        }
    }
}
