using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Orcub.Game
{
    /// <summary>
    /// Owns the account / privacy moments around home and the win card:
    ///   boot      loader result → offline status chip, or cloud restore when the cloud and this phone differ
    ///   win       tracking pre-prompt right before iOS asks (first completed level, ATT undetermined only),
    ///             then the guest save nudge (after level 5, and once more after level 20; never again)
    ///   home      Settings screen; welcome-back card after a cloud save is restored
    /// BoardView keeps only a handful of calls into this.
    /// </summary>
    public sealed class AccountFlow : MonoBehaviour
    {
        const string Nudge5Key = "orcub.nudge.5", Nudge20Key = "orcub.nudge.20";

        UiKit _kit;
        HomeScreen _home;
        SettingsScreen _settings;
        AccountOverlay _sheet;
        int _deferredWin = -1;
        bool _booted;

        public static AccountFlow Create(Transform parent, UiKit kit, HomeScreen home)
        {
            var f = new GameObject("AccountFlow").AddComponent<AccountFlow>();
            f.transform.SetParent(parent, false);
            f._kit = kit;
            f._home = home;
            return f;
        }

        static IAccountService Svc => AccountGateway.Service;

        // ------------------------------------------------------------------ home

        public void OpenSettings()
        {
            if (_settings != null) return;
            _settings = SettingsScreen.Open(transform, _kit, () => _settings = null);
        }

        public SettingsScreen Settings => _settings;

        /// <summary>Loader finished: home is showing. Offline gets the status chip; conflicts are handled in Update.</summary>
        public void AfterBoot(BootResult result)
        {
            _booted = true;
            if (result.Offline && _home != null) ShowOffline();
        }

        public void ShowOffline() => _home.ShowStatus(OfflineIcon, "Offline", "Playing on this phone", false);

        public void ShowWelcomeBack(SaveSummary s) =>
            _home.ShowStatus(a => Fill(a, AccountArt.CloudCheck), "Welcome back", $"Level {s.Level} · {s.Stars}", true);

        void OfflineIcon(RectTransform area)
        {
            // a completed coral/mint gear ring: local play is ready, nothing is waiting
            var ring = UiKit.Rect("Ring", area);
            UiKit.Anchor(ring, 0.18f, 0.06f, 0.82f, 0.94f);
            ring.gameObject.AddComponent<UnityEngine.UI.AspectRatioFitter>().aspectMode = UnityEngine.UI.AspectRatioFitter.AspectMode.FitInParent;
            var coral = _kit.MakeImage(ring, _kit.Art.homeGearRing, UiKit.Coral);
            UiKit.Anchor(coral.rectTransform, 0, 0, 1, 1);
            var mint = _kit.MakeImage(ring, _kit.Art.homeGearRing, UiKit.Mint);
            UiKit.Anchor(mint.rectTransform, 0, 0, 1, 1);
            mint.type = UnityEngine.UI.Image.Type.Filled;
            mint.fillMethod = UnityEngine.UI.Image.FillMethod.Radial360;
            mint.fillOrigin = (int)UnityEngine.UI.Image.Origin360.Right;
            mint.fillAmount = 0.35f;
        }

        static void Fill(RectTransform area, Sprite sprite)
        {
            var img = new GameObject(sprite != null ? sprite.name : "Icon", typeof(RectTransform)).AddComponent<UnityEngine.UI.Image>();
            img.rectTransform.SetParent(area, false);
            img.sprite = sprite;
            img.preserveAspect = true;
            img.raycastTarget = false;
            UiKit.Anchor(img.rectTransform, 0, 0, 1, 1);
        }

        void Update()
        {
            if (!_booted || _sheet != null || _home == null || !_home.Visible) return;
            var svc = Svc;
            if (svc != null && svc.HasConflict)
            {
                _settings?.Close();
                ShowCloudRestore(svc.Cloud, svc.Local, keepCloud => _ = svc.ResolveConflict(keepCloud));
                return;
            }
            if (AccountGateway.Boot.Restored.HasValue)
            {
                var s = AccountGateway.Boot.Restored.Value;
                AccountGateway.Boot.Restored = null;
                _home.Refresh();
                ShowWelcomeBack(s);
            }
        }

        public void ShowCloudRestore(SaveSummary cloud, SaveSummary phone, Action<bool> resolve)
        {
            _sheet = AccountSheets.CloudRestore(transform, _kit, cloud, phone, keepCloud =>
            {
                _sheet = null;
                resolve?.Invoke(keepCloud);
                if (_home == null) return;
                _home.Refresh();
                if (keepCloud) ShowWelcomeBack(cloud);
            });
        }

        // ------------------------------------------------------------------ win

        /// <summary>
        /// Replaces the direct AdsGateway RecordWin call. When the tracking pre-prompt is due, the ads service only
        /// learns about the win (and so starts its UMP → ATT sequence) after the player taps Continue.
        /// </summary>
        public void RecordWin(int levelNumber)
        {
            if (TrackingPromptDue) { _deferredWin = levelNumber; return; }
            AdsGateway.Service?.RecordWin(levelNumber);
        }

        /// <summary>Runs the win card's Next / Home action after any due sheet (tracking first, then the nudge).</summary>
        public void AfterWin(int levelNumber, Action then)
        {
            if (_deferredWin >= 0)
            {
                int win = _deferredWin;
                _deferredWin = -1;
                ShowTracking(() => { AdsGateway.Service?.RecordWin(win); then?.Invoke(); });
                return;
            }
            if (NudgeDue(levelNumber)) { ShowSaveNudge(then); return; }
            then?.Invoke();
        }

        public void ShowTracking(Action then)
        {
            _sheet = AccountSheets.Tracking(transform, _kit, () => { _sheet = null; then?.Invoke(); });
        }

        public void ShowSaveNudge(Action then)
        {
            _sheet = AccountSheets.SaveNudge(transform, _kit, () => { _sheet = null; then?.Invoke(); });
        }

        /// <summary>Guests only; after level 5 and once after level 20, never more. Marks itself shown.</summary>
        static bool NudgeDue(int levelNumber)
        {
            var svc = Svc;
            if (svc == null || !svc.Ready || !svc.IsGuest) return false;
            if (levelNumber >= 20 && PlayerPrefs.GetInt(Nudge20Key, 0) == 0)
            {
                PlayerPrefs.SetInt(Nudge20Key, 1);
                PlayerPrefs.SetInt(Nudge5Key, 1);
                PlayerPrefs.Save();
                return true;
            }
            if (levelNumber >= 5 && levelNumber < 20 && PlayerPrefs.GetInt(Nudge5Key, 0) == 0)
            {
                PlayerPrefs.SetInt(Nudge5Key, 1);
                PlayerPrefs.Save();
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ tracking status

#if UNITY_IOS && !UNITY_EDITOR
        // Native bridge shipped by the ads module (Ads/Plugins/iOS/OrcubTracking.mm).
        [DllImport("__Internal")] static extern int OrcubTrackingStatus();
#endif

        [Serializable] sealed class AdsFlags { public bool adsEnabled = true; }

        static bool AdsEnabled
        {
            get
            {
                var json = Resources.Load<TextAsset>("AdsConfig");
                return json == null || JsonUtility.FromJson<AdsFlags>(json.text).adsEnabled;
            }
        }

        /// <summary>Only on a device whose ATT status is still notDetermined, with ads on.</summary>
        static bool TrackingPromptDue
        {
            get
            {
#if UNITY_IOS && !UNITY_EDITOR
                return AdsGateway.Service != null && AdsEnabled && OrcubTrackingStatus() == 0;
#else
                return false;
#endif
            }
        }
    }
}
