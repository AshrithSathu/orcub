using System;
using System.Collections.Generic;
using Orcub.Core;
using UnityEngine;

namespace Orcub.Game
{
    /// <summary>
    /// The tap spins running on a board. A tap's counted move completes when its spin stops (spring timers,
    /// rust deadlines). Review e, H1: gestures stay strictly sequential, as the solver assumes. Before any new
    /// gesture or booster starts, <see cref="FinishAll"/> turns every running spin the rest of the way at once and
    /// completes its move; only then is the Undo snapshot taken and the new gesture tracked.
    /// Pure C# (no MonoBehaviour) so the accounting is unit-tested.
    /// </summary>
    public sealed class SpinSet
    {
        /// <summary>Turns ring <c>id</c> by <c>delta</c> at <c>speed</c> deg/s (BoardView adds ticks and probes).</summary>
        public delegate RotateOutcome TurnFn(int id, float delta, float speed);

        readonly Dictionary<int, float> _remaining = new Dictionary<int, float>();
        readonly Dictionary<int, float> _speed = new Dictionary<int, float>();
        readonly Dictionary<int, GestureTracker> _moves = new Dictionary<int, GestureTracker>();
        readonly List<int> _keys = new List<int>();

        /// <summary>Raised after a spin stopped (and its move, if any, completed).</summary>
        public event Action<int> Ended;

        public int Count => _remaining.Count;
        public bool IsSpinning(int id) => _remaining.ContainsKey(id);
        /// <summary>True while anything is running or a tap's move is still waiting to complete.</summary>
        public bool Busy => _remaining.Count > 0 || _moves.Count > 0;

        /// <param name="move">The tap's counted move, completed when the spin stops; null for visual-only spins.</param>
        public void Start(int id, float delta, float speed, GestureTracker move)
        {
            _remaining[id] = delta;
            _speed[id] = speed;
            if (move != null) _moves[id] = move;
        }

        /// <summary>Advances every spin by one frame; spins that finish or get blocked end (completing their move).</summary>
        public void Step(float dt, float defaultSpeed, TurnFn turn)
        {
            if (_remaining.Count == 0) return;
            _keys.Clear();
            _keys.AddRange(_remaining.Keys);
            foreach (int id in _keys)
            {
                if (!_remaining.TryGetValue(id, out float remaining)) continue;   // ended by an earlier callback
                float speed = _speed.TryGetValue(id, out float sp) ? sp : defaultSpeed;
                float step = Mathf.Sign(remaining) * Mathf.Min(Mathf.Abs(remaining), speed * dt);
                if (turn(id, step, step / Mathf.Max(dt, 1e-4f)) != RotateOutcome.Rotated) { End(id); continue; }
                remaining -= step;
                if (Mathf.Abs(remaining) < 0.01f) End(id);
                else _remaining[id] = remaining;
            }
        }

        /// <summary>Stops a spin; a tap spin's counted move completes here. Returns false when nothing was running.</summary>
        public bool End(int id)
        {
            bool spun = _remaining.Remove(id);
            bool moved = _moves.TryGetValue(id, out var g);
            if (!spun && !moved) return false;
            _speed.Remove(id);
            if (moved) { _moves.Remove(id); g.Complete(); }
            Ended?.Invoke(id);
            return true;
        }

        /// <summary>
        /// H1: fast-forwards every running spin (the rest of its turn in one step) and completes its move, so a new
        /// gesture, booster or pull never interleaves with an unfinished one. Returns true when anything was finished.
        /// </summary>
        public bool FinishAll(TurnFn turn)
        {
            if (!Busy) return false;
            _keys.Clear();
            _keys.AddRange(_remaining.Keys);
            foreach (int id in _keys)
            {
                if (_remaining.TryGetValue(id, out float rest) && Mathf.Abs(rest) >= 0.01f) turn(id, rest, 0f);
                End(id);
            }
            _keys.Clear();
            _keys.AddRange(_moves.Keys);   // a tap whose spin was already removed: still complete its move
            foreach (int id in _keys) End(id);
            return true;
        }

        /// <summary>Drops everything without completing (board rebuilt).</summary>
        public void Clear()
        {
            _remaining.Clear();
            _speed.Clear();
            _moves.Clear();
        }
    }
}
