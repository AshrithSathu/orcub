using Orcub.Core;
using UnityEngine;

namespace Orcub.Game
{
    /// <summary>
    /// One player gesture on a ring (a drag, or the spin a tap starts), and the move-accounting rules from
    /// ArtSource/mechanics/README.md:
    ///  - every drag sample goes through <see cref="Turn"/>; samples never complete a move by themselves;
    ///  - a ratchet turned the wrong way is refused (<see cref="RotateOutcome.WrongDirection"/>): nothing turns and
    ///    nothing is charged, however long the player drags;
    ///  - a drag counts once it turned the ring a real amount or made progress (a release, a clay pass);
    ///  - <see cref="Complete"/> calls <see cref="PuzzleModel.CompleteMove"/> exactly once, however often it is called.
    /// </summary>
    public sealed class GestureTracker
    {
        public const float CountedDegrees = 8f;

        readonly PuzzleModel _model;
        readonly int _startSignature;

        public int Ring { get; }
        public float Rotated { get; private set; }
        public float WrongTurn { get; private set; }
        public bool Completed { get; private set; }

        public GestureTracker(PuzzleModel model, int ring)
        {
            _model = model;
            Ring = ring;
            _startSignature = Signature(model);
        }

        public RotateOutcome Turn(float delta)
        {
            var outcome = _model.Rotate(Ring, delta);
            if (outcome == RotateOutcome.Rotated) Rotated += Mathf.Abs(delta);
            else if (outcome == RotateOutcome.WrongDirection) WrongTurn += Mathf.Abs(delta);
            return outcome;
        }

        /// <summary>Forget refused degrees once the refusal has been shown (so the next cue needs a new push).</summary>
        public void ClearWrongTurn() => WrongTurn = 0f;

        /// <summary>True when this drag should cost a move (and so complete one).</summary>
        public bool Counts => Rotated >= CountedDegrees || Signature(_model) != _startSignature;

        /// <summary>Ends the counted gesture: the model's move boundary (spring timers, rust deadlines). Idempotent.</summary>
        public void Complete()
        {
            if (Completed) return;
            Completed = true;
            _model.CompleteMove();
        }

        /// <summary>Changes whenever real progress happened: a clamp released or re-closed, a clay pass.</summary>
        public static int Signature(PuzzleModel m)
        {
            int sig = 17;
            foreach (var l in m.Links) sig = sig * 31 + (l.Active ? 1 : 0) + l.Passes * 3;
            return sig;
        }
    }
}
