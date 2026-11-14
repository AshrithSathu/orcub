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
        [SerializeField] float framePadding = 0.8f;
        [Tooltip("Play the animated logo splash before the first board.")]
        [SerializeField] bool playSplash = true;
        [Tooltip("Show DEV level skip buttons + level stats in the HUD (turn off for release).")]
        [SerializeField] bool devTools = true;

        const int ContinueMoves = 5;
        int _levelIndex;
        LevelRules _rules;
        int _movesUsed, _bonusMoves;
        bool _usedAdContinue, _levelOver;
        float _winAt = -1f, _failCheckAt = -1f;
        int _winStars;
        bool _winNewBest;
        readonly Dictionary<int, float> _spinSpeed = new Dictionary<int, float>();
        LevelData _levelData;
        LevelHud _hud;

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
            _hud = LevelHud.Create(transform, art.uiFont, art.uiStar, devTools);
            _hud.Restart += () => LoadLevel(_levelIndex);
            _hud.Prev += () => LoadLevel(_levelIndex - 1);
            _hud.Next += () => LoadLevel(_levelIndex + 1);
            Build();

            if (playSplash && art.logoLetters != null && art.logoLetters.Length > 0)
            {
                HideRingsForIntro();
                _hud.SetVisible(false, instant: true);
                _splash = SplashView.Play(transform, art, cam, _fx, _crumbs,
                    onReveal: () => { DropInRings(); _hud.SetVisible(true); },
                    onDone: () => _splash = null);
            }
        }

        // ------------------------------------------------------------------ intro

        void HideRingsForIntro()
        {
            _inputLocked = true;
            foreach (var r in _rings) r.HideForIntro();
        }

        /// <summary>Rings land one after another, top of the board first, each with a soft tick.</summary>
        void DropInRings()
        {
            var order = new List<RingView>(_rings);
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

        public void Build()
        {
            _levelIndex = Progress.CurrentLevel;
            _levelData = LevelLibrary.Get(_levelIndex);
            var def = _levelData != null ? _levelData.ToDef() : Levels.Basic10();
            Build(def);
            _rules = LevelRules.For(def, tutorial: _levelIndex < 3);
            _movesUsed = 0;
            _bonusMoves = 0;
            _usedAdContinue = false;
            _levelOver = false;
            _winAt = _failCheckAt = -1f;
            if (_hud != null) _hud.ClosePopup();
            UpdateHud();
        }

        /// <summary>Jump to a level (wraps), save progress, and drop the new board in.</summary>
        public void LoadLevel(int index)
        {
            int count = Mathf.Max(1, LevelLibrary.Count);
            _levelIndex = ((index % count) + count) % count;
            Progress.CurrentLevel = _levelIndex;
            Build();
            if (Application.isPlaying)
            {
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
        }

        public void Build(LevelDef level)
        {
            Clear();
            _model = new PuzzleModel(level);
            _model.LinkReleased += OnLinkReleased;
            _model.Solved += OnSolved;

            _content = new GameObject("Board").transform;
            _content.SetParent(transform, false);

            if (art.backgroundSprite != null) BuildBackdrop();
            else BuildGlow();

            foreach (var ring in _model.Rings)
            {
                var view = new GameObject().AddComponent<RingView>();
                view.transform.SetParent(_content, false);
                view.Init(ring, art);
                _rings.Add(view);
            }
            foreach (var link in _model.Links)
            {
                var owner = _rings[link.Owner];
                var holder = new GameObject().AddComponent<HolderView>();
                holder.Init(link, _model, owner, art);
                owner.OwnedHolders.Add(holder);
                _holders.Add(holder);
            }

            _bounds = ComputeBounds();
            _fitAspect = -1f;
            FitCamera();
        }

        public void Clear()
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
                _hud?.ShowWin(_levelIndex + 1, _winStars, _movesUsed, _winNewBest, () => LoadLevel(_levelIndex + 1));
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
                _fx?.Pick(ring.Radius);
            }
            else
            {
                _mode = DragMode.Locked;
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
            _movesUsed++;
            _hud?.SetMoves(Mathf.Max(0, MovesLeft), bump: true);
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
            _hud?.ShowOutOfMoves(ContinueMoves, !_usedAdContinue,
                onWatchAd: () => _hud.ShowAdPlaceholder(1.4f, () =>
                {
                    _usedAdContinue = true;
                    _bonusMoves += ContinueMoves;
                    _levelOver = false;
                    _hud.SetMoves(MovesLeft, bump: true);
                    Haptics.Play(Haptics.Impact.Medium, 0.8f);
                }),
                onRetry: () => LoadLevel(_levelIndex));
        }

        void OnSolved()
        {
            _levelOver = true;
            _fx?.Cleared();
            _winStars = _rules.StarsFor(_movesUsed);
            _winNewBest = Progress.RecordWin(_levelIndex, _winStars);
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
            _holders[link.Id].Release();
            _fx?.Release();
            if (_crumbs != null)
            {
                Vector2 clamp = _model.ClampPoint(link);
                Vector2 away = clamp - _model.Rings[link.Target].Center;
                _crumbs.Burst(_content.TransformPoint(clamp), away, art.hardwareTint, art.RingColor(_model.Rings[link.Target].Color));
            }
            foreach (int id in new[] { link.Target, link.Owner })
            {
                if (!_model.IsFree(id)) continue;
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
                safe.height *= 1f - LevelHud.TopBandFraction; // leave the HUD band at the top free
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
