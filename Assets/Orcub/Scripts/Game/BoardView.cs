using System;
using System.Collections.Generic;
using Orcub.Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Orcub.Game
{
    /// <summary>
    /// Owns the puzzle model and its views, and turns pointer input into moves:
    ///  - drag a ring that is not locked  -> rotate it around its centre
    ///  - tap a ring that is not locked   -> spin it a fixed step
    ///  - touch a locked ring             -> it shakes and its holders flash
    ///  - drag / tap a free ring          -> pull it out of the board
    /// Gear mechanics (README in ArtSource/mechanics): the model's CompleteMove() runs exactly once per counted
    /// gesture (drag on release, tap when its spin ends, locked touch at once); a ratchet turned the wrong way is
    /// refused with a shake and costs nothing; Undo rebuilds every mechanic view from the restored model.
    /// </summary>
    [ExecuteAlways]
    public sealed partial class BoardView : MonoBehaviour
    {
        enum DragMode { None, Rotate, Pull, Locked }

        [SerializeField] OrcubArt art;
        [SerializeField] Camera cam;
        [Tooltip("Degrees a single tap spins a ring (negative = clockwise).")]
        [SerializeField] float tapSpin = -60f;
        [SerializeField] float tapSpinSpeed = 420f;
        [SerializeField] float pullOutDistance = 1.0f;
        [SerializeField] float framePadding = 0.45f;
        [Tooltip("Play the animated logo splash before the first board.")]
        [SerializeField] bool playSplash = true;
        [Tooltip("Show DEV level skip buttons + level stats in the HUD (turn off for release).")]
        [SerializeField] bool devTools = true;

        const int ContinueMoves = 5;
        int _levelIndex;
        LevelRules _rules;
        int _movesUsed, _bonusMoves;

        // Adaptive difficulty: one attempt per LoadLevel, frozen plan, outcome recorded once.
        Adaptive _adaptive;
        LevelDef _def;
        bool _attemptOpen;
        float _attemptStart;
        int _attemptBoosters, _attemptContinues;
        AttemptPlan _plan;
        float _requiredBefore, _lastGesture, _thinkSeconds;
        int _gestures;
        bool _featurePending;
        int _lockedTouches;
        bool _usedAdContinue, _levelOver;
        float _winAt = -1f, _failCheckAt = -1f;
        int _winStars;
        LevelData _levelData;
        LevelHud _hud;
        WreathView _wreath;
        UiKit _kit;
        HomeScreen _home;
        AccountFlow _account;
        MapScreen _map;
        ChaptersScreen _chapters;
        readonly Stack<(ModelSnapshot snap, int moves)> _undo = new Stack<(ModelSnapshot, int)>();
        ModelSnapshot _pendingSnap;

        PuzzleModel _model;
        readonly List<RingView> _rings = new List<RingView>();
        readonly List<HolderView> _holders = new List<HolderView>();
        readonly SpinSet _spins = new SpinSet();   // tap spins: each tap's move completes when its spin stops
        readonly List<Link> _released = new List<Link>();
        readonly Dictionary<int, float> _turned = new Dictionary<int, float>(); // unwrapped rotation per ring, for ratchet detents
        GestureTracker _gesture;   // the drag in progress
        readonly List<RingMechanics> _mechs = new List<RingMechanics>();
        readonly List<(Link link, int passes, bool inGap)> _probe = new List<(Link, int, bool)>();
        float _refusedAt = -10f, _clampRefusedAt = -10f;
        readonly HashSet<string> _toldOnce = new HashSet<string>();
        LevelData _fallbackData;
        Coroutine _loading;
        LoaderScreen _loader;
        Transform _content;
        SpriteRenderer _glow;
        SpriteRenderer _backdrop;
        Feedback _fx;
        ClayCrumbs _crumbs;
        Rect _bounds;
        float _fitAspect;
        Rect _fitSafeArea;
        bool _inputLocked;
        SplashView _splash;
        int _session;              // bumped when a level start is cancelled (Home) or replaced: stale callbacks bail
        bool _boosterUnlockBusy;
        SpinSet.TurnFn _turnFn;

        // Drag state
        DragMode _mode;
        int _active = -1;
        Vector2 _pressWorld;
        float _pressTime;
        float _lastAngle;
        float _rotated;
        bool _refusedThisPress;    // M1: any sample of this press was refused by a ratchet
        Vector2 _pullVelocity;
        Vector2 _lastPointer;

        public PuzzleModel Model => _model;
        public OrcubArt Art { get => art; set => art = value; }
        public Camera Cam { get => cam; set => cam = value; }

        // ------------------------------------------------------------------ edit-mode preview
        // The board is generated from code, so without this the scene looks empty until you press Play.
        // The preview is rebuilt whenever the scene opens / scripts reload and is never saved into the scene.

        void OnEnable()
        {
            if (Application.isPlaying) return;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.delayCall += BuildPreview;
#endif
        }

        void OnDisable()
        {
            if (!Application.isPlaying) Clear();
        }

        void BuildPreview()
        {
            if (this == null || Application.isPlaying || !isActiveAndEnabled || art == null) return;
            if (cam == null) cam = Camera.main;
            Build();
            foreach (var t in _content.GetComponentsInChildren<Transform>(true))
                t.gameObject.hideFlags = HideFlags.DontSave | HideFlags.NotEditable;
        }

        void Start()
        {
            if (!Application.isPlaying) return;
            if (cam == null) cam = Camera.main;

            // Phones: run at the display's native refresh (120 Hz on ProMotion), never dim mid-puzzle.
            QualitySettings.vSyncCount = 0;
            int hz = Mathf.RoundToInt((float)Screen.currentResolution.refreshRateRatio.value);
            Application.targetFrameRate = Mathf.Max(60, hz);
            Screen.sleepTimeout = SleepTimeout.NeverSleep;

            _spins.Ended += _ => _failCheckAt = Mathf.Max(_failCheckAt, Time.time + 0.15f);   // L2: keep a longer dead-end beat
            _fx = Feedback.Create(transform, art);
            MechSprites.Warm();   // the procedural arc arrow, built now rather than on the first linked board
            _crumbs = ClayCrumbs.Create(transform, art.spriteMaterial);
            _hud = LevelHud.Create(transform, art, devTools && Application.isEditor);   // never on a phone
            _wreath = WreathView.Create(transform, art, cam);
            _hud.Pause += OpenPause;
            GameSettings.Apply();
            _hud.Prev += () => DevJump(-1);
            _hud.Next += () => DevJump(+1);
            _hud.Booster += UseBooster;
            _kit = new UiKit(art);
            _home = HomeScreen.Create(transform, _kit, _fx, onPlay: () => PlayLevel(Frontier), onMap: ShowChapters,
                onSettings: () => _account.OpenSettings(), onDaily: () => ShowDaily());
            _account = AccountFlow.Create(transform, _kit, _home);
            ShopFlow.Create(transform, _kit, () => _account.OpenSettings(), () => _hud != null && _hud.Shown, msg => _hud?.ShowToast(msg));
            InitTutorial();
            InitDaily();
            _map = MapScreen.Create(transform, _kit, onPlayLevel: PlayLevel, onBack: ShowChapters);
            _chapters = ChaptersScreen.Create(transform, _kit, onOpen: ShowMap, onBack: ShowHome);
            _home.Hide();

            // The frontier board is built (hidden) so the felt backdrop is there for the splash.
            Build(Frontier);
            HideRingsForIntro();
            _hud.SetVisible(false, instant: true);
            if (playSplash && art.logoLetters != null && art.logoLetters.Length > 0)
            {
                _splash = SplashView.Play(transform, art, cam, _fx, _crumbs,
                    onReveal: () => LoaderScreen.Show(transform, _kit, onDone: boot => { ShowHome(); _account.AfterBoot(boot); AppOpened(); }),
                    onDone: () => _splash = null);
            }
            else { ShowHome(); _account.AfterBoot(default); AppOpened(); }
        }

        // ------------------------------------------------------------------ app lifecycle (daily follow-ups)

        bool _booted, _paused;

        /// <summary>Boot and every foreground resume: the daily loop counts the session and retries pending rewards.</summary>
        void AppOpened()
        {
            _booted = true;
            ClientGateway.OpenApp(LivesClock.FullAt(DateTimeOffset.UtcNow));
            if (_home != null && _home.Visible) StartCoroutine(HomeMomentsSoon(false));   // today's gift, once settled
            _ = RewardClaims.Drain(OnRewardGranted);   // first-clear gold that failed to claim earlier
        }

        void OnApplicationPause(bool paused)
        {
            if (!Application.isPlaying) return;
            if (paused) { _paused = true; ClientGateway.Save?.Invoke(); return; }
            if (_paused && _booted) AppOpened();
            _paused = false;
        }

        // ------------------------------------------------------------------ screens

        /// <summary>The next level the player hasn't beaten (home's Play button).</summary>
        static int Frontier => Mathf.Max(0, Progress.HighestUnlocked);   // infinite saga: no upper bound

        void ShowHome()
        {
            // review i, L1: a tap spin or a pending fail check never finishes under Home (no card over Home)
            _failCheckAt = -1f;
            _spins.Clear();
            LeaveDaily();
            _dailyFlow?.CloseScreen();
            CancelLevelStart();
            _inputLocked = true;
            _hud.ClosePopup();
            _hud.SetVisible(false, instant: true);
            _wreath?.Hide();
            _map.Hide();
            _chapters.Hide();
            _home.Show();
            LevelLibrary.Prefetch(Frontier);   // warm the next generated boards before Play is tapped
            bool afterWin = _optInAfterWin;
            _optInAfterWin = false;
            MaybeOfferStarter();
            if (_booted || afterWin) StartCoroutine(HomeMomentsSoon(afterWin));
        }

        /// <summary>Home → All levels (chapter cards) → one chapter's map → level-start sheet → play.</summary>
        void ShowChapters()
        {
            _home.Hide();
            _map.Hide();
            _chapters.Show();
        }

        void ShowMap(int chapter)
        {
            _home.Hide();
            _chapters.Hide();
            _map.Show(chapter);
        }

        /// <summary>
        /// Review e, M3: a level that is still loading (or dropping in) when the player leaves for Home never
        /// starts behind it. Stops the loader coroutine and invalidates landing / intro / assist callbacks.
        /// </summary>
        void CancelLevelStart()
        {
            EndTutorial();
            _session++;
            if (_loading != null) { StopCoroutine(_loading); _loading = null; }
            HideLevelLoader();
        }

        /// <summary>Start a level from home or the map. Needs at least one life to start.</summary>
        void PlayLevel(int index)
        {
            if (Lives.Count <= 0)
            {
                ShowOutOfLives(() => PlayLevel(index), onBack: () => { });
                return;
            }
            _home.Hide();
            _map.Hide();
            _chapters.Hide();
            _hud.SetVisible(true);
            LoadLevel(index);
        }

        /// <summary>Leave the current level for home. Mid-level quits and abandoned fails cost a life.</summary>
        void GoHome(bool costsLife)
        {
            EndAttempt(AttemptOutcome.Quit);
            if (costsLife) Lives.TrySpend();
            ShowHome();
        }

        void DevJump(int delta)
        {
            int target = Mathf.Max(0, _levelIndex + delta);
            Progress.HighestUnlocked = target;   // dev: unlock as you skip so the map follows
            LoadLevel(target);
        }

        // ------------------------------------------------------------------ intro

        void HideRingsForIntro()
        {
            _inputLocked = true;
            foreach (var r in _rings) if (r != null) r.HideForIntro();
        }

        /// <summary>Rings land one after another, top of the board first, each with a soft tick.</summary>
        void DropInRings()
        {
            var order = _rings.FindAll(r => r != null);
            order.Sort((a, b) => _model.Rings[b.RingId].Center.y.CompareTo(_model.Rings[a.RingId].Center.y));
            int remaining = order.Count, session = _session;
            for (int i = 0; i < order.Count; i++)
            {
                var view = order[i];
                view.Landed = v =>
                {   // landing is silent: no sound on level load; never unlock input behind Home (M3)
                    if (--remaining == 0 && session == _session && (_home == null || !_home.Visible)) _inputLocked = false;
                };
                view.DropIn(0.07f * i);
            }
            if (order.Count == 0 && (_home == null || !_home.Visible)) _inputLocked = false;
        }

        public int LevelIndex => _levelIndex;

        public void Build() => Build(Frontier);

        public void Build(int index) => Build(index, ReadyData(index, _plan?.Variant ?? 0));

        /// <summary>
        /// Curated data, or a generated board that is already prepared. Never blocks on the infinite worker:
        /// a cold generated index (edit-mode preview, splash backdrop) shows its curated stand-in instead.
        /// </summary>
        static LevelData ReadyData(int index, int variant)
        {
            if (index < LevelLibrary.CuratedCount) return LevelLibrary.Get(index, variant);
            if (LevelLibrary.TryGetVariants(index, out var choices) && choices != null && choices.Length > 0)
                return choices[Mathf.Clamp(variant, 0, choices.Length - 1)];
            return LevelLibrary.Get(FallbackIndex(index));
        }

        /// <summary>
        /// Ready-cached stand-in when a generated board isn't prepared within the loader ceiling: the curated
        /// chapter-10 slot in the same position (all seven mechanics mixed; slot 20 is that chapter's boss).
        /// </summary>
        public static int FallbackIndex(int index) => LevelLibrary.CuratedCount - 20 + ((index % 20) + 20) % 20;

        void Build(int index, LevelData data)
        {
            _levelIndex = index;
            _levelData = data;
            var def = _levelData != null ? _levelData.ToDef() : Levels.Basic10();
            _def = def;
            Build(def);
            _rules = LevelRules.For(def, tutorial: _levelIndex < 3);
            _movesUsed = 0;
            _bonusMoves = 0;
            _usedAdContinue = false;
            _goldContinues = 0;
            _levelOver = false;
            _winAt = _failCheckAt = -1f;
            _undo.Clear();
            _pendingSnap = null;
            if (_hud != null) _hud.ClosePopup();
            if (_wreath != null) _wreath.Hide();
            UpdateHud();
        }

        /// <summary>Hard ceiling on the loader before a ready curated stand-in is used (INFINITE.md).</summary>
        const float LoaderCeiling = 1f;

        /// <summary>
        /// Jump to a level (the saga is infinite: no wrap), and drop the new board in. Generated boards are
        /// prepared off the main thread first; the loader shows for at most ~1 s, then a ready stand-in is used.
        /// </summary>
        public void LoadLevel(int index)
        {
            index = Mathf.Max(0, index);
            LeaveDaily();
            _dailyFlow?.CloseScreen();
            EndAttempt(AttemptOutcome.Quit);   // restart / skip before a result counts as a quit
            _plan = null;
            _levelIndex = index;
            if (!Application.isPlaying) { Build(index); return; }
            CancelLevelStart();   // a newer request wins
            if (LevelLibrary.TryGetVariants(index, out var ready) && ready != null && ready.Length > 0) StartLevel(index, null, ready);
            else _loading = StartCoroutine(LoadWhenReady(index));
        }

        System.Collections.IEnumerator LoadWhenReady(int index)
        {
            _inputLocked = true;
            var task = LevelLibrary.PrepareAsync(index);
            float start = Time.unscaledTime;
            while (!task.IsCompleted && Time.unscaledTime - start < LoaderCeiling)
            {
                float t = Time.unscaledTime - start;
                if (_loader == null && t > 0.12f) _loader = LoaderScreen.ShowLevel(transform, _kit);   // no flash for fast boards
                if (_loader != null) _loader.SetProgress(Mathf.Clamp01(t / LoaderCeiling));
                yield return null;
            }
            HideLevelLoader();
            _loading = null;
            if (index != _levelIndex) yield break;   // stale request
            bool ok = task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion && task.Result != null && task.Result.Length > 0;
            if (!ok) StartLevel(index, LevelLibrary.Get(FallbackIndex(index)), null, faulted: task.IsFaulted);
            else StartLevel(index, null, task.Result);
        }

        void HideLevelLoader()
        {
            if (_loader != null) Destroy(_loader.gameObject);
            _loader = null;
        }

        /// <param name="fallback">Curated stand-in when the generated board wasn't ready in time; null = the real board.</param>
        /// <param name="choices">The prepared variants (M4: selection and build use this fixed array, never a blocking fetch).</param>
        void StartLevel(int index, LevelData fallback, LevelData[] choices, bool faulted = false)
        {
            _fallbackData = fallback;
            if (fallback == null && (choices == null || choices.Length == 0)) choices = new[] { ReadyData(index, 0) };
            BeginAttempt(fallback, choices);
            if (fallback != null) TrackFallback(index, faulted);
            var data = fallback ?? choices[Mathf.Clamp(_plan.Variant, 0, choices.Length - 1)];
            // review i, M4: a broken board (bad gear group, unsolvable, too many rings) throws in Build / LevelRules.
            // Never leave a half-started level: drop this attempt unrecorded and start the ready stand-in.
            bool built;
            try { Build(index, data); built = FailFlow.TryRules(_def, _levelIndex, _plan.ExtraMoves, out _rules); }
            catch (Exception e) { Debug.LogWarning("Board failed to build: " + e.Message); built = false; }
            if (!built)
            {
                if (_attemptOpen) { _adaptive.CancelAttempt(); _attemptOpen = false; ConsumableGateway.LevelEnded?.Invoke(); }
                _neutralAttempt = false;
                if (fallback == null) { StartLevel(index, LevelLibrary.Get(FallbackIndex(index)), null, faulted: true); return; }
                Debug.LogError("Fallback board failed to build; returning home");
                ShowHome();
                return;
            }
            if (_neutralAttempt) TrackNeutralStart();   // H1: reward evidence even when adaptive selection failed
            BeginStreak();
            UpdateHud();
            HideRingsForIntro();
            DropInRings();
            LevelLibrary.Prefetch(index + 1);   // keep the next window warm
            WarmSolver(index + 1);
            ClaimBoosterUnlocks(index);
            StartCoroutine(AfterLanding(_plan, _session));
        }

        /// <summary>
        /// Review e, L9: fallback attempts are flagged for the data team. The typed level_start / level_end schemas
        /// reject unknown fields, so the flag rides on its own metric-only event right after level_start.
        /// </summary>
        static void TrackFallback(int index, bool faulted) =>
            ClientGateway.Track("level_fallback", new FallbackMetrics
            {
                metric_level_index = index, metric_fallback_index = FallbackIndex(index), metric_faulted = faulted,
            });

        [Serializable]
        public struct FallbackMetrics { public int metric_level_index, metric_fallback_index; public bool metric_faulted; }

        /// <summary>M4: solve the next curated mechanic board(s) on a worker thread so its start finds them cached.</summary>
        static void WarmSolver(int index)
        {
            if (!LevelLibrary.TryGetVariants(index, out var next) || next == null) return;
            var defs = new List<LevelDef>(next.Length);
            foreach (var d in next) if (d != null) defs.Add(d.ToDef());
            _ = MechanicSolver.Warm(defs);
        }

        // ------------------------------------------------------------------ booster tutorial unlock grants

        /// <summary>
        /// Hint unlocks at level 5, Undo at 8, Unclamp at 12 (displayed numbers). Each one asks the wallet for its
        /// booster_unlock grant once; the server is idempotent per player and kind, so a retry after a failure (or
        /// after a reinstall) is safe. Unbound gateway (no client API yet): nothing happens.
        /// </summary>
        void ClaimBoosterUnlocks(int index)
        {
            if (ClientGateway.BoosterUnlock == null || _boosterUnlockBusy) return;
            var due = BoosterUnlocks.Due(index + 1, kind => PlayerPrefs.GetInt(BoosterUnlocks.Key(kind), 0) == 1);
            if (due.Count == 0) return;
            _ = ClaimBoosterUnlocks(due);
        }

        async System.Threading.Tasks.Task ClaimBoosterUnlocks(List<string> kinds)
        {
            _boosterUnlockBusy = true;
            try
            {
                foreach (var kind in kinds)
                {
                    bool ok;
                    try { ok = await ClientGateway.BoosterUnlock(kind); }
                    catch (Exception e) { Debug.LogWarning("booster_unlock " + kind + " pending: " + e.Message); ok = false; }
                    if (!ok) continue;   // retried at the next level start
                    PlayerPrefs.SetInt(BoosterUnlocks.Key(kind), 1);
                    PlayerPrefs.Save();
                    if (this != null && _hud != null) UpdateHud();
                }
            }
            finally { _boosterUnlockBusy = false; }
        }

        // ------------------------------------------------------------------ "New gear!" intro

        /// <summary>Intro key of this level's tutorial ("ratchet", ... "mix"), or null.</summary>
        string IntroKey
        {
            get
            {
                var raw = _levelData?.mechanic_intro;
                if (string.IsNullOrEmpty(raw) || _fallbackData != null || _levelIndex >= LevelLibrary.CuratedCount) return null;
                int dot = raw.LastIndexOf('.');
                return dot >= 0 ? raw.Substring(dot + 1) : raw;
            }
        }

        /// <summary>What the pause card's "i" re-opens: this board's mechanic, or "mix" when it has several.</summary>
        string BoardIntroKey
        {
            get
            {
                if (IntroKey != null) return IntroKey;
                var m = _def?.Mechanics;
                if (m == null || m.Length == 0) return null;
                return m.Length == 1 && _kit.Art.IntroIcon(m[0]) != null ? m[0] : "mix";
            }
        }

        System.Collections.IEnumerator AfterLanding(AttemptPlan plan, int session)
        {
            int level = _levelIndex;
            var model = _model;
            bool Current() => session == _session && level == _levelIndex && model == _model;
            while (_inputLocked && Current()) yield return null;   // after the rings have landed
            if (!Current() || _levelOver || _hud == null) yield break;
            string key = IntroKey;
            if (key != null && !MechanicIntro.Seen(key))
            {
                _inputLocked = true;
                bool closed = false;
                ShowIntro(key, () => closed = true);
                // L7: also stop if anything closed the card without telling us (it never strands the input lock)
                while (!closed && Current() && _hud.PopupOpen) yield return null;
                if (closed) MechanicIntro.MarkSeen(key);   // only once the player actually pressed "Got it"
                if (Current()) _inputLocked = false;
            }
            if (!Current()) yield break;
            BeginTutorial();
            AnnounceStreakBonus();
            if (plan.ExtraMoves > 0 || plan.FreeHintGlow) yield return AnnounceAssist(plan, session);
        }

        void ShowIntro(string key, Action onClose)
        {
            var info = MechanicIntro.For(key, _def);
            _hud.ShowMechanicIntro(_kit.Art.IntroIcon(key), info, onClose);
        }

        void UpdateHud()
        {
            if (_hud == null) return;
            string info = _levelData == null ? "" :
                $"{_levelData.rings.Length} rings · {_levelData.links.Length} holders · depth {_levelData.depth} · " +
                $"best {_rules.MinMoves} · 3★ ≤{_rules.ThreeStar} · 2★ ≤{_rules.TwoStar} · limit {_rules.MoveLimit} · diff {_levelData.difficulty:F1}";
            _hud.SetLevel(_levelIndex + 1, info);
            if (_daily) _hud.SetLevelText("Daily");
            _hud.SetBoss(_def != null && _def.IsBoss);
            _hud.SetMoves(MovesLeft);
            var colours = new List<Color>();
            foreach (var r in _model.Rings) colours.Add(art.RingColor(r.Color));
            _hud.SetGoal(colours);
            foreach (var r in _model.Rings) if (r.Removed) _hud.MarkGoalFreed(r.Id);
            foreach (BoosterKind k in Enum.GetValues(typeof(BoosterKind))) _hud.SetBoosterCount(k, BoosterShown(k));
            ApplyBoosterLocks();
            _hud.SetBoosterReady(BoosterKind.Undo, _undo.Count > 0);
        }

        public void Build(LevelDef level)
        {
            Clear();
            _model = new PuzzleModel(level);
            _model.LinkReleased += OnLinkReleased;
            _model.RingRemoved += r => _hud?.MarkGoalFreed(r.Id);
            _model.Solved += OnSolved;
            _model.KeyCollected += OnKeyCollected;
            _model.OilCollected += OnOilCollected;
            _model.RingFrozen += OnRingFrozen;
            _model.ClampReclosed += OnClampReclosed;
            _toldOnce.Clear();
            BuildViews();
        }

        /// <summary>(Re)create every view from the model's current state (used after Undo too).</summary>
        void BuildViews()
        {
            _content = new GameObject("Board").transform;
            _content.SetParent(transform, false);

            if (art.backgroundSprite != null) BuildBackdrop();
            else BuildGlow();

            foreach (var ring in _model.Rings)
            {
                if (ring.Removed) { _rings.Add(null); _mechs.Add(null); continue; }
                var view = new GameObject().AddComponent<RingView>();
                view.transform.SetParent(_content, false);
                view.Init(ring, art);
                _rings.Add(view);
                _mechs.Add(RingMechanics.Attach(view, ring, _model, art));   // mechanic pieces, from current state
            }
            foreach (var link in _model.Links)
            {
                var owner = _rings[link.Owner];
                if (owner == null) { _holders.Add(null); continue; }
                var holder = new GameObject().AddComponent<HolderView>();
                holder.Init(link, _model, owner, art);
                if (!link.Active) holder.ReleaseInstant();
                owner.OwnedHolders.Add(holder);
                _holders.Add(holder);
            }
            for (int i = 0; i < _rings.Count; i++)
                if (_rings[i] != null && _model.IsFree(i)) _rings[i].FreeHint = true;

            _bounds = ComputeBounds();
            _fitAspect = -1f;
            FitCamera();
        }

        public void Clear() => ClearViews();

        void ClearViews()
        {
            _spins.Clear();
            _turned.Clear();
            _rings.Clear();
            _mechs.Clear();
            _holders.Clear();
            _active = -1;
            _mode = DragMode.None;
            if (_content == null) return;
            if (Application.isPlaying) Destroy(_content.gameObject);
            else DestroyImmediate(_content.gameObject);
            _content = null;
        }

        // ------------------------------------------------------------------ loop

        void Update()
        {
            if (!Application.isPlaying)
            {
                if (_content == null) BuildPreview(); // edit mode: (re)build the preview if it's missing
                return;
            }
            if (_model == null) return;
            if (_winAt > 0f && Time.time >= _winAt)
            {
                _winAt = -1f;
                if (_daily) { ShowDailyWin(); return; }
                _wreath?.Show();
                _hud?.ShowWin(_levelIndex + 1, _winStars,
                    onNext: () => _account.AfterWin(_levelIndex + 1, () =>
                    {
                        if (AdsGateway.Service != null) AdsGateway.Service.MaybeShowInterstitial("level_end", () => LoadLevel(_levelIndex + 1));
                        else LoadLevel(_levelIndex + 1);
                    }),
                    onHome: () => _account.AfterWin(_levelIndex + 1, () => GoHome(costsLife: false)));
                ShowStreakStrip();
            }
            if (_failCheckAt > 0f && Time.time >= _failCheckAt && !_spins.Busy && _active < 0 &&
                (_hud == null || !_hud.PopupOpen) && (_home == null || !_home.Visible))
            {
                _failCheckAt = -1f;
                CheckOutOfMoves();
            }

            FitCamera();
            HandlePointer();
            StepSpins(Time.deltaTime);
            if (!_spins.Busy && _active < 0) FinishFeature();
        }

        void HandlePointer()
        {
            var pointer = Pointer.current;
            if (pointer == null || cam == null) return;
            if (_inputLocked || (_splash != null && _splash.IsBlocking))
            {
                if (_active >= 0) { _rings[_active].IsHeld = false; _active = -1; _mode = DragMode.None; }
                return;
            }

            Vector2 screen = pointer.position.ReadValue();
            if (pointer.press.wasPressedThisFrame && (_hud != null && _hud.Covers(screen) || TutorialCovers(screen))) return; // HUD / Skip
            Vector2 world = cam.ScreenToWorldPoint(screen);
            world = _content.InverseTransformPoint(world);

            if (pointer.press.wasPressedThisFrame) Press(world);
            else if (pointer.press.isPressed && _active >= 0) Drag(world);

            if (pointer.press.wasReleasedThisFrame && _active >= 0) Release(world);
        }

        void Press(Vector2 p)
        {
            _active = Pick(p);
            if (_active < 0) return;
            // H1: every running tap spin finishes (and completes its move) before this gesture starts, so the
            // Undo snapshot, the gesture's progress check and the move boundaries stay strictly sequential.
            // This also keeps pulls behind the move boundary of a tap that freed the ring (L9).
            FinishSpins();
            if (_active < 0 || _rings[_active] == null || _model.Rings[_active].Removed || _levelOver) { _active = -1; return; }
            FinishFeature();
            _requiredBefore = Adaptive.RequiredRotation(_model);
            _thinkSeconds = Time.unscaledTime - _lastGesture;
            ClearHints();   // the player is acting; the hint has done its job

            _pressWorld = p;
            _lastPointer = p;
            _pressTime = Time.time;
            _rotated = 0f;
            _refusedThisPress = false;
            _pullVelocity = Vector2.zero;
            var ring = _model.Rings[_active];
            _lastAngle = PuzzleModel.AngleOf(p - ring.Center);
            var view = _rings[_active];

            if (_model.IsFree(_active))
            {
                _mode = DragMode.Pull;
                view.IsHeld = true;
                view.SetLifted(true);
                _fx?.Pick(ring.Radius);
            }
            else if (MovesLeft <= 0)   // no moves left: any turn attempt (right or wrong ring) opens the card
            {
                _mode = DragMode.None;
                _active = -1;
                _failCheckAt = Time.time;
                return;
            }
            else if (_model.CanRotate(_active))
            {
                _mode = DragMode.Rotate;
                _pendingSnap = _model.Capture();   // pushed onto the undo stack if this becomes a move
                _gesture = new GestureTracker(_model, _active);
                _fx?.Pick(ring.Radius);
            }
            else
            {
                _mode = DragMode.Locked;
                _pendingSnap = _model.Capture();
                view.Shake();
                _fx?.Locked();
                foreach (var h in view.OwnedHolders)
                    if (_model.Links[h.LinkId].Active) h.Flash();
                ExplainLocked(_active);
                // Touching a ring that is still held (by a holder, padlock, rust or its locked gear partner)
                // is a wrong move: it costs a move too, and it is a counted gesture for the rules.
                _lockedTouches++;
                CountMove();
                _model.CompleteMove();
                FinishFeature();
                _failCheckAt = Time.time + 0.45f;
            }
            view.SetPicked(_mode != DragMode.Locked);
        }

        void Drag(Vector2 p)
        {
            var ring = _model.Rings[_active];
            var view = _rings[_active];
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);

            switch (_mode)
            {
                case DragMode.Rotate:
                    if ((p - ring.Center).sqrMagnitude < 0.09f) break; // too close to the axis to read an angle
                    float angle = PuzzleModel.AngleOf(p - ring.Center);
                    float delta = Mathf.DeltaAngle(_lastAngle, angle);
                    _lastAngle = angle;
                    var outcome = TurnRing(_active, delta, delta / dt, _gesture);
                    _rotated = _gesture.Rotated;
                    _refusedThisPress |= _gesture.Refused;
                    // ratchet: never silently reverse the drag; refuse with a shake and charge nothing
                    if (outcome == RotateOutcome.WrongDirection && _gesture.WrongTurn > 5f && Time.time - _refusedAt > 0.6f)
                    {
                        _gesture.ClearWrongTurn();
                        RefuseTurn(_active);
                    }
                    break;
                case DragMode.Pull:
                    view.SetPullOffset(p - _pressWorld);
                    _pullVelocity = Vector2.Lerp(_pullVelocity, (p - _lastPointer) / dt, 0.5f);
                    break;
            }
            _lastPointer = p;
        }

        void Release(Vector2 p)
        {
            var view = _rings[_active];
            float held = Time.time - _pressTime;
            bool isTap = held < 0.3f && (p - _pressWorld).magnitude < 0.25f;

            switch (_mode)
            {
                case DragMode.Rotate:
                    if (IsTapSpin(isTap, _rotated, _refusedThisPress))
                    {
                        CountMove();
                        StartSmartSpin(_active, new GestureTracker(_model, _active));   // completes when the spin stops
                    }
                    else if (_gesture != null && _gesture.Counts)
                    {
                        CountMove();
                        _gesture.Complete();       // exactly once per counted gesture
                        _failCheckAt = Mathf.Max(_failCheckAt, Time.time + 0.15f);
                    }
                    _gesture = null;
                    break;
                case DragMode.Pull:
                    view.IsHeld = false;
                    Vector2 offset = view.PullOffset;
                    if (isTap || offset.magnitude > pullOutDistance || _pullVelocity.magnitude > 7f)
                    {
                        Vector2 dir = offset.magnitude > 0.2f ? offset
                            : _model.Rings[_active].Center - _bounds.center;
                        if (dir.sqrMagnitude < 0.01f) dir = Vector2.up;
                        if (!PullOut(_active, dir, _pullVelocity.magnitude)) view.SetLifted(false);   // M6: never stuck lifted
                    }
                    else view.SetLifted(false);
                    break;
            }
            view.SetPicked(false);
            _active = -1;
            _mode = DragMode.None;
        }

        void StepSpins(float dt) => _spins.Step(dt, tapSpinSpeed, TurnFn);

        SpinSet.TurnFn TurnFn => _turnFn ??= (id, delta, speed) => TurnRing(id, delta, speed);

        /// <summary>H1: finish every running tap spin now (rest of the turn in one step) and complete its move.</summary>
        void FinishSpins()
        {
            if (_model == null) return;
            _spins.FinishAll(TurnFn);
        }

        /// <summary>
        /// A short press is a tap (smart spin) only when nothing was turned and nothing was refused: a quick
        /// wrong-way flick on a ratchet is a refusal, never a paid spin the other way (review e, M1).
        /// </summary>
        public static bool IsTapSpin(bool shortPress, float rotatedDegrees, bool refused) =>
            shortPress && rotatedDegrees < 6f && !refused;

        // ------------------------------------------------------------------ moves, win, fail

        int MovesLeft => _rules.MoveLimit + _bonusMoves - _movesUsed;

        /// <summary>Stops a spin; a tap spin's counted move completes here (spring timers, rust deadlines).</summary>
        void EndSpin(int id) => _spins.End(id);

        void RefuseTurn(int id)
        {
            _refusedAt = Time.time;
            _fx?.Refused();
            int mate = _model.LinkedPartner(id);
            for (int k = 0; k < 2; k++)
            {
                int r = k == 0 ? id : mate;
                if (r < 0 || _rings[r] == null) continue;
                _rings[r].Shake();
                _mechs[r]?.Refuse();
            }
            TellOnce("ratchet", "This gear only turns one way");
        }

        /// <summary>Extra cues on a locked touch: which mechanic is holding the ring.</summary>
        void ExplainLocked(int id)
        {
            var ring = _model.Rings[id];
            int mate = -1;
            foreach (var r in _model.Rings) if (r.Id != id && r.GearGroup != 0 && r.GearGroup == ring.GearGroup && !r.Removed) mate = r.Id;
            if (_model.IsPadlocked(id)) { _mechs[id]?.Nudge(); TellOnce("padlock", "Free the key to unlock"); }
            else if (ring.Frozen) { _mechs[id]?.Nudge(); TellOnce("frozen", "Rusted solid"); }
            else if (mate >= 0 && _rings[mate] != null && !_model.CanRotate(mate))
            {
                // its gear partner is the one held: show that ring too
                _rings[mate].Shake();
                foreach (var h in _rings[mate].OwnedHolders)
                    if (_model.Links[h.LinkId].Active) h.Flash();
                _mechs[mate]?.Nudge();
                TellOnce("linked", "Its gear partner is held");
            }
        }

        void TellOnce(string key, string message)
        {
            if (_hud == null || !_toldOnce.Add(key)) return;
            _hud.ShowToast(message);
        }

        void CountMove()
        {
            _undo.Push((_pendingSnap ?? _model.Capture(), _movesUsed));
            _pendingSnap = null;
            _movesUsed++; _gestures++;
            _featurePending = true;
            _hud?.SetMoves(Mathf.Max(0, MovesLeft), bump: true);
            _hud?.SetBoosterReady(BoosterKind.Undo, true);
            _tutorial?.OnMove();
        }

        /// <summary>
        /// A tap spins the ring the short way to its nearest clamp, just far enough to release it
        /// (or a fixed step if nothing is clamped on it), so every tap is a meaningful move.
        /// </summary>
        void StartSmartSpin(int id, GestureTracker move)
        {
            float delta = SmartSpinDelta(_model, id, tapSpin);
            // Short hops are quick, long ones a touch slower but never sluggish.
            _spins.Start(id, delta, Mathf.Max(tapSpinSpeed, Mathf.Abs(delta) / 0.45f), move);
        }

        /// <summary>Visual-only spin for capture tools (no move is counted).</summary>
        public void DebugSpin(int id, float degrees) { if (id >= 0) _spins.Start(id, degrees, tapSpinSpeed, null); }

        /// <summary>
        /// Shortest turn that makes progress on this ring or its gear partner, respecting both ratchets:
        /// reach the nearest clamp the ring can open; a clay coat gets its second pass in the same spin (one
        /// gesture, like the solver); a clamp already in the gap needs a full turn to leave and re-enter.
        /// </summary>
        public static float SmartSpinDelta(PuzzleModel m, int id, float idle)
        {
            int mate = m.LinkedPartner(id);
            bool Allowed(float d) => m.DirectionAllowed(id, d) && (mate < 0 || m.DirectionAllowed(mate, -d));
            float delta = 0f, best = float.MaxValue;
            for (int k = 0; k < 2; k++)
            {
                int r = k == 0 ? id : mate;
                if (r < 0) continue;
                var ring = m.Rings[r];
                float sign = r == id ? 1f : -1f;   // the partner turns the opposite way
                foreach (var linkId in ring.Incoming)
                {
                    var l = m.Links[linkId];
                    if (!l.Active || (l.Tinted && l.Color != ring.Color)) continue;
                    float reach = PuzzleModel.GapHalfAngle - l.ClampHalfAngle;
                    float d = Mathf.DeltaAngle(ring.GapAngle, l.ClampAngle);
                    bool fresh = l.Clay && l.Passes == 0;   // an uncracked coat needs two entries in one gesture
                    float turn;
                    // in the gap already: leave and re-enter (twice for a fresh coat, e.g. a spring re-closed in the gap)
                    if (l.InGap) turn = (d >= 0f ? 1f : -1f) * (fresh ? 720f : 360f);
                    else
                    {
                        turn = Mathf.Sign(d) * (Mathf.Max(0f, Mathf.Abs(d) - reach) + 3f);
                        if (fresh) turn += Mathf.Sign(turn) * 360f;
                    }
                    turn *= sign;
                    // the ratchet forces the long way round. In the gap: the same sweep reversed. Otherwise the
                    // reverse entry is 360 away; a fresh coat needs that entry and one more full turn, so the forward
                    // s*(x+360) becomes s*(x-720) (review e, M2: subtracting 720 gave a single pass).
                    if (!Allowed(turn)) turn = l.InGap ? -turn : turn - Mathf.Sign(turn) * (fresh ? 1080f : 360f);
                    if (!Allowed(turn) || Mathf.Abs(turn) >= best) continue;
                    best = Mathf.Abs(turn);
                    delta = turn;
                }
            }
            if (best < float.MaxValue) return delta;
            return Allowed(idle) ? idle : -idle;
        }

        bool HasActiveLinks()
        {
            foreach (var l in _model.Links)
                if (l.Active) return true;
            return false;
        }

        void ShowRewarded(string placement, Action onReward, Action onUnavailable = null)
        {
            var ads = AdsGateway.Service;
            if (ads == null || !ads.RewardedReady)
            {
                _hud.ShowToast("No video available right now");
                if (onUnavailable != null) onUnavailable();
                else if (_levelOver && MovesLeft <= 0) CheckOutOfMovesAgain();
                return;
            }
            int level = _levelIndex;
            ads.ShowRewarded(placement, () =>
            {
                if (this != null && _levelIndex == level) onReward();
            }, () =>
            {
                if (this == null) return;
                _hud.ShowToast("Video not completed");
                if (onUnavailable != null) onUnavailable();
                else if (_levelOver && MovesLeft <= 0) CheckOutOfMovesAgain();
            });
        }

        void CheckOutOfMoves()
        {
            if (_levelOver || _model.IsSolved || !HasActiveLinks()) return;
            // M5: rust (or a spring re-close) can leave no way out with moves still in hand: end the attempt the
            // normal way instead of making the player burn every remaining move. Extra moves can't help here, so
            // the card offers Undo (if there is one to take back) and Retry, never a paid/video continue.
            // review i, M1: a dead board is a dead end even on the last move; it never gets a "+N moves" continue
            var card = FailFlow.Decide(MovesLeft, Deadlock.IsDead(_model));
            if (card == FailFlow.Card.None) return;
            bool stuck = card == FailFlow.Card.DeadEnd;
            _levelOver = true;
            EndTutorial();
            _wreath?.Show(frame: false);   // the sheet leaves the board in view (approved out_of_moves_gold_B)
            if (_daily) { ShowDailyFail(stuck); return; }
            if (stuck)
            {
                var dead = new LevelHud.ContinueOffer { Title = FrozenHolds() ? "Rusted solid" : "No way out" };
                if (_undo.Count > 0 && BoosterShown(BoosterKind.Undo) > 0)
                    dead.Extra.Add(("Undo", art.iconUndo, () => Rescue(BoosterKind.Undo)));
                // M2: an owned Unclamp that can open something rescues the board (a frozen ring's own clamp first)
                if (BoosterShown(BoosterKind.Unclamp) > 0 && !_hud.BoosterLocked(BoosterKind.Unclamp) && FailFlow.RescueLink(_model) >= 0)
                    dead.Extra.Add(("Unclamp", art.iconUnclamp, () => Rescue(BoosterKind.Unclamp)));
                _hud?.ShowOutOfMoves(dead, onRetry: RetryAfterLoss,
                    onHome: () => { EndAttempt(AttemptOutcome.Lose); GoHome(costsLife: true); });
                return;
            }
            bool canWatch = !_usedAdContinue;
            // review i, L4: ad_offer means "the video pill is visible AND an ad is ready"
            if (canWatch && AdsGateway.Service?.RewardedReady == true) AdsGateway.Service.RewardedOfferShown("continue_moves");
            var offer = new LevelHud.ContinueOffer { Moves = ContinueMoves, CanWatch = canWatch };
            offer.OnWatch = () => ShowRewarded("continue_moves", () =>
            {
                _usedAdContinue = true;
                Continued(ContinueMoves);
            });
            AddGoldContinue(offer);
            _hud?.ShowOutOfMoves(offer, onRetry: RetryAfterLoss,
                onHome: () => { EndAttempt(AttemptOutcome.Lose); GoHome(costsLife: true); });
        }

        /// <summary>
        /// Retry from a fail card (review i, M3): the loss is recorded only once a life is actually spent. With no
        /// lives the out-of-lives sheet opens and Back returns to the same card with the attempt still open.
        /// </summary>
        void RetryAfterLoss()
        {
            var port = DailyGateway.Current;
            int wins = port?.Wins ?? 0;
            SpendLifeThen(() =>
            {
                EndAttempt(AttemptOutcome.Lose);
                if (wins > 0 && port != null && _dailyFlow != null) _dailyFlow.ShowFreshStart(port.NextBonus, () => LoadLevel(_levelIndex));
                else LoadLevel(_levelIndex);
            }, reopen: CheckOutOfMovesAgain);
        }

        /// <summary>A dead-end card's booster rescue: back to the board, the booster acts, the check runs again.</summary>
        void Rescue(BoosterKind kind)
        {
            _wreath?.Hide();
            _levelOver = false;
            UseBooster(kind);
        }

        /// <summary>L8: "Rusted solid" only when a frozen ring is what holds the board (not any rust anywhere).</summary>
        bool FrozenHolds()
        {
            foreach (var r in _model.Rings)
            {
                if (r.Removed || !r.Frozen) continue;
                foreach (int id in r.Incoming) if (_model.Links[id].Active) return true;
            }
            return false;
        }

        /// <summary>A continue (video or gold) landed: the attempt stays open and resumes with the extra moves.</summary>
        void Continued(int moves)
        {
            _wreath?.Hide();
            _attemptContinues++;
            StreakContinued();
            _bonusMoves += moves;
            _failCheckAt = Time.time + 0.3f;   // M1: a dead end found after a continue is reported without a move
            _levelOver = false;
            _hud.SetMoves(MovesLeft, bump: true);
            Haptics.Play(Haptics.Impact.Medium, 0.8f);
        }

        // ------------------------------------------------------------------ pause, lives

        void OpenPause()
        {
            if (_levelOver || _hud == null || _hud.PopupOpen) return;
            _wreath?.Show(frame: false);
            ShowPauseCard();
        }

        void ShowPauseCard()
        {
            if (_daily)
            {   // the daily board is free to restart and leave (no lives, no saga progress)
                _hud.ShowPause(onRestart: RetryDaily, onResume: () => _wreath?.Hide(), onHome: BackToDaily);
                return;
            }
            string intro = BoardIntroKey;
            _hud.ShowPause(
                onInfo: intro == null ? null : (Action)(() => ShowIntro(intro, ShowPauseCard)),
                onRestart: () => _hud.ShowLifeConfirm("Restart level?", "Restart",
                    onConfirm: () => SpendLifeThen(() => LoadLevel(_levelIndex), reopen: ShowPauseCard),
                    onCancel: ShowPauseCard),
                onResume: () => _wreath?.Hide(),
                onHome: () =>
                {
                    if (_movesUsed == 0) { GoHome(costsLife: false); return; }   // nothing played yet: free
                    _hud.ShowLifeConfirm("Leave level?", "Leave",
                        onConfirm: () => GoHome(costsLife: true),
                        onCancel: ShowPauseCard);
                });
        }

        void CheckOutOfMovesAgain()
        {
            _levelOver = false;
            CheckOutOfMoves();
        }

        /// <summary>
        /// Spend a life and run <paramref name="action"/>. With no lives left, show the out-of-lives card instead:
        /// "Refill with ad" refills and then runs the action; "Back" returns to the card that asked (<paramref name="reopen"/>).
        /// </summary>
        void SpendLifeThen(Action action, Action reopen)
        {
            FailFlow.SpendThen(Lives.TrySpend, null, action, () =>
            {
                _wreath?.Show(frame: false);
                ShowOutOfLives(() => { if (Lives.TrySpend()) action(); else SpendLifeThen(action, reopen); }, reopen);
            });
        }

        /// <summary>
        /// Approved out_of_lives_gold_B. The video row refills through the SSV-verified ad path (awaited; the
        /// provisional lives are already applied when the reward lands). Gold is charged only when the player taps
        /// the explicit "Refill for 90 gold" button on this sheet (that tap is the confirmation of the shown price);
        /// the sheet shows a pending state during the wallet request and an inline retry on failure.
        /// </summary>
        void ShowOutOfLives(Action onRefilled, Action onBack)
        {
            const string placement = "refill_lives";
            // review i, L4: ad_offer only when an ad can actually play, under the same name ad_shown uses
            if (AdsGateway.Service?.RewardedReady == true) AdsGateway.Service.RewardedOfferShown("lives");
            _hud.ShowOutOfLives(
                onRefillAd: () => ShowRewarded(placement, async () =>
                {
                    bool ok = await Lives.RefillFromAd(placement);
                    if (this == null) return;
                    if (!ok && Lives.Count <= 0) { _hud.ShowToast("Life not added yet. Try again soon"); onBack?.Invoke(); return; }
                    onRefilled();
                }, () => ShowOutOfLives(onRefilled, onBack)),
                refillGold: Lives.RefillWithGold,
                onGoldRefilled: () => { if (this != null) onRefilled(); },
                onBack: onBack ?? (() => { }));
        }

        // ------------------------------------------------------------------ boosters

        void UseBooster(BoosterKind kind)
        {
            if (_model == null || _levelOver || _inputLocked || _hud.PopupOpen) return;
            if (_active >= 0) return;   // a finger is on a ring; the tap is ignored silently
            FinishSpins();              // H1: a running tap spin completes its move before the booster acts
            if (_levelOver) return;
            if (_hud.BoosterLocked(kind))
            {
                Haptics.Notify(Haptics.Notice.Warning);
                _hud.ShowToast($"{char.ToUpper(BoosterName(kind)[0])}{BoosterName(kind).Substring(1)} unlocks at level {TutorialFlow.UnlockLevel(kind)}");
                return;
            }
            if (BoosterShown(kind) <= 0)
            {
                if (ShopFlow.Instance != null && ShopGateway.Current != null)
                    ShopFlow.OfferBooster(kind, () => { if (this != null) UpdateHud(); });   // one for 40 gold
                else
                {
                    Haptics.Notify(Haptics.Notice.Warning);
                    _hud.ShowToast($"No {BoosterName(kind)}s left");
                }
                return;
            }
            string refusal = null;
            switch (kind)
            {
                case BoosterKind.Hint:
                    if (Hints.TryFind(_model, out var hint) && _rings[hint.Ring] != null)
                    {
                        ClearHints();
                        if (hint.PullOut) _rings[hint.Ring].ShowPullHint();
                        else _rings[hint.Ring].ShowHint(hint.Delta);
                        _hud.ShowToast(hint.PullOut ? "Pull this gear out" : "Turn this gear");
                    }
                    else refusal = "No hint for this board";
                    break;
                case BoosterKind.Undo:
                    if (_undo.Count > 0)
                    {
                        var (snap, moves) = _undo.Pop();
                        ClearViews();
                        _model.Restore(snap);
                        _movesUsed = moves;
                        BuildViews();
                        UpdateHud();
                        foreach (var r in _rings) if (r != null) r.Pulse();
                        _failCheckAt = Time.time + 0.4f;   // still a dead end (rust / spring)? the card comes back
                    }
                    else refusal = "Nothing to undo yet";
                    break;
                case BoosterKind.Unclamp:
                    int linkId = FailFlow.RescueLink(_model);   // a frozen ring's own clamp first (review i, M2)
                    if (linkId < 0 || !_model.ReleaseLink(linkId)) refusal = "No clamps left";
                    else
                    {
                        // L4: ReleaseLink assumes a release inside a gesture (move zero = that gesture). Outside one,
                        // the next gesture is the first of the spring's K, so the badge counts down right away.
                        _model.Links[linkId].ReleasedAt = _model.Moves;
                        ClearHints();
                        _failCheckAt = Time.time + 0.15f;
                    }
                    break;
            }
            if (refusal != null)
            {
                Haptics.Notify(Haptics.Notice.Warning);
                _hud.ShowToast(refusal);
                return;
            }
            if (kind == BoosterKind.Undo && _attemptOpen)
            {
                _gestures++;
                FinishFeature();
                _adaptive.RecordMove(false, Time.unscaledTime - _lastGesture, _lockedTouches, true);
                _lastGesture = Time.unscaledTime; _lockedTouches = 0;
            }
            ClientGateway.Track("booster_used", new { level = _levelIndex, booster = BoosterName(kind) });
            if (_freeBooster == kind) _freeBooster = null;   // the win-streak bonus goes first (attempt-local, no wallet)
            else BoosterInventory.TrySpend(kind);
            _attemptBoosters++;
            Haptics.Play(Haptics.Impact.Medium, 0.8f);
            _hud.SetBoosterCount(kind, BoosterShown(kind));
            _hud.SetBoosterReady(BoosterKind.Undo, _undo.Count > 0);
            _tutorial?.OnBooster(kind);
        }

        // ------------------------------------------------------------------ adaptive difficulty

        /// <summary>
        /// Freezes this attempt's plan: a player who keeps losing a level gets a few extra moves and,
        /// after more losses, a free hint. Never fewer moves than the level's authored budget.
        /// </summary>
        void BeginAttempt(LevelData fallback, LevelData[] choices)
        {
            _neutralAttempt = false;
            _attemptStart = _lastGesture = Time.unscaledTime;
            _attemptBoosters = _attemptContinues = _lockedTouches = 0;
            _featurePending = false; _gestures = 0;
            _attemptOpen = false;
            try
            {
                _adaptive ??= new Adaptive();
                ClientGateway.Configure?.Invoke(_adaptive);
                // a curated stand-in (generated board not ready in time) is recorded against this index as given
                _plan = fallback != null ? _adaptive.BeginAttempt(_levelIndex, fallback.ToDef()) : _adaptive.BeginAttempt(_levelIndex, choices);
                _attemptOpen = true;
            }
            catch (Exception e)
            {
                // C14: a bad pack entry / model asset must never soft-lock the level. Play it with a neutral plan,
                // unrecorded (no attempt is open, so no level_start/level_end and no rating change).
                if (_adaptive != null) _adaptive.CancelAttempt();
                _plan = AttemptPlan.Neutral();
                _neutralAttempt = true;   // H1: level_start / level_end still go out (no rating change); see TrackNeutralStart
                if (_toldOnce.Add("adaptive_failed")) Debug.LogWarning("Adaptive plan unavailable, using neutral: " + e.Message);
                ClientGateway.Track("error", new { code = "adaptive_begin_failed", context = "level_start" });
                return;
            }
            ClientGateway.Track("level_start", new { LevelIndex = _levelIndex, Variant = _plan.Variant, Plan = new { variant = _plan.Variant, extraMoves = _plan.ExtraMoves, freeHintGlow = _plan.FreeHintGlow, reason = _plan.Reason, moveLimit = _plan.MoveLimit } });
        }

        System.Collections.IEnumerator AnnounceAssist(AttemptPlan plan, int session)
        {
            int level = _levelIndex;
            while (_inputLocked && session == _session) yield return null;   // after the rings have landed
            if (session != _session || level != _levelIndex || _levelOver || _hud == null) yield break;
            if (plan.FreeHintGlow && Hints.TryFind(_model, out var hint) && _rings[hint.Ring] != null)
            {
                if (hint.PullOut) _rings[hint.Ring].ShowPullHint();
                else _rings[hint.Ring].ShowHint(hint.Delta);
                _hud.ShowToast(plan.ExtraMoves > 0 ? $"+{plan.ExtraMoves} moves and a free hint" : "Here's a free hint");
            }
            else if (plan.ExtraMoves > 0) _hud.ShowToast($"+{plan.ExtraMoves} moves this time");
        }

        /// <summary>First-clear gold arrived from the wallet (server amounts: boss 50, otherwise 5).</summary>
        void OnRewardGranted(int levelIndex, int gold)
        {
            if (this == null || _hud == null) return;
            _hud.ShowToast(LevelRewards.IsBoss(levelIndex) ? $"Boss reward · +{gold} gold" : $"+{gold} gold");
        }

        /// <returns>True when a level_end went out for this attempt.</returns>
        bool EndAttempt(AttemptOutcome outcome)
        {
            EndStreak(outcome);
            if (!_attemptOpen || _adaptive == null) return EndNeutralAttempt(outcome);
            FinishFeature();
            _attemptOpen = false;
            if (_gestures == 0 && outcome != AttemptOutcome.Win)   // H1: a win always sends its level_end evidence
            {
                _adaptive.CancelAttempt();   // left without touching the board: not evidence of anything
                ConsumableGateway.LevelEnded?.Invoke();   // no level_end goes out: apply deferred ad policy here
                return false;
            }
            bool tracked = false;
            try
            {
                _adaptive.EndAttempt(new AttemptResult(outcome, _movesUsed, Time.unscaledTime - _attemptStart,
                    _attemptBoosters, _attemptContinues));
                ClientGateway.Track("level_end", _adaptive.Log.Entries[_adaptive.Log.Entries.Count - 1]);
                tracked = true;   // the level_end telemetry hook applies deferred ad policy itself
                ClientGateway.Save?.Invoke();
            }
            catch (Exception e) { Debug.LogWarning("Adaptive record skipped: " + e.Message); }
            if (!tracked) ConsumableGateway.LevelEnded?.Invoke();
            return tracked;
        }

        void FinishFeature()
        {
            if (!_featurePending || !_attemptOpen) return;
            _featurePending = false;
            _adaptive.RecordMove(Adaptive.RequiredRotation(_model) < _requiredBefore - .01f,
                _thinkSeconds, _lockedTouches);
            _lastGesture = Time.unscaledTime; _lockedTouches = 0;
        }

        void OnDestroy() { if (Application.isPlaying) EndAttempt(AttemptOutcome.Quit); }
        void OnApplicationQuit() => EndAttempt(AttemptOutcome.Quit);

        static string BoosterName(BoosterKind kind) => kind switch
        {
            BoosterKind.Hint => "hint",
            BoosterKind.Undo => "undo",
            _ => "unclamp",
        };

        void ClearHints()
        {
            foreach (var r in _rings) if (r != null) r.ClearHint();
        }

        void OnSolved()
        {
            if (_daily) { OnDailySolved(); return; }
            _optInAfterWin = true;
            _levelOver = true;
            EndTutorial();
            _fx?.Cleared();
            _winStars = _rules.StarsFor(_movesUsed);
            _starterAfterWin = true;
            Progress.RecordWin(_levelIndex, _winStars);
            LevelLibrary.Prefetch(_levelIndex + 1);   // the frontier moved: warm the next boards
            _account.RecordWin(_levelIndex + 1);   // may wait for the tracking pre-prompt
            if (EndAttempt(AttemptOutcome.Win)) RewardClaims.Enqueue(_levelIndex, OnRewardGranted);
            _winAt = Time.time + 1.0f;   // let the last ring fly off and the chime ring first
        }

        /// <summary>Rotates through the model and fires a ratchet tick for every gear detent crossed.</summary>
        RotateOutcome TurnRing(int id, float delta, float speed, GestureTracker gesture = null)
        {
            // watch clamps on this ring and its gear partner: colour refusals and clay cracks
            _probe.Clear();
            int mate = _model.LinkedPartner(id);
            for (int k = 0; k < 2; k++)
            {
                int r = k == 0 ? id : mate;
                if (r < 0) continue;
                var incoming = _model.Rings[r].Incoming;
                for (int i = 0; i < incoming.Count; i++)
                {
                    var l = _model.Links[incoming[i]];
                    if (l.Active && (l.Clay || l.Tinted)) _probe.Add((l, l.Passes, l.InGap));
                }
            }
            var outcome = gesture != null ? gesture.Turn(delta) : _model.Rotate(id, delta);
            if (outcome != RotateOutcome.Rotated) return outcome;
            for (int p = 0; p < _probe.Count; p++)
            {
                var (l, passes, inGap) = _probe[p];
                if (!l.Active) continue;
                bool mismatch = l.Tinted && l.Color != _model.Rings[l.Target].Color;
                if (mismatch && l.InGap && !inGap && Time.time - _clampRefusedAt > 0.4f)
                {   // the gap passed a colour clamp that isn't this ring's colour: a gentle "no"
                    _clampRefusedAt = Time.time;
                    if (_holders[l.Id] != null) _holders[l.Id].Refuse();
                    _fx?.ClampRefused();
                    TellOnce("colour", "This clamp opens only for its own colour");
                }
                if (l.Clay && l.Passes > passes) _fx?.Cracked();
            }
            if (mate >= 0) _turned[mate] = (_turned.TryGetValue(mate, out float m0) ? m0 : 0f) - delta;
            var ring = _model.Rings[id];
            _turned.TryGetValue(id, out float before);
            float after = before + delta;
            _turned[id] = after;
            float detent = DetentDegrees(ring.Radius);
            if (Mathf.FloorToInt(before / detent) != Mathf.FloorToInt(after / detent))
                _fx?.Tick(ring.Radius, speed);
            return outcome;
        }

        /// <summary>Half a tooth pitch, using the same tooth spacing the Blender rings are modeled with.</summary>
        static float DetentDegrees(float radius)
        {
            const float tubeHalf = 0.23f, toothPitch = 0.74f;
            float teeth = Mathf.Max(8f, Mathf.Round(2f * Mathf.PI * (radius + tubeHalf) / toothPitch));
            return 360f / (teeth * 2f);
        }

        /// <returns>True when the ring left the board.</returns>
        bool PullOut(int id, Vector2 dir, float speed)
        {
            // M6: a tap spin that freed this ring finishes its move first; that move can re-close a spring on it,
            // so freedom is checked only afterwards
            EndSpin(id);
            if (!_model.IsFree(id) || !_model.TryRemove(id)) return false;
            _rings[id].FreeHint = false;
            _rings[id].FlyAway(dir, speed);
            _fx?.PullOut(_model.Rings[id].Radius);
            _tutorial?.OnPulled();
            return true;
        }

        void OnLinkReleased(Link link)
        {
            _tutorial?.OnReleased();
            if (_holders[link.Id] != null) _holders[link.Id].Release();
            _fx?.Release();
            if (_crumbs != null)
            {
                Vector2 clamp = _model.ClampPoint(link);
                Vector2 away = clamp - _model.Rings[link.Target].Center;
                _crumbs.Burst(_content.TransformPoint(clamp), away, art.hardwareTint, art.RingColor(_model.Rings[link.Target].Color));
            }
            for (int k = 0; k < 2; k++)
            {
                int id = k == 0 ? link.Target : link.Owner;
                if (!_model.IsFree(id) || _rings[id] == null) continue;
                _rings[id].Pulse();
                _rings[id].FreeHint = true;
            }
        }

        void OnKeyCollected(Ring carrier)
        {
            _fx?.Unlocked();
            foreach (var r in _model.Rings)
                if (r.PadlockId == carrier.KeyId && !r.Removed && _rings[r.Id] != null) _rings[r.Id].Pulse();
        }

        void OnOilCollected(Ring carrier)
        {
            _fx?.Oiled();
            foreach (var r in _model.Rings)
                if (r.RustOilId == carrier.OilId && !r.Removed && !r.Frozen && _rings[r.Id] != null) _rings[r.Id].Pulse();
        }

        void OnRingFrozen(Ring ring)
        {
            _fx?.Frozen();
            if (_rings[ring.Id] != null) _rings[ring.Id].Shake();
            // M5: a ring that rusted with clamps still on it can never be freed: after a short beat the fail
            // card opens (CheckOutOfMoves sees the dead end), the player is never left tapping out moves
            if (Deadlock.IsDead(_model)) _failCheckAt = Time.time + 0.7f;
        }

        void OnClampReclosed(Link link)
        {
            if (_holders[link.Id] != null) _holders[link.Id].Reclose();
            _fx?.Snapped();
            for (int k = 0; k < 2; k++)
            {
                int id = k == 0 ? link.Target : link.Owner;
                if (_rings[id] != null && !_model.IsFree(id)) _rings[id].FreeHint = false;
                // M6: a ring held for a pull whose spring just re-closed on it drops back into place
                if (id == _active && _mode == DragMode.Pull && _rings[id] != null)
                {
                    _rings[id].IsHeld = false;
                    _rings[id].SetPullOffset(Vector2.zero);
                    _rings[id].SetLifted(false);
                    _rings[id].SetPicked(false);
                    _active = -1;
                    _mode = DragMode.None;
                }
            }
            if (Deadlock.IsDead(_model)) _failCheckAt = Time.time + 0.7f;   // M5: a re-close that leaves no way out
        }

        /// <summary>Closest ring whose tube (or interior) is under the pointer.</summary>
        int Pick(Vector2 p)
        {
            int best = -1;
            float bestScore = float.MaxValue;
            for (int i = 0; i < _model.Rings.Count; i++)
            {
                var ring = _model.Rings[i];
                if (ring.Removed || _rings[i] == null || _rings[i].IsFlying) continue;
                float d = (p - ring.Center).magnitude;
                if (d > ring.Radius + 0.75f) continue; // generous: fingers are fat
                float score = Mathf.Abs(d - ring.Radius);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }
            return best;
        }

        // ------------------------------------------------------------------ debug / demo

        /// <summary>
        /// Plays the board by itself through the same paths the player uses: pull out every free ring, then make
        /// the gesture an exact search says is best (a full sweep in an allowed direction, a double sweep for a
        /// fresh clay coat), counted and completed like a tap. Works on mechanic boards (ratchets, keys, rust
        /// deadlines, springs). Used to smoke-test views and to confirm boards are completable in play mode.
        /// </summary>
        public void StartAutoSolve(float stepDelay = 0.6f) => StartCoroutine(AutoSolve(stepDelay));

        /// <summary>Last autosolve result for tools: null while running, then "solved" or the reason it stopped.</summary>
        public string AutoSolveResult { get; private set; }

        System.Collections.IEnumerator AutoSolve(float stepDelay)
        {
            AutoSolveResult = null;
            while (_inputLocked || (_hud != null && _hud.PopupOpen))
            {
                if (_hud != null && _hud.PopupOpen && !_levelOver) _hud.ClosePopupAndNotify();   // e.g. the intro card
                yield return null;
            }
            var model = _model;
            var memo = new Dictionary<string, int>();
            while (model == _model && !_model.IsSolved)
            {
                int pick = -1;
                for (int i = 0; i < _model.Rings.Count && pick < 0; i++)
                    if (_model.IsFree(i)) pick = i;
                if (pick >= 0)
                {
                    Vector2 dir = _model.Rings[pick].Center - _bounds.center;
                    PullOut(pick, dir.sqrMagnitude < 0.01f ? Vector2.up : dir, 10f);
                    yield return new WaitForSeconds(stepDelay * 0.5f);
                    continue;
                }
                var (ring, angle) = AutoPlan.Next(_def, _model, memo);
                if (ring < 0) { AutoSolveResult = "stuck at move " + _model.Moves; Debug.LogWarning("[Orcub] autosolve: " + AutoSolveResult); yield break; }
                FinishSpins();
                _pendingSnap = _model.Capture();
                CountMove();
                _spins.Start(ring, angle, 900f, new GestureTracker(_model, ring));
                while (_spins.IsSpinning(ring) && model == _model) yield return null;
                yield return new WaitForSeconds(stepDelay);
            }
            if (model == _model) AutoSolveResult = "solved in " + _model.Moves + " moves";
        }

        // ------------------------------------------------------------------ framing

        Rect ComputeBounds()
        {
            if (_model.Rings.Count == 0) return new Rect(-1, -1, 2, 2);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var r in _model.Rings)
            {
                float e = r.Radius + 0.45f; // tube + teeth
                minX = Mathf.Min(minX, r.Center.x - e);
                maxX = Mathf.Max(maxX, r.Center.x + e);
                minY = Mathf.Min(minY, r.Center.y - e);
                maxY = Mathf.Max(maxY, r.Center.y + e);
            }
            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        /// <summary>Re-fit next time (e.g. after the camera's target texture / aspect changed).</summary>
        public void ForceRefit()
        {
            _fitAspect = -1f;
            FitCamera();
        }

        public void FitCamera()
        {
            if (cam == null) return;
            // Fit the board inside the device safe area (notch, Dynamic Island, home indicator).
            Rect safe = SafeViewportRect();
            if (_hud != null && cam.targetTexture == null)
            {
                // keep the top bar + goal pill and the booster tray clear; the board fills what's between
                float top = LevelHud.TopReserve, bottom = _hud.BottomReserve;
                safe = new Rect(safe.x, safe.y + safe.height * bottom, safe.width, safe.height * (1f - top - bottom));
            }
            if (Mathf.Approximately(_fitAspect, cam.aspect) && safe == _fitSafeArea) return;
            _fitAspect = cam.aspect;
            _fitSafeArea = safe;
            cam.orthographic = true;
            float halfH = _bounds.height * 0.5f + framePadding;
            float halfW = _bounds.width * 0.5f + framePadding;
            float aspect = Mathf.Max(cam.aspect, 0.01f);
            cam.orthographicSize = Mathf.Max(halfH / safe.height, halfW / (aspect * safe.width));
            // Shift so the board centre lands on the safe area's centre.
            var c = _bounds.center;
            float offX = (0.5f - safe.center.x) * 2f * cam.orthographicSize * aspect;
            float offY = (0.5f - safe.center.y) * 2f * cam.orthographicSize;
            cam.transform.position = new Vector3(c.x + offX, c.y + offY, -10f);
            if (_backdrop != null) FitBackdrop();
            cam.backgroundColor = art.background;
            cam.clearFlags = CameraClearFlags.SolidColor;
            if (_glow != null)
            {
                _glow.transform.localPosition = c;
                float size = Mathf.Max(_bounds.width, _bounds.height) * 1.6f;
                _glow.transform.localScale = Vector3.one * size;
            }
        }

        Rect SafeViewportRect()
        {
            // Render-texture captures and the editor have no notch: use the whole view.
            if (cam.targetTexture != null || Screen.width <= 0 || Screen.height <= 0) return new Rect(0, 0, 1, 1);
            var sa = Screen.safeArea;
            var r = new Rect(sa.x / Screen.width, sa.y / Screen.height, sa.width / Screen.width, sa.height / Screen.height);
            return r.width <= 0.01f || r.height <= 0.01f ? new Rect(0, 0, 1, 1) : r;
        }

        /// <summary>Full-screen painted backdrop (unlit), always covering the camera view.</summary>
        void BuildBackdrop()
        {
            var go = new GameObject("Backdrop");
            go.transform.SetParent(_content, false);
            _backdrop = go.AddComponent<SpriteRenderer>();
            _backdrop.sprite = art.backgroundSprite;
            _backdrop.sortingOrder = -200;
            if (art.unlitMaterial != null) _backdrop.sharedMaterial = art.unlitMaterial;
            FitBackdrop();
        }

        void FitBackdrop()
        {
            if (_backdrop == null || cam == null) return;
            float viewH = cam.orthographicSize * 2f;
            float viewW = viewH * Mathf.Max(cam.aspect, 0.01f);
            var size = _backdrop.sprite.bounds.size;
            float s = Mathf.Max(viewW / size.x, viewH / size.y) * 1.02f; // cover, never letterbox
            _backdrop.transform.localScale = new Vector3(s, s, 1f);
            Vector3 p = _content.InverseTransformPoint(cam.transform.position);
            _backdrop.transform.localPosition = new Vector3(p.x, p.y, 0f);
        }

        /// <summary>Soft radial light behind the board so the playfield isn't a flat fill.</summary>
        void BuildGlow()
        {
            const int n = 128;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n - 0.5f, dy = (y + 0.5f) / n - 0.5f;
                float d = Mathf.Sqrt(dx * dx + dy * dy) * 2f;
                float a = Mathf.Clamp01(1f - d);
                a = a * a * (3f - 2f * a);
                var c = art.backgroundGlow;
                c.a = a;
                px[y * n + x] = c;
            }
            tex.SetPixels(px);
            tex.Apply();
            var sprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
            var go = new GameObject("Glow");
            go.transform.SetParent(_content, false);
            _glow = go.AddComponent<SpriteRenderer>();
            _glow.sprite = sprite;
            _glow.sortingOrder = -100;
            if (art.spriteMaterial != null) _glow.sharedMaterial = art.spriteMaterial;
        }
    }
}
