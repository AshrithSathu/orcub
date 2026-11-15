using System;
using Orcub.Core;
using UnityEngine;

namespace Orcub.Game
{
    /// <summary>
    /// The first-levels tutorial on the board (approved tutorial_v1): level 1 rotate → release → pull, level 2 the
    /// move counter, booster unlock cards at 5 / 8 / 12. Driven only by what happens on the board; input is never
    /// locked by it, Skip ends a lesson for good, and leaving a level just hides it.
    /// </summary>
    public sealed partial class BoardView
    {
        TutorialFlow _tutorial;
        TutorialOverlay _tutView;
        int _tutRing = -1;   // the ring the level-1 lesson is about

        void InitTutorial()
        {
            _tutorial = new TutorialFlow(new PrefsTutorialStore(), ClientGateway.Track);
            _tutView = TutorialOverlay.Create(transform, _kit, cam);
            _tutorial.Changed += OnTutorialChanged;
            _tutView.SkipPressed += () => _tutorial.Skip();
        }

        /// <summary>After the board has landed (and any "New gear!" card closed).</summary>
        void BeginTutorial()
        {
            if (_tutorial == null || _levelOver || _hud == null || _model == null) return;
            _tutRing = -1;
            _tutorial.Begin(_levelIndex + 1, Frontier + 1);
        }

        void EndTutorial() => _tutorial?.End();

        bool TutorialCovers(Vector2 screen) => _tutView != null && _tutView.Covers(screen);

        void OnTutorialChanged(TutorialStep step)
        {
            if (_hud == null || _tutView == null) return;
            _hud.SetBoosterSpotlight(null);
            switch (step)
            {
                case TutorialStep.None: _hud.SetTutorialDim(false, false); _tutView.Hide(); return;
                case TutorialStep.Moves: _hud.SetTutorialDim(false, true); _hud.SetGoalHidden(true); break;
                case TutorialStep.Rotate:
                case TutorialStep.Release:
                case TutorialStep.Pull: _hud.SetTutorialDim(true, true); break;
                default:
                    _hud.SetTutorialDim(true, false);
                    _hud.SetBoosterSpotlight(TutorialFlow.KindOf(step));
                    break;
            }
            if (!TryTarget(step, out var target)) { _tutorial.End(); return; }
            _tutView.Show(step, target);
            if (step == TutorialStep.Release) StartCoroutine(AdvanceRelease(_session));
        }

        System.Collections.IEnumerator AdvanceRelease(int session)
        {
            yield return new WaitForSecondsRealtime(1.3f);   // "Released!" auto-advances
            if (session == _session && _tutorial != null) _tutorial.OnReleaseShown();
        }

        bool TryTarget(TutorialStep step, out TutorialTarget t)
        {
            t = default;
            t.TrayTop = _hud.TrayTopScreen;
            t.GoalBottom = _hud.GoalBottomScreen;
            switch (step)
            {
                case TutorialStep.Rotate:
                    foreach (var l in _model.Links)
                    {
                        if (!l.Active || !_model.CanRotate(l.Target)) continue;
                        var ring = _model.Rings[l.Target];
                        _tutRing = l.Target;
                        Ring(ring, ref t);
                        float d = Mathf.DeltaAngle(ring.GapAngle, l.ClampAngle);
                        t.TurnSign = _model.DirectionAllowed(l.Target, d) ? Mathf.Sign(d) : -Mathf.Sign(d);
                        return true;
                    }
                    return false;
                case TutorialStep.Release:
                    if (_tutRing < 0 || _tutRing >= _model.Rings.Count) return false;
                    Ring(_model.Rings[_tutRing], ref t);
                    return true;
                case TutorialStep.Pull:
                    int pick = _tutRing >= 0 && _tutRing < _model.Rings.Count && !_model.Rings[_tutRing].Removed && _model.IsFree(_tutRing) ? _tutRing : -1;
                    if (pick < 0)
                        foreach (var r in _model.Rings) if (!r.Removed && _model.IsFree(r.Id)) { pick = r.Id; break; }
                    if (pick < 0) return false;
                    var pr = _model.Rings[pick];
                    Ring(pr, ref t);
                    // sideways toward the nearer edge (approved pull_A), never down into the tray
                    t.PullDirection = pr.Center.x < _bounds.center.x - 0.2f ? Vector2.left : Vector2.right;
                    return true;
                case TutorialStep.Moves:
                    t.Moves = _hud.MovesScreenRect;
                    return true;
                default:
                    var kind = TutorialFlow.KindOf(step);
                    if (!kind.HasValue) return false;
                    t.Booster = _hud.BoosterScreenRect(kind.Value);
                    return true;
            }
        }

        void Ring(Ring ring, ref TutorialTarget t)
        {
            t.RingCenter = _content.TransformPoint(ring.Center);
            t.RingRadius = ring.Radius * _content.lossyScale.x;
            var others = new System.Collections.Generic.List<Rect>();
            foreach (var o in _model.Rings)
            {
                if (o.Id == ring.Id || o.Removed) continue;
                Vector2 a = cam.WorldToScreenPoint(_content.TransformPoint(o.Center - Vector2.one * (o.Radius + 0.6f)));
                Vector2 b = cam.WorldToScreenPoint(_content.TransformPoint(o.Center + Vector2.one * (o.Radius + 0.6f)));
                others.Add(Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y)));
            }
            t.Others = others.ToArray();
        }

        /// <summary>Locks shown in the tray until each booster's unlock level (furthest level reached).</summary>
        void ApplyBoosterLocks()
        {
            if (_hud == null) return;
            foreach (BoosterKind k in Enum.GetValues(typeof(BoosterKind)))
                _hud.SetBoosterLocked(k, TutorialFlow.Locked(k, _levelIndex + 1, Frontier + 1));
        }
    }
}
