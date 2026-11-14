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
    public sealed class BoardView : MonoBehaviour
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
        readonly Dictionary<int, float> _spinSpeed = new Dictionary<int, float>();
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
        readonly Dictionary<int, float> _spins = new Dictionary<int, float>();
        readonly List<Link> _released = new List<Link>();
        readonly Dictionary<int, float> _turned = new Dictionary<int, float>(); // unwrapped rotation per ring, for ratchet detents
        readonly Dictionary<int, GestureTracker> _spinMoves = new Dictionary<int, GestureTracker>();   // tap spins: move completes when they stop
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

        // Drag state
        DragMode _mode;
        int _active = -1;
        Vector2 _pressWorld;
        float _pressTime;
        float _lastAngle;
        float _rotated;
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

            _fx = Feedback.Create(transform, art);
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
                onSettings: () => _account.OpenSettings());
            _account = AccountFlow.Create(transform, _kit, _home);
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
                    onReveal: () => LoaderScreen.Show(transform, _kit, onDone: boot => { ShowHome(); _account.AfterBoot(boot); }),
                    onDone: () => _splash = null);
            }
            else { ShowHome(); _account.AfterBoot(default); }
        }

        // ------------------------------------------------------------------ screens

        /// <summary>The next level the player hasn't beaten (home's Play button).</summary>
        static int Frontier => Mathf.Max(0, Progress.HighestUnlocked);   // infinite saga: no upper bound

        void ShowHome()
        {
            _inputLocked = true;
            _hud.ClosePopup();
            _hud.SetVisible(false, instant: true);
            _wreath?.Hide();
            _map.Hide();
            _chapters.Hide();
            _home.Show();
            LevelLibrary.Prefetch(Frontier);   // warm the next generated boards before Play is tapped
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

        /// <summary>Start a level from home or the map. Needs at least one life to start.</summary>
        void PlayLevel(int index)
        {
            if (Lives.Count <= 0)
            {
                _hud.ShowOutOfLives(
                    onRefillAd: () => ShowRewarded("refill_lives", () => { Lives.Refill(); PlayLevel(index); }, () => PlayLevel(index)),
                    onRefillCogs: null,
                    onBack: () => { });
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
            int remaining = order.Count;
            for (int i = 0; i < order.Count; i++)
            {
                var view = order[i];
                view.Landed = v =>
                {
                    if (--remaining == 0) _inputLocked = false; // landing is silent: no sound on level load
                };
                view.DropIn(0.07f * i);
            }
            if (order.Count == 0) _inputLocked = false;
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
            EndAttempt(AttemptOutcome.Quit);   // restart / skip before a result counts as a quit
            _plan = null;
            _levelIndex = index;
            if (!Application.isPlaying) { Build(index); return; }
            if (_loading != null) { StopCoroutine(_loading); _loading = null; }   // a newer request wins
            HideLevelLoader();
            if (LevelLibrary.TryGetVariants(index, out var ready) && ready != null && ready.Length > 0) StartLevel(index, null);
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
            if (!ok)
            {
                ClientGateway.Track("level_fallback", new { LevelIndex = index, Fallback = FallbackIndex(index), Faulted = task.IsFaulted });
                StartLevel(index, LevelLibrary.Get(FallbackIndex(index)));
            }
            else StartLevel(index, null);
        }

        void HideLevelLoader()
        {
            if (_loader != null) Destroy(_loader.gameObject);
            _loader = null;
        }

        /// <param name="fallback">Curated stand-in when the generated board wasn't ready in time; null = the real board.</param>
        void StartLevel(int index, LevelData fallback)
        {
            _fallbackData = fallback;
            BeginAttempt(fallback?.ToDef());
            Build(index, fallback ?? LevelLibrary.Get(index, _plan.Variant));
            _rules = LevelRules.For(_def, tutorial: _levelIndex < 3, extraMoves: _plan.ExtraMoves);
            UpdateHud();
            HideRingsForIntro();
            DropInRings();
            LevelLibrary.Prefetch(index + 1);   // keep the next window warm
            StartCoroutine(AfterLanding(_plan));
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

        System.Collections.IEnumerator AfterLanding(AttemptPlan plan)
        {
            int level = _levelIndex;
            var model = _model;
            while (_inputLocked) yield return null;   // after the rings have landed
            if (level != _levelIndex || model != _model || _levelOver || _hud == null) yield break;
            string key = IntroKey;
            if (key != null && !MechanicIntro.Seen(key))
            {
                _inputLocked = true;
                bool closed = false;
                ShowIntro(key, () => closed = true);
                while (!closed && model == _model) yield return null;
                MechanicIntro.MarkSeen(key);
                if (model == _model) _inputLocked = false;
            }
            if (model != _model) yield break;
            if (plan.ExtraMoves > 0 || plan.FreeHintGlow) yield return AnnounceAssist(plan);
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
            _hud.SetBoss(_def != null && _def.IsBoss);
            _hud.SetMoves(MovesLeft);
            var colours = new List<Color>();
            foreach (var r in _model.Rings) colours.Add(art.RingColor(r.Color));
            _hud.SetGoal(colours);
            foreach (var r in _model.Rings) if (r.Removed) _hud.MarkGoalFreed(r.Id);
            foreach (BoosterKind k in Enum.GetValues(typeof(BoosterKind))) _hud.SetBoosterCount(k, BoosterInventory.Count(k));
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
            _spinSpeed.Clear();
            _spinMoves.Clear();
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
                _wreath?.Show();
                _hud?.ShowWin(_levelIndex + 1, _winStars,
                    onNext: () => _account.AfterWin(_levelIndex + 1, () =>
                    {
                        if (AdsGateway.Service != null) AdsGateway.Service.MaybeShowInterstitial("level_end", () => LoadLevel(_levelIndex + 1));
                        else LoadLevel(_levelIndex + 1);
                    }),
                    onHome: () => _account.AfterWin(_levelIndex + 1, () => GoHome(costsLife: false)));
            }
            if (_failCheckAt > 0f && Time.time >= _failCheckAt && _spins.Count == 0 && _active < 0)
            {
                _failCheckAt = -1f;
                CheckOutOfMoves();
            }

            FitCamera();
            HandlePointer();
            StepSpins(Time.deltaTime);
            if (_spins.Count == 0 && _active < 0) FinishFeature();
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
            if (pointer.press.wasPressedThisFrame && _hud != null && _hud.Covers(screen)) return; // HUD button
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
            FinishFeature();
            _requiredBefore = Adaptive.RequiredRotation(_model);
            _thinkSeconds = Time.unscaledTime - _lastGesture;
            ClearHints();   // the player is acting; the hint has done its job

            _pressWorld = p;
            _lastPointer = p;
            _pressTime = Time.time;
            _rotated = 0f;
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
                EndSpin(_active);                  // a tap spin still running ends its own move first
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
                    if (isTap && _rotated < 6f)
                    {
                        StartSmartSpin(_active);
                        CountMove();
                        _spinMoves[_active] = new GestureTracker(_model, _active);   // completes when the spin stops
                    }
                    else if (_gesture != null && _gesture.Counts)
                    {
                        CountMove();
                        _gesture.Complete();       // exactly once per counted gesture
                        _failCheckAt = Time.time + 0.15f;
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
                        PullOut(_active, dir, _pullVelocity.magnitude);
                    }
                    else view.SetLifted(false);
                    break;
            }
            view.SetPicked(false);
            _active = -1;
            _mode = DragMode.None;
        }

        void StepSpins(float dt)
        {
            if (_spins.Count == 0) return;
            var keys = new List<int>(_spins.Keys);
            foreach (var id in keys)
            {
                float remaining = _spins[id];
                float speed = _spinSpeed.TryGetValue(id, out float sp) ? sp : tapSpinSpeed;
                float step = Mathf.Sign(remaining) * Mathf.Min(Mathf.Abs(remaining), speed * dt);
                if (TurnRing(id, step, step / Mathf.Max(dt, 1e-4f)) != RotateOutcome.Rotated)
                {
                    EndSpin(id);
                    continue;
                }
                remaining -= step;
                if (Mathf.Abs(remaining) < 0.01f) EndSpin(id);
                else _spins[id] = remaining;
            }
        }

        // ------------------------------------------------------------------ moves, win, fail

        int MovesLeft => _rules.MoveLimit + _bonusMoves - _movesUsed;

        /// <summary>Stops a spin; a tap spin's counted move completes here (spring timers, rust deadlines).</summary>
        void EndSpin(int id)
        {
            if (!_spins.Remove(id) && !_spinMoves.ContainsKey(id)) return;
            _spinSpeed.Remove(id);
            if (_spinMoves.TryGetValue(id, out var g)) { _spinMoves.Remove(id); g.Complete(); }
            _failCheckAt = Time.time + 0.15f;
        }

        void RefuseTurn(int id)
        {
            _refusedAt = Time.time;
            _fx?.Refused();
            int mate = _model.LinkedPartner(id);
            foreach (int r in new[] { id, mate })
            {
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
        }

        /// <summary>
        /// A tap spins the ring the short way to its nearest clamp, just far enough to release it
        /// (or a fixed step if nothing is clamped on it), so every tap is a meaningful move.
        /// </summary>
        void StartSmartSpin(int id)
        {
            float delta = SmartSpinDelta(_model, id, tapSpin);
            _spins[id] = delta;
            // Short hops are quick, long ones a touch slower but never sluggish.
            _spinSpeed[id] = Mathf.Max(tapSpinSpeed, Mathf.Abs(delta) / 0.45f);
        }

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
            foreach (int r in new[] { id, mate })
            {
                if (r < 0) continue;
                var ring = m.Rings[r];
                float sign = r == id ? 1f : -1f;   // the partner turns the opposite way
                foreach (var linkId in ring.Incoming)
                {
                    var l = m.Links[linkId];
                    if (!l.Active || (l.Tinted && l.Color != ring.Color)) continue;
                    float reach = PuzzleModel.GapHalfAngle - l.ClampHalfAngle;
                    float d = Mathf.DeltaAngle(ring.GapAngle, l.ClampAngle);
                    float turn;
                    if (l.InGap) turn = (d >= 0f ? 1f : -1f) * 360f;
                    else
                    {
                        turn = Mathf.Sign(d) * (Mathf.Max(0f, Mathf.Abs(d) - reach) + 3f);
                        if (l.Clay && l.Passes == 0) turn += Mathf.Sign(turn) * 360f;
                    }
                    turn *= sign;
                    if (!Allowed(turn)) turn -= Mathf.Sign(turn) * (l.Clay && l.Passes == 0 ? 720f : 360f);
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
            if (_levelOver || _model.IsSolved || MovesLeft > 0 || !HasActiveLinks()) return;
            _levelOver = true;
            _wreath?.Show();
            _hud?.ShowOutOfMoves(ContinueMoves, !_usedAdContinue,
                onWatchAd: () => ShowRewarded("continue_moves", () =>
                {
                    _wreath?.Hide();
                    _usedAdContinue = true;
                    _attemptContinues++;
                    _bonusMoves += ContinueMoves;
                    _levelOver = false;
                    _hud.SetMoves(MovesLeft, bump: true);
                    Haptics.Play(Haptics.Impact.Medium, 0.8f);
                }),
                onRetry: () => { EndAttempt(AttemptOutcome.Lose); SpendLifeThen(() => LoadLevel(_levelIndex), reopen: CheckOutOfMovesAgain); },
                onHome: () => { EndAttempt(AttemptOutcome.Lose); GoHome(costsLife: true); });
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
            if (Lives.TrySpend())
            {
                action();
                return;
            }
            _wreath?.Show(frame: false);
            _hud.ShowOutOfLives(
                onRefillAd: () => ShowRewarded("refill_lives", () =>
                {
                    Lives.Refill();
                    if (Lives.TrySpend()) action();
                }, () => SpendLifeThen(action, reopen)),
                onRefillCogs: null,   // enabled once Brass Cogs exist (step 4)
                onBack: reopen);
        }

        // ------------------------------------------------------------------ boosters

        void UseBooster(BoosterKind kind)
        {
            if (_model == null || _levelOver || _inputLocked || _hud.PopupOpen) return;
            if (_active >= 0 || _spins.Count > 0) return;   // a ring is mid-turn; the tap is ignored silently
            if (BoosterInventory.Count(kind) <= 0)
            {
                Haptics.Notify(Haptics.Notice.Warning);
                _hud.ShowToast($"No {BoosterName(kind)}s left");   // shop arrives with the currency step
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
                    }
                    else refusal = "Nothing to undo yet";
                    break;
                case BoosterKind.Unclamp:
                    int linkId = Unclamp.PickLink(_model);
                    if (linkId < 0 || !_model.ReleaseLink(linkId)) refusal = "No clamps left";
                    else ClearHints();
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
            BoosterInventory.TrySpend(kind);
            _attemptBoosters++;
            Haptics.Play(Haptics.Impact.Medium, 0.8f);
            _hud.SetBoosterCount(kind, BoosterInventory.Count(kind));
            _hud.SetBoosterReady(BoosterKind.Undo, _undo.Count > 0);
        }

        // ------------------------------------------------------------------ adaptive difficulty

        /// <summary>
        /// Freezes this attempt's plan: a player who keeps losing a level gets a few extra moves and,
        /// after more losses, a free hint. Never fewer moves than the level's authored budget.
        /// </summary>
        void BeginAttempt(LevelDef fallback = null)
        {
            _attemptStart = _lastGesture = Time.unscaledTime;
            _attemptBoosters = _attemptContinues = _lockedTouches = 0;
            _featurePending = false; _gestures = 0;
            _adaptive ??= new Adaptive();
            ClientGateway.Configure?.Invoke(_adaptive);
            // a curated stand-in (generated board not ready in time) is recorded against this index as given
            _plan = fallback != null ? _adaptive.BeginAttempt(_levelIndex, fallback) : _adaptive.BeginAttempt(_levelIndex);
            _attemptOpen = true;
            ClientGateway.Track("level_start", new { LevelIndex = _levelIndex, Variant = _plan.Variant, Plan = new { variant = _plan.Variant, extraMoves = _plan.ExtraMoves, freeHintGlow = _plan.FreeHintGlow, reason = _plan.Reason, moveLimit = _plan.MoveLimit } });
        }

        System.Collections.IEnumerator AnnounceAssist(AttemptPlan plan)
        {
            int level = _levelIndex;
            while (_inputLocked) yield return null;   // after the rings have landed
            if (level != _levelIndex || _levelOver || _hud == null) yield break;
            if (plan.FreeHintGlow && Hints.TryFind(_model, out var hint) && _rings[hint.Ring] != null)
            {
                if (hint.PullOut) _rings[hint.Ring].ShowPullHint();
                else _rings[hint.Ring].ShowHint(hint.Delta);
                _hud.ShowToast(plan.ExtraMoves > 0 ? $"+{plan.ExtraMoves} moves and a free hint" : "Here's a free hint");
            }
            else if (plan.ExtraMoves > 0) _hud.ShowToast($"+{plan.ExtraMoves} moves this time");
        }

        void EndAttempt(AttemptOutcome outcome)
        {
            if (!_attemptOpen || _adaptive == null) return;
            FinishFeature();
            _attemptOpen = false;
            if (_gestures == 0)
            {
                _adaptive.CancelAttempt();   // left without touching the board: not evidence of anything
                return;
            }
            try
            {
                _adaptive.EndAttempt(new AttemptResult(outcome, _movesUsed, Time.unscaledTime - _attemptStart,
                    _attemptBoosters, _attemptContinues));
                ClientGateway.Track("level_end", _adaptive.Log.Entries[_adaptive.Log.Entries.Count - 1]);
                ClientGateway.Save?.Invoke();
            }
            catch (Exception e) { Debug.LogWarning("Adaptive record skipped: " + e.Message); }
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
            _levelOver = true;
            _fx?.Cleared();
            _winStars = _rules.StarsFor(_movesUsed);
            Progress.RecordWin(_levelIndex, _winStars);
            LevelLibrary.Prefetch(_levelIndex + 1);   // the frontier moved: warm the next boards
            _account.RecordWin(_levelIndex + 1);   // may wait for the tracking pre-prompt
            EndAttempt(AttemptOutcome.Win);
            _winAt = Time.time + 1.0f;   // let the last ring fly off and the chime ring first
        }

        /// <summary>Rotates through the model and fires a ratchet tick for every gear detent crossed.</summary>
        RotateOutcome TurnRing(int id, float delta, float speed, GestureTracker gesture = null)
        {
            // watch clamps on this ring and its gear partner: colour refusals and clay cracks
            _probe.Clear();
            int mate = _model.LinkedPartner(id);
            foreach (int r in new[] { id, mate })
                if (r >= 0)
                    foreach (int linkId in _model.Rings[r].Incoming)
                    {
                        var l = _model.Links[linkId];
                        if (l.Active && (l.Clay || l.Tinted)) _probe.Add((l, l.Passes, l.InGap));
                    }
            var outcome = gesture != null ? gesture.Turn(delta) : _model.Rotate(id, delta);
            if (outcome != RotateOutcome.Rotated) return outcome;
            foreach (var (l, passes, inGap) in _probe)
            {
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

        void PullOut(int id, Vector2 dir, float speed)
        {
            if (!_model.IsFree(id)) return;
            EndSpin(id);   // a tap spin that freed this ring finishes its move before the pull
            if (!_model.TryRemove(id)) return;
            _rings[id].FreeHint = false;
            _rings[id].FlyAway(dir, speed);
            _fx?.PullOut(_model.Rings[id].Radius);
        }

        void OnLinkReleased(Link link)
        {
            if (_holders[link.Id] != null) _holders[link.Id].Release();
            _fx?.Release();
            if (_crumbs != null)
            {
                Vector2 clamp = _model.ClampPoint(link);
                Vector2 away = clamp - _model.Rings[link.Target].Center;
                _crumbs.Burst(_content.TransformPoint(clamp), away, art.hardwareTint, art.RingColor(_model.Rings[link.Target].Color));
            }
            foreach (int id in new[] { link.Target, link.Owner })
            {
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
            bool stuck = false;
            foreach (int id in ring.Incoming) stuck |= _model.Links[id].Active;
            if (stuck) TellOnce("frozen", "Rusted solid! Undo to try again");
        }

        void OnClampReclosed(Link link)
        {
            if (_holders[link.Id] != null) _holders[link.Id].Reclose();
            _fx?.Snapped();
            foreach (int id in new[] { link.Target, link.Owner })
                if (_rings[id] != null && !_model.IsFree(id)) _rings[id].FreeHint = false;
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
                _pendingSnap = _model.Capture();
                _spins[ring] = angle;
                _spinSpeed[ring] = 900f;
                CountMove();
                _spinMoves[ring] = new GestureTracker(_model, ring);
                while (_spins.ContainsKey(ring) && model == _model) yield return null;
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
