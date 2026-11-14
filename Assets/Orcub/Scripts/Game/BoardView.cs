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
        bool _usedAdContinue, _levelOver;
        float _winAt = -1f, _failCheckAt = -1f;
        int _winStars;
        readonly Dictionary<int, float> _spinSpeed = new Dictionary<int, float>();
        LevelData _levelData;
        LevelHud _hud;
        WreathView _wreath;
        UiKit _kit;
        HomeScreen _home;
        MapScreen _map;
        readonly Stack<(ModelSnapshot snap, int moves)> _undo = new Stack<(ModelSnapshot, int)>();
        ModelSnapshot _pendingSnap;

        PuzzleModel _model;
        readonly List<RingView> _rings = new List<RingView>();
        readonly List<HolderView> _holders = new List<HolderView>();
        readonly Dictionary<int, float> _spins = new Dictionary<int, float>();
        readonly List<Link> _released = new List<Link>();
        readonly Dictionary<int, float> _turned = new Dictionary<int, float>(); // unwrapped rotation per ring, for ratchet detents
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
            _home = HomeScreen.Create(transform, _kit, _fx, onPlay: () => PlayLevel(Frontier), onMap: ShowMap,
                onSettings: () => _hud.ShowSettings(onClose: () => { }));
            _map = MapScreen.Create(transform, _kit, onPlayLevel: PlayLevel, onBack: ShowHome);
            _home.Hide();

            // The frontier board is built (hidden) so the felt backdrop is there for the splash.
            Build(Frontier);
            HideRingsForIntro();
            _hud.SetVisible(false, instant: true);
            if (playSplash && art.logoLetters != null && art.logoLetters.Length > 0)
            {
                _splash = SplashView.Play(transform, art, cam, _fx, _crumbs,
                    onReveal: () => LoaderScreen.Show(transform, _kit, onDone: ShowHome),
                    onDone: () => _splash = null);
            }
            else ShowHome();
        }

        // ------------------------------------------------------------------ screens

        /// <summary>The next level the player hasn't beaten (home's Play button).</summary>
        static int Frontier => Mathf.Clamp(Progress.HighestUnlocked, 0, Mathf.Max(0, LevelLibrary.Count - 1));

        void ShowHome()
        {
            _inputLocked = true;
            _hud.ClosePopup();
            _hud.SetVisible(false, instant: true);
            _wreath?.Hide();
            _map.Hide();
            _home.Show();
        }

        void ShowMap()
        {
            _home.Hide();
            _map.Show();
        }

        /// <summary>Start a level from home or the map. Needs at least one life to start.</summary>
        void PlayLevel(int index)
        {
            if (Lives.Count <= 0)
            {
                _hud.ShowOutOfLives(
                    onRefillAd: () => _hud.ShowAdPlaceholder(1.4f, () => { Lives.Refill(); PlayLevel(index); }),
                    onRefillCogs: null,
                    onBack: () => { });
                return;
            }
            _home.Hide();
            _map.Hide();
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
            int target = Mathf.Clamp(_levelIndex + delta, 0, Mathf.Max(0, LevelLibrary.Count - 1));
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

        public void Build(int index)
        {
            _levelIndex = index;
            _levelData = LevelLibrary.Get(_levelIndex);
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

        /// <summary>Jump to a level (wraps), save progress, and drop the new board in.</summary>
        public void LoadLevel(int index)
        {
            int count = Mathf.Max(1, LevelLibrary.Count);
            _levelIndex = ((index % count) + count) % count;
            EndAttempt(AttemptOutcome.Quit);   // restart / skip before a result counts as a quit
            Build(_levelIndex);
            if (Application.isPlaying)
            {
                BeginAttempt();
                HideRingsForIntro();
                DropInRings();
            }
        }

        void UpdateHud()
        {
            if (_hud == null) return;
            string info = _levelData == null ? "" :
                $"{_levelData.rings.Length} rings · {_levelData.links.Length} holders · depth {_levelData.depth} · " +
                $"best {_rules.MinMoves} · 3★ ≤{_rules.ThreeStar} · 2★ ≤{_rules.TwoStar} · limit {_rules.MoveLimit} · diff {_levelData.difficulty:F1}";
            _hud.SetLevel(_levelIndex + 1, info);
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
                if (ring.Removed) { _rings.Add(null); continue; }
                var view = new GameObject().AddComponent<RingView>();
                view.transform.SetParent(_content, false);
                view.Init(ring, art);
                _rings.Add(view);
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
            _turned.Clear();
            _rings.Clear();
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
                    onNext: () => LoadLevel(_levelIndex + 1),
                    onHome: () => GoHome(costsLife: false));
            }
            if (_failCheckAt > 0f && Time.time >= _failCheckAt && _spins.Count == 0 && _active < 0)
            {
                _failCheckAt = -1f;
                CheckOutOfMoves();
            }

            FitCamera();
            HandlePointer();
            StepSpins(Time.deltaTime);
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
                _spins.Remove(_active);
                _pendingSnap = _model.Capture();   // pushed onto the undo stack if this becomes a move
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
                // Touching a ring that is still holding another is a wrong move: it costs a move too.
                CountMove();
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
                    _rotated += Mathf.Abs(delta);
                    TurnRing(_active, delta, delta / dt);
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
                    }
                    else if (_rotated >= 8f)
                    {
                        CountMove();
                        _failCheckAt = Time.time + 0.15f;
                    }
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
                    _spins.Remove(id);
                    _failCheckAt = Time.time + 0.15f;
                    continue;
                }
                remaining -= step;
                if (Mathf.Abs(remaining) < 0.01f)
                {
                    _spins.Remove(id);
                    _failCheckAt = Time.time + 0.15f;
                }
                else _spins[id] = remaining;
            }
        }

        // ------------------------------------------------------------------ moves, win, fail

        int MovesLeft => _rules.MoveLimit + _bonusMoves - _movesUsed;

        void CountMove()
        {
            _undo.Push((_pendingSnap ?? _model.Capture(), _movesUsed));
            _pendingSnap = null;
            _movesUsed++;
            _hud?.SetMoves(Mathf.Max(0, MovesLeft), bump: true);
            _hud?.SetBoosterReady(BoosterKind.Undo, true);
        }

        /// <summary>
        /// A tap spins the ring the short way to its nearest clamp, just far enough to release it
        /// (or a fixed step if nothing is clamped on it), so every tap is a meaningful move.
        /// </summary>
        void StartSmartSpin(int id)
        {
            var ring = _model.Rings[id];
            float delta = tapSpin, best = float.MaxValue;
            foreach (var linkId in ring.Incoming)
            {
                var l = _model.Links[linkId];
                if (!l.Active) continue;
                float reach = PuzzleModel.GapHalfAngle - l.ClampHalfAngle;
                float d = Mathf.DeltaAngle(ring.GapAngle, l.ClampAngle);
                float need = Mathf.Max(0f, Mathf.Abs(d) - reach) + 3f;
                if (need < best)
                {
                    best = need;
                    delta = Mathf.Sign(d) * need;
                }
            }
            _spins[id] = delta;
            // Short hops are quick, long ones a touch slower but never sluggish.
            _spinSpeed[id] = Mathf.Max(tapSpinSpeed, Mathf.Abs(delta) / 0.45f);
        }

        bool HasActiveLinks()
        {
            foreach (var l in _model.Links)
                if (l.Active) return true;
            return false;
        }

        void CheckOutOfMoves()
        {
            if (_levelOver || _model.IsSolved || MovesLeft > 0 || !HasActiveLinks()) return;
            _levelOver = true;
            _wreath?.Show();
            _hud?.ShowOutOfMoves(ContinueMoves, !_usedAdContinue,
                onWatchAd: () => _hud.ShowAdPlaceholder(1.4f, () =>
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
            _wreath?.Show();
            ShowPauseCard();
        }

        void ShowPauseCard()
        {
            _hud.ShowPause(
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
            _wreath?.Show();
            _hud.ShowOutOfLives(
                onRefillAd: () => _hud.ShowAdPlaceholder(1.4f, () =>
                {
                    Lives.Refill();
                    if (Lives.TrySpend()) action();
                }),
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
        void BeginAttempt()
        {
            _attemptStart = Time.time;
            _attemptBoosters = _attemptContinues = 0;
            _attemptOpen = false;
            if (_def == null) return;
            _adaptive ??= new Adaptive();
            AttemptPlan plan;
            try { plan = _adaptive.BeginAttempt(_levelIndex, _def); }
            catch (Exception e) { Debug.LogWarning("Adaptive plan skipped: " + e.Message); return; }
            _attemptOpen = true;
            if (plan.ExtraMoves > 0)
            {
                _rules = LevelRules.For(_def, tutorial: _levelIndex < 3, extraMoves: plan.ExtraMoves);
                UpdateHud();
            }
            if (plan.ExtraMoves > 0 || plan.FreeHintGlow) StartCoroutine(AnnounceAssist(plan));
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
            _attemptOpen = false;
            if (outcome == AttemptOutcome.Quit && _movesUsed == 0)
            {
                _adaptive.CancelAttempt();   // left without touching the board: not evidence of anything
                return;
            }
            try
            {
                _adaptive.EndAttempt(new AttemptResult(outcome, _movesUsed, Time.time - _attemptStart,
                    _attemptBoosters, _attemptContinues));
            }
            catch (Exception e) { Debug.LogWarning("Adaptive record skipped: " + e.Message); }
        }

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
            EndAttempt(AttemptOutcome.Win);
            _winAt = Time.time + 1.0f;   // let the last ring fly off and the chime ring first
        }

        /// <summary>Rotates through the model and fires a ratchet tick for every gear detent crossed.</summary>
        RotateOutcome TurnRing(int id, float delta, float speed)
        {
            var outcome = _model.Rotate(id, delta);
            if (outcome != RotateOutcome.Rotated) return outcome;
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
            if (!_model.TryRemove(id)) return;
            _spins.Remove(id);
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
        /// Plays the board by itself through the same paths a tap uses: pull out any free ring, otherwise
        /// spin a ring that can turn and still has clamps on it. Used to smoke-test views and animations.
        /// </summary>
        public void StartAutoSolve(float stepDelay = 0.6f) => StartCoroutine(AutoSolve(stepDelay));

        System.Collections.IEnumerator AutoSolve(float stepDelay)
        {
            while (_inputLocked) yield return null;
            var model = _model;
            while (model == _model && !_model.IsSolved)
            {
                int pick = -1;
                for (int i = 0; i < _model.Rings.Count && pick < 0; i++)
                    if (_model.IsFree(i)) pick = i;
                if (pick >= 0)
                {
                    Vector2 dir = _model.Rings[pick].Center - _bounds.center;
                    PullOut(pick, dir.sqrMagnitude < 0.01f ? Vector2.up : dir, 10f);
                    yield return new WaitForSeconds(stepDelay);
                    continue;
                }
                for (int i = 0; i < _model.Rings.Count && pick < 0; i++)
                {
                    if (!_model.CanRotate(i) || _spins.ContainsKey(i)) continue;
                    foreach (var id in _model.Rings[i].Incoming)
                        if (_model.Links[id].Active) { pick = i; break; }
                }
                if (pick < 0) yield break; // stuck (should not happen on a solvable board)
                _spins[pick] = tapSpin;
                yield return new WaitForSeconds(stepDelay);
            }
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
