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

        struct Step { public float Wait; public Action Do; public string Shot; public Func<bool> Until; }

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
            Add(0f, () => Hud().ShowOutOfLives(() => { }, () => System.Threading.Tasks.Task.FromResult(false), () => { }, () => { }), null);
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
        static void AddUntil(Func<bool> until, Action act = null) => Steps.Enqueue(new Step { Wait = 0f, Do = act, Until = until });

        // ------------------------------------------------------------------ gear mechanics

        /// <summary>One board per mechanic (chapter tutorial with its intro card + a mid-chapter level), states.</summary>
        static readonly (int index, string name)[] MechanicLevels =
        {
            (20, "ratchet"), (40, "linked"), (60, "colour"), (80, "key"), (100, "rust"), (120, "spring"), (140, "clay"), (160, "mix"),
        };

        [MenuItem("Orcub/Capture Mechanics")]
        static void MenuMechanics() => RunMechanics(Path.GetFullPath("ArtSource/.automation/shots"));

        /// <summary>
        /// Mechanic captures at the Game view's size (set it to iPhone 12 Pro, 1170x2532): every chapter's
        /// "New gear!" card, mid-chapter boards, live states (refusal, open spring, cracked clay, unlocked padlock,
        /// spreading/frozen rust, linked turn), a boss board + level-start sheet, an infinite level, the pause "i".
        /// </summary>
        public static void RunMechanics(string dir)
        {
            if (!EditorApplication.isPlaying) { Debug.LogWarning("[Orcub] enter play mode first"); return; }
            _dir = dir;
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.GetFiles(dir, "m*.png")) File.Delete(f);
            Application.runInBackground = true;
            Steps.Clear();
            var bv = UnityEngine.Object.FindFirstObjectByType<BoardView>();
            object Field(string n) => bv.GetType().GetField(n, Flags).GetValue(bv);
            object Call(string method, params object[] args) => bv.GetType().GetMethod(method, Flags | BindingFlags.Public).Invoke(bv, args);
            LevelHud Hud() => UnityEngine.Object.FindFirstObjectByType<LevelHud>();
            var saved = SaveProgress();
            void Play(int index)
            {
                Orcub.Core.Lives.Refill();
                Hud().ClosePopup();
                Call("PlayLevel", index);
            }
            int Ring(Func<Orcub.Core.Ring, bool> p)
            {
                foreach (var r in bv.Model.Rings) if (!r.Removed && p(r)) return r.Id;
                return -1;
            }
            void Spin(int ring, float degrees) => bv.DebugSpin(ring, degrees);
            void HideWreath() { var w = Field("_wreath"); w?.GetType().GetMethod("Hide").Invoke(w, null); }
            // L9: the intro flags this tool clears are put back afterwards
            var introKeys = new System.Collections.Generic.List<string>();
            foreach (var (_, n) in MechanicLevels) introKeys.Add("orcub.intro." + (n == "colour" ? "colour_clamp" : n == "key" ? "key_padlock" : n == "rust" ? "rust_oil" : n));
            var introSaved = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var key in introKeys) introSaved[key] = PlayerPrefs.HasKey(key) ? PlayerPrefs.GetInt(key) : int.MinValue;

            AddUntil(() => UnityEngine.Object.FindFirstObjectByType<LoaderScreen>() == null && Field("_splash") == null);   // boot done
            Add(0f, () => { foreach (var key in introKeys) PlayerPrefs.DeleteKey(key); }, null);
            int k = 1;
            foreach (var (index, name) in MechanicLevels)
            {
                Add(0f, () => Play(index), null);
                Add(2.6f, null, $"m{k++:00}_{name}_intro");
                Add(0f, () => Hud().ClosePopupAndNotify(), null);
                Add(0.7f, null, $"m{k++:00}_{name}_tutorial");
            }
            // mid-chapter boards and live states
            Add(0f, () => Play(26), null);
            Add(2.2f, null, $"m{k++:00}_ratchet_level27");
            Add(0f, () => Call("RefuseTurn", Ring(r => r.Ratchet != 0)), null);
            Add(0.12f, null, $"m{k++:00}_ratchet_refused");
            Add(0f, () => Play(47), null);
            Add(2.2f, null, $"m{k++:00}_linked_level48");
            Add(0f, () => Spin(Ring(r => r.GearGroup > 0 && bv.Model.CanRotate(r.Id)), 120f), null);
            Add(0.15f, null, $"m{k++:00}_linked_turning");
            Add(0f, () => Play(66), null);
            Add(2.2f, null, $"m{k++:00}_colour_level67");
            Add(0f, () => Play(88), null);
            Add(2.2f, null, $"m{k++:00}_key_level89");
            Add(0f, () =>
            {   // pull the key carrier straight out (its holders let go) to show the padlock popping open
                var m = bv.Model;
                int carrier = Ring(r => r.KeyId > 0);
                foreach (var l in m.Links) if (l.Active && (l.Owner == carrier || l.Target == carrier)) m.ReleaseLink(l.Id);
                Call("PullOut", carrier, UnityEngine.Vector2.up, 10f);
            }, null);
            Add(0.25f, null, $"m{k++:00}_key_unlocking");
            Add(0f, () => Play(107), null);
            Add(2.2f, null, $"m{k++:00}_rust_level108");
            Add(0f, () => { var m = bv.Model; int r = Ring(x => x.RustAfter > 0); while (m.Rings[r].RustAfter - m.Moves > 1) m.CompleteMove(); }, null);
            Add(0.5f, null, $"m{k++:00}_rust_last_move");
            Add(0f, () => bv.Model.CompleteMove(), null);
            Add(0.5f, null, $"m{k++:00}_rust_frozen");
            Add(0.9f, null, $"m{k++:00}_rust_dead_end_card");   // M5: frozen with clamps on it -> the fail card
            // owner correction: rust must read on every palette colour (orange 8, amber 1, pale yellow 7, coral 0, ...)
            Add(0f, () => { Hud().ClosePopup(); HideWreath(); bv.Build(RustSampler(new[] { 8, 1, 7, 0, 5, 3 }, 3)); Call("UpdateHud"); }, null);
            Add(0.6f, null, $"m{k++:00}_rust_colours_early");
            Add(0f, () => { var m = bv.Model; while (m.Moves < 2) m.CompleteMove(); }, null);   // deadlines 3/4/5: 1, 2 and 3 left
            Add(0.6f, null, $"m{k++:00}_rust_colours_last_move");
            Add(0f, () => { var m = bv.Model; while (m.Moves < 5) m.CompleteMove(); }, null);
            Add(0.6f, null, $"m{k++:00}_rust_colours_frozen");
            Add(0f, () => Play(126), null);
            Add(2.2f, null, $"m{k++:00}_spring_level127");
            Add(0f, () => { var m = bv.Model; foreach (var l in m.Links) if (l.Active && l.SpringMoves > 0) { m.ReleaseLink(l.Id); break; } }, null);
            Add(0.6f, null, $"m{k++:00}_spring_open");
            Add(0f, () => Play(146), null);
            Add(2.2f, null, $"m{k++:00}_clay_level147");
            Add(0f, () => Play(140), null);   // the tutorial board: its coated clamp's ring can turn
            Add(2.4f, () => Hud().ClosePopupAndNotify(), null);
            Add(0.6f, null, $"m{k++:00}_clay_intact");
            Add(0f, () =>
            {   // one pass of the gap over a clay coat: it cracks
                var m = bv.Model;
                foreach (var l in m.Links)
                    if (l.Active && l.Clay && m.CanRotate(l.Target))
                    {
                        var t = m.Rings[l.Target];
                        float d = UnityEngine.Mathf.DeltaAngle(t.GapAngle, l.ClampAngle);
                        if (!m.DirectionAllowed(l.Target, d)) d -= UnityEngine.Mathf.Sign(d) * 360f;
                        m.Rotate(l.Target, d);
                        break;
                    }
            }, null);
            Add(0.5f, null, $"m{k++:00}_clay_cracked");
            Add(0f, () => { Hud().ClosePopup(); HideWreath(); bv.Build(LinkedSampler()); Call("UpdateHud"); }, null);
            Add(0.6f, null, $"m{k++:00}_linked_pairs_tinted");
            Add(0f, () => Play(185), null);
            Add(2.2f, null, $"m{k++:00}_mix_level186");
            Add(0f, () => Call("OpenPause"), null);
            Add(0.8f, null, $"m{k++:00}_pause_info");
            Add(0f, () => { Hud().ClosePopup(); Play(39); }, null);
            Add(2.6f, null, $"m{k++:00}_boss_level40");
            Add(0f, () => { Hud().ClosePopup(); Call("ShowMap", 1); }, null);
            Add(0.6f, () => SheetOpen(39), null);
            Add(0.7f, null, $"m{k++:00}_boss_sheet");
            Add(0f, () => { LevelRewards.Live = true; SheetOpen(39); }, null);
            Add(0.7f, null, $"m{k++:00}_boss_sheet_reward_preview");
            Add(0f, () => { LevelRewards.Live = false; SheetOpen(436); }, null);
            Add(0.9f, null, $"m{k++:00}_infinite_sheet_437");
            Add(0f, () => Play(436), null);
            AddUntil(() => bv.LevelIndex == 436 && bv.Model != null && Field("_loading") == null);
            Add(2.4f, null, $"m{k++:00}_infinite_level437");
            Add(0f, () => Play(459), null);
            AddUntil(() => Field("_loading") == null);
            Add(2.6f, null, $"m{k++:00}_infinite_boss460");
            // out-of-lives sheet (approved out_of_lives_gold_B): idle, pending gold refill, failed refill
            var gate = new System.Threading.Tasks.TaskCompletionSource<bool>();
            Add(0f, () => Hud().ShowOutOfLives(() => { }, () => gate.Task, () => { }, () => { }), null);
            Add(0.9f, null, $"m{k++:00}_out_of_lives_gold");
            Add(0f, () => ClickButton("Refill for 90 gold"), null);
            Add(0.3f, null, $"m{k++:00}_out_of_lives_gold_pending");
            Add(0f, () => gate.TrySetResult(false), null);
            Add(0.3f, null, $"m{k++:00}_out_of_lives_gold_failed");
            Add(0f, () =>
            {
                Hud().ClosePopup();
                RestoreProgress(saved);
                foreach (var kv in introSaved)
                    if (kv.Value == int.MinValue) PlayerPrefs.DeleteKey(kv.Key); else PlayerPrefs.SetInt(kv.Key, kv.Value);
                PlayerPrefs.Save();
            }, null);
            Start();
        }

        static void ClickButton(string name)
        {
            foreach (var b in UnityEngine.Object.FindObjectsByType<Button>(FindObjectsSortMode.None))
                if (b.name == name && b.isActiveAndEnabled) { b.onClick.Invoke(); return; }
        }

        /// <summary>A sampler board: one rusting ring per palette colour, each held by a clamp from a plain ring.</summary>
        static Orcub.Core.LevelDef RustSampler(int[] colours, int rustAfter)
        {
            var d = new Orcub.Core.LevelDef { Name = "Rust sampler", Mechanics = new[] { "rust" } };
            for (int i = 0; i < colours.Length; i++)
            {
                float x = (i % 2) * 4.2f - 2.1f, y = 6f - (i / 2) * 4.2f;
                d.Rings.Add(new Orcub.Core.RingDef(x, y, 1.25f, colours[i], 90f) { RustAfter = rustAfter + (i % 3) });
            }
            for (int i = 0; i < colours.Length; i += 2) d.Links.Add(new Orcub.Core.LinkDef(i, i + 1));
            return d;
        }

        /// <summary>Three linked pairs on one board (tinted marks, pair numbers) plus plain rings.</summary>
        static Orcub.Core.LevelDef LinkedSampler()
        {
            var d = new Orcub.Core.LevelDef { Name = "Linked sampler", Mechanics = new[] { "linked" } };
            int[] colours = { 3, 8, 4, 2, 0, 6 };
            for (int i = 0; i < 6; i++)
            {
                float x = (i % 2) * 4.2f - 2.1f, y = 6f - (i / 2) * 4.2f;
                d.Rings.Add(new Orcub.Core.RingDef(x, y, i < 2 ? 1.0f : 1.3f, colours[i], 0f) { GearGroup = i / 2 + 1 });
            }
            return d;
        }

        static void SheetOpen(int index)
        {
            var map = UnityEngine.Object.FindFirstObjectByType<MapScreen>(FindObjectsInactive.Include);
            var sheet = (LevelStartSheet)map.GetType().GetField("_sheet", Flags).GetValue(map);
            sheet.Open(index);
        }

        [MenuItem("Orcub/Verify Mechanics Autosolve")]
        static void MenuVerify() => VerifyAutosolve(new[] { 20, 26, 40, 47, 60, 66, 80, 88, 100, 107, 120, 126, 140, 146, 160, 185, 39, 199, 436, 459 });

        /// <summary>Plays each level in play mode with the dev autosolve (real input paths) and logs the result.</summary>
        public static void VerifyAutosolve(int[] levels)
        {
            if (!EditorApplication.isPlaying) { Debug.LogWarning("[Orcub] enter play mode first"); return; }
            Application.runInBackground = true;
            Steps.Clear();
            _dir = Path.GetFullPath("ArtSource/.automation/shots");
            var bv = UnityEngine.Object.FindFirstObjectByType<BoardView>();
            var saved = SaveProgress();
            var report = new System.Text.StringBuilder();
            foreach (int index in levels)
            {
                double started = 0;
                Add(0f, () =>
                {
                    Orcub.Core.Lives.Refill();
                    UnityEngine.Object.FindFirstObjectByType<LevelHud>().ClosePopup();
                    bv.GetType().GetMethod("PlayLevel", Flags).Invoke(bv, new object[] { index });
                }, null);
                AddUntil(() => bv.LevelIndex == index && bv.GetType().GetField("_loading", Flags).GetValue(bv) == null);
                Add(0.3f, () => { started = EditorApplication.timeSinceStartup; bv.StartAutoSolve(0.05f); }, null);
                AddUntil(() => bv.AutoSolveResult != null || EditorApplication.timeSinceStartup - started > 90, () =>
                {
                    string line = $"level {index + 1}: {bv.AutoSolveResult ?? "timeout"} (moves used {bv.Model.Moves}, solved {bv.Model.IsSolved})";
                    report.AppendLine(line);
                    Debug.Log("[Orcub] verify " + line);
                });
                Add(1.3f, null, null);
            }
            Add(0f, () =>
            {
                UnityEngine.Object.FindFirstObjectByType<LevelHud>().ClosePopup();
                RestoreProgress(saved);
                File.WriteAllText(Path.Combine(_dir, "mechanics_autosolve.txt"), report.ToString());
                Debug.Log("[Orcub] autosolve verification:\n" + report);
            }, null);
            Start();
        }

        /// <summary>Dev tools play and win levels; put the editor's saved progress back afterwards.</summary>
        static System.Collections.Generic.Dictionary<string, int> SaveProgress()
        {
            var d = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var key in new[] { "orcub.level", "orcub.highest" }) d[key] = PlayerPrefs.GetInt(key, -1);
            for (int i = 0; i < 480; i++) d["orcub.stars." + i] = PlayerPrefs.GetInt("orcub.stars." + i, -1);
            return d;
        }

        static void RestoreProgress(System.Collections.Generic.Dictionary<string, int> d)
        {
            foreach (var kv in d)
                if (kv.Value < 0) PlayerPrefs.DeleteKey(kv.Key);
                else PlayerPrefs.SetInt(kv.Key, kv.Value);
            PlayerPrefs.Save();
        }

        static void Tick()
        {
            if (!EditorApplication.isPlaying) { Steps.Clear(); EditorApplication.update -= Tick; return; }
            if (EditorApplication.timeSinceStartup < _next) return;
            if (Steps.Count == 0) { EditorApplication.update -= Tick; Debug.Log("[Orcub] shots done: " + _dir); return; }
            if (Steps.Peek().Until != null)
            {
                bool ready;
                try { ready = Steps.Peek().Until(); } catch { ready = true; }
                if (!ready) return;
            }
            var s = Steps.Dequeue();
            try { s.Do?.Invoke(); }
            catch (Exception e) { Debug.LogWarning("[Orcub] shot step failed: " + (e.InnerException ?? e).Message); }
            if (s.Shot != null) ScreenCapture.CaptureScreenshot(Path.Combine(_dir, s.Shot + ".png"));
            _next = EditorApplication.timeSinceStartup + (Steps.Count > 0 ? Steps.Peek().Wait : 0f) + 0.05f;
        }
    }
}
