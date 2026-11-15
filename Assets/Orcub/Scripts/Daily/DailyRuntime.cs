using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Orcub.Game;
using Orcub.Net;
using Orcub.Store;
using UnityEngine;

namespace Orcub.Daily
{
    public sealed class DailyRuntime : MonoBehaviour
    {
        public static Func<ILocalNotifications> NotificationsFactory = () => new UnavailableNotifications();
        public static DailyServices Current { get; private set; }
        string owner;
        bool openPending;
        DateTimeOffset? fullAt;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Boot() => new GameObject("OrcubDaily").AddComponent<DailyRuntime>();
        void Awake()
        {
            DontDestroyOnLoad(gameObject);
            ClientGateway.DailyCapture=Capture;
            ClientGateway.DailyRestore=Restore;
            ClientGateway.AppOpened=Opened;
            ClientGateway.Flushed=Retry;
        }
        bool Ensure()
        {
            var client=ClientServices.Instance;
            if(client==null || !client.Ready || client.Stopped) return false;
            if(owner!=client.PlayerId || Current==null) {
                Current?.Deactivate();
                owner=client.PlayerId; Current=new DailyServices(owner,NotificationsFactory());
                var active=Current;
                var account=owner;
                active.Context.Changed+=()=> { if(Current==active && account==client.PlayerId && !client.Stopped) client.Account?.RequestSave(); };
                active.Wallet.WalletChanged+=async _=> {
                    if(Current!=active || owner!=client.PlayerId || StoreRuntime.Service==null) return;
                    try { await StoreRuntime.Service.Wallet.Refresh(); } catch(Exception) { }
                };
            }
            return true;
        }
        string Capture() => Ensure() ? DailyPersistence.Capture(Current.Context.State) : null;
        void Restore(string json)
        {
            if(!Ensure()) return;
            var merged=SaveSnapshot.MergeDaily(JObject.Parse(Capture()),JObject.Parse(json));
            DailyPersistence.Save(owner,DailyPersistence.Restore(merged.ToString()));
            Current.Deactivate(); Current=null; Ensure();
        }
        void Opened(DateTimeOffset? livesFullAt) { fullAt=livesFullAt; openPending=true; Update(); }
        void Update()
        {
            if(!Ensure()) return;
            if(openPending && ClientServices.Instance.Account?.Linked==true) { openPending=false; Current.OnAppOpen(fullAt); }
        }
        async void Retry()
        {
            if(!Ensure() || ClientServices.Instance.Account?.Linked!=true) return;
            var active=Current;
            try { await active.Puzzle.RetryRewards(); } catch(Exception) { }
        }
        void OnDestroy()
        {
            ClientGateway.DailyCapture=null; ClientGateway.DailyRestore=null;
            ClientGateway.AppOpened=null; ClientGateway.Flushed=null; Current?.Deactivate(); Current=null;
        }
        sealed class UnavailableNotifications : ILocalNotifications
        {
            public bool Authorized=>false;
            public string OpenedId=>null;
            public Task<bool> RequestPermission()=>Task.FromResult(false);
            public void Cancel(string id) { }
            public void Schedule(string id,DateTimeOffset at,string title,string body) { }
        }
    }
}
