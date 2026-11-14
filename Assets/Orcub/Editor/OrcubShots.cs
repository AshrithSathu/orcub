using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Orcub.Game;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Orcub.EditorTools
{
    /// <summary>
    /// Dev tool: in play mode, walks every screen and card and saves a screenshot of each at the Game view's size
    /// (set it to a phone, e.g. iPhone 12 Pro 1170x2532). Used for design review passes.
    /// </summary>
    public static class OrcubShots
    {
        const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;

        struct Step { public float Wait; public Action Do; public string Shot; }

        static readonly Queue<Step> Steps = new Queue<Step>();
        static double _next;
        static string _dir;
        public static bool Running => Steps.Count > 0;
        public static string LastDir => _dir;

        [MenuItem("Orcub/Capture All Screens")]
        static void Menu() => Run(Path.GetFullPath("ArtSource/.automation/shots"));

        [MenuItem("Orcub/Capture Account Screens")]
        static void MenuAccount() => Run(Path.GetFullPath("ArtSource/.automation/shots"), accountOnly: true);

        public static void Run(string dir) => Run(dir, false);

        public static void Run(string dir, bool accountOnly)
        {
            if (!EditorApplication.isPlaying) { Debug.LogWarning("[Orcub] enter play mode first"); return; }
            _dir = dir;
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.GetFiles(dir, accountOnly ? "a*.png" : "*.png")) File.Delete(f);
            Application.runInBackground = true;
            Steps.Clear();

            var bv = UnityEngine.Object.FindFirstObjectByType<BoardView>();
            void Call(string method, params object[] args) => bv.GetType().GetMethod(method, Flags).Invoke(bv, args);
            Action<string, object[]> call = (m, a) => Call(m, a);
            LevelHud Hud() => UnityEngine.Object.FindFirstObjectByType<LevelHud>();
            void Click(string name)
            {
                foreach (var b in UnityEngine.Object.FindObjectsByType<Button>(FindObjectsSortMode.None))
                    if (b.name == name && b.isActiveAndEnabled)
                    {
                        ExecuteEvents.Execute(b.gameObject, new PointerEventData(EventSystem.current), ExecuteEvents.pointerClickHandler);
                        return;
                    }
            }

            if (accountOnly) { AddAccount(bv, call); Start(); return; }
            Add(0f, () => Call("ShowHome"), null);
            Add(0.8f, null, "01_home");
            Add(0f, () => Call("ShowChapters"), null);
            Add(0.8f, null, "02_chapters");
            Add(0f, () => Call("ShowMap", Chapters.Of(Chapters.Frontier)), null);
            Add(0.9f, null, "03_map");
            Add(0f, () =>
            {
                var map = UnityEngine.Object.FindFirstObjectByType<MapScreen>();
                var sheet = (LevelStartSheet)map.GetType().GetField("_sheet", Flags).GetValue(map);
                sheet.Open(Chapters.Frontier);
            }, null);
            Add(0.7f, null, "04_level_start");
            Add(0f, () => Call("PlayLevel", Chapters.Frontier), null);
            Add(2.2f, null, "05_game");
            Add(0f, () => Click("Hint"), null);
            Add(0.45f, null, "06_hint");
            Add(0f, () => Click("Undo"), null);
            Add(0.4f, null, "07_toast");
            Add(1.8f, () => Call("OpenPause"), null);
            Add(0.8f, null, "08_pause");
            Add(0f, () => Hud().ShowLifeConfirm("Restart level?", "Restart", () => { }, () => { }), null);
            Add(0.8f, null, "10_life_confirm");
            Add(0f, () => Hud().ShowOutOfLives(() => { }, null, () => { }), null);
            Add(0.8f, null, "11_out_of_lives");
            Add(0f, () => { Hud().ClosePopup(); bv.GetType().GetField("_wreath", Flags).GetValue(bv).GetType().GetMethod("Show").Invoke(bv.GetType().GetField("_wreath", Flags).GetValue(bv), null); Hud().ShowOutOfMoves(5, true, () => { }, () => { }, () => { }); }, null);
            Add(1.0f, null, "12_out_of_moves");
            Add(0f, () => { Hud().ClosePopup(); Hud().ShowWin(Chapters.Frontier + 1, 2, () => { }, () => { }); }, null);
            Add(1.0f, null, "13_win");
            Add(0f, () => Hud().ClosePopup(), null);
            AddAccount(bv, call);
            Start();
        }

        /// <summary>
        /// Account / privacy screens (approved account_v1 + account_v2). Signed-in states use
        /// SettingsScreen.Fake: the editor can't complete a native Google sign-in.
        /// </summary>
        static void AddAccount(BoardView bv, Action<string, object[]> call)
        {
            var kit = (UiKit)bv.GetType().GetField("_kit", Flags).GetValue(bv);
            AccountFlow Flow() => UnityEngine.Object.FindFirstObjectByType<AccountFlow>();
            var signedIn = new SettingsScreen.View { Guest = false, Provider = "Google", Email = "alex@gmail.com", Saved = DateTime.UtcNow };
            var guest = new SettingsScreen.View { Guest = true };
            void Clear()
            {
                foreach (var d in UnityEngine.Object.FindObjectsByType<AccountDialog>(FindObjectsSortMode.None)) UnityEngine.Object.Destroy(d.gameObject);
                foreach (var o in UnityEngine.Object.FindObjectsByType<AccountOverlay>(FindObjectsSortMode.None)) UnityEngine.Object.Destroy(o.gameObject);
                foreach (var s in UnityEngine.Object.FindObjectsByType<SettingsScreen>(FindObjectsSortMode.None)) UnityEngine.Object.Destroy(s.gameObject);
                foreach (var l in UnityEngine.Object.FindObjectsByType<LoaderScreen>(FindObjectsSortMode.None)) UnityEngine.Object.Destroy(l.gameObject);
                var f = Flow();
                f.GetType().GetField("_settings", Flags).SetValue(f, null);
                f.GetType().GetField("_sheet", Flags).SetValue(f, null);
            }
            void Settings(SettingsScreen.View view) { Clear(); SettingsScreen.Fake = view; Flow().OpenSettings(); }

            Add(0f, () => { call("ShowHome", new object[0]); Settings(guest); }, null);
            Add(0.7f, null, "a01_settings_guest");
            Add(0f, () => Settings(signedIn), null);
            Add(0.7f, null, "a02_settings_signed_in");
            Add(0f, () => { Settings(guest); }, null);
            Add(0.6f, () => Flow().Settings.ShowSigningIn(), null);
            Add(0.3f, null, "a03_signing_in");
            Add(0f, () => Flow().Settings.ShowSignInFailed(), null);
            Add(0.5f, null, "a04_signin_failed");
            Add(0f, () => Settings(signedIn), null);
            Add(0.6f, () => Flow().Settings.OpenSignOutConfirm(), null);
            Add(0.6f, null, "a05_signout_confirm");
            Add(0f, () => { Settings(signedIn); }, null);
            Add(0.6f, () => Flow().Settings.OpenDeleteConfirm(), null);
            Add(0.6f, null, "a06_delete_confirm");
            Add(0f, () => Flow().Settings.Dialog.SetBusy(true), null);
            Add(0.3f, null, "a07_delete_busy");
            Add(0f, () => { var d = Flow().Settings.Dialog; d.SetBusy(false); d.ShowError(); }, null);
            Add(0.3f, null, "a08_delete_error");
            Add(0f, () => { Clear(); SettingsScreen.Fake = null; Flow().ShowTracking(null); }, null);
            Add(0.7f, null, "a09_att_preprompt");
            Add(0f, () => { Clear(); Flow().ShowSaveNudge(null); }, null);
            Add(0.7f, null, "a10_save_nudge");
            Add(0f, () => { Clear(); Flow().ShowCloudRestore(new SaveSummary(42, 96), new SaveSummary(12, 30), _ => { }); }, null);
            Add(0.7f, null, "a11_cloud_restore");
            Add(0f, () => { Clear(); Flow().ShowWelcomeBack(new SaveSummary(42, 96)); }, null);
            Add(0.6f, null, "a12_welcome_back");
            Add(0f, () => { Clear(); LoaderScreen.ShowStatic(bv.transform, kit, 0.66f); }, null);
            Add(0.4f, null, "a13_loader_online");
            Add(0f, () => { Clear(); call("ShowHome", new object[0]); Flow().ShowOffline(); }, null);
            Add(0.6f, null, "a14_loader_offline");
            Add(0f, () => { Clear(); SettingsScreen.Fake = null; }, null);
        }

        static void Start()
        {
            _next = EditorApplication.timeSinceStartup;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static void Add(float wait, Action act, string shot) => Steps.Enqueue(new Step { Wait = wait, Do = act, Shot = shot });

        static void Tick()
        {
            if (!EditorApplication.isPlaying) { Steps.Clear(); EditorApplication.update -= Tick; return; }
            if (EditorApplication.timeSinceStartup < _next) return;
            if (Steps.Count == 0) { EditorApplication.update -= Tick; Debug.Log("[Orcub] shots done: " + _dir); return; }
            var s = Steps.Dequeue();
            try { s.Do?.Invoke(); }
            catch (Exception e) { Debug.LogWarning("[Orcub] shot step failed: " + (e.InnerException ?? e).Message); }
            if (s.Shot != null) ScreenCapture.CaptureScreenshot(Path.Combine(_dir, s.Shot + ".png"));
            _next = EditorApplication.timeSinceStartup + (Steps.Count > 0 ? Steps.Peek().Wait : 0f) + 0.05f;
        }
    }
}
