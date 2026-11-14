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

        public static void Run(string dir)
        {
            if (!EditorApplication.isPlaying) { Debug.LogWarning("[Orcub] enter play mode first"); return; }
            _dir = dir;
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.GetFiles(dir, "*.png")) File.Delete(f);
            Application.runInBackground = true;
            Steps.Clear();

            var bv = UnityEngine.Object.FindFirstObjectByType<BoardView>();
            void Call(string method, params object[] args) => bv.GetType().GetMethod(method, Flags).Invoke(bv, args);
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
            Add(0f, () => Hud().ShowSettings(() => { }), null);
            Add(0.8f, null, "09_settings");
            Add(0f, () => Hud().ShowLifeConfirm("Restart level?", "Restart", () => { }, () => { }), null);
            Add(0.8f, null, "10_life_confirm");
            Add(0f, () => Hud().ShowOutOfLives(() => { }, null, () => { }), null);
            Add(0.8f, null, "11_out_of_lives");
            Add(0f, () => { Hud().ClosePopup(); bv.GetType().GetField("_wreath", Flags).GetValue(bv).GetType().GetMethod("Show").Invoke(bv.GetType().GetField("_wreath", Flags).GetValue(bv), null); Hud().ShowOutOfMoves(5, true, () => { }, () => { }, () => { }); }, null);
            Add(1.0f, null, "12_out_of_moves");
            Add(0f, () => { Hud().ClosePopup(); Hud().ShowWin(Chapters.Frontier + 1, 2, () => { }, () => { }); }, null);
            Add(1.0f, null, "13_win");
            Add(0f, () => { Hud().ClosePopup(); Call("ShowHome"); Hud().ShowSettings(() => { }); }, null);
            Add(0.8f, null, "14_home_settings");
            Add(0f, () => { Hud().ClosePopup(); LoaderScreen.Show(bv.transform, (UiKit)bv.GetType().GetField("_kit", Flags).GetValue(bv), () => { }); }, null);
            Add(0.35f, null, "15_loader");

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
