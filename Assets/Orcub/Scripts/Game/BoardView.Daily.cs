using System;
using System.Collections.Generic;
using Orcub.Core;
using UnityEngine;

namespace Orcub.Game
{
    /// <summary>
    /// Daily loop on the board (approved features_v1, Daily README "UI integration hooks"):
    ///  - the Daily Puzzle board: the service's LevelData with LevelRules.For, no adaptive selection, no saga
    ///    progress / stars / level_end, no life cost, no continues; loss or quit reports Complete(false);
    ///  - the win streak on saga attempts: one attempt id per board, the free attempt-local booster it carries,
    ///    continues, and win / loss / quit at the end of the attempt;
    ///  - the calm moments for the daily gift and the notification opt-in (never at launch).
    /// </summary>
    public sealed partial class BoardView
    {
        DailyFlow _dailyFlow;
        bool _daily, _dailyEnded;
        LevelData _dailyData;
        int _dailyRequest;
        string _streakAttempt;
        BoosterKind? _freeBooster;
        readonly Dictionary<int, int> _tries = new Dictionary<int, int>();
        bool _optInAfterWin;

        void InitDaily() => _dailyFlow = DailyFlow.Create(transform, _kit, play: PlayDaily, back: ShowHome);

        // ------------------------------------------------------------------ daily screen + board

        void ShowDaily()
        {
            LeaveDaily();
            CancelLevelStart();
            _inputLocked = true;
            _hud.ClosePopup();
            _hud.SetVisible(false, instant: true);
            _wreath?.Hide();
            _map.Hide();
            _chapters.Hide();
            _home.Hide();
            _dailyFlow.OpenScreen();
        }

        async void PlayDaily()
        {
            var port = DailyGateway.Current;
            if (port == null || !port.CanPlay) return;
            int request = ++_dailyRequest;
            var loader = LoaderScreen.ShowLevel(transform, _kit);
            LevelData data = null;
            try { data = await port.PrepareDaily(); }
            catch (Exception e) { Debug.LogWarning("Daily puzzle not ready: " + e.Message); }
            if (loader != null) Destroy(loader.gameObject);
            if (this == null || request != _dailyRequest || _dailyFlow == null || !_dailyFlow.ScreenOpen) return;   // navigated away
            if (data == null || !port.StartDaily())
            {
                _dailyFlow.ShowScreenAgain();   // the day rolled over or the board couldn't be made: the screen redraws
                return;
            }
            StartDaily(data);
        }

        void StartDaily(LevelData data)
        {
            EndAttempt(AttemptOutcome.Quit);   // a saga attempt never stays open behind the daily board
            CancelLevelStart();
            _daily = true;
            _dailyEnded = false;
            _dailyData = data;
            _dailyFlow.HideScreen();
            _home.Hide();
            _map.Hide();
            _chapters.Hide();
            _hud.SetVisible(true);
            _plan = AttemptPlan.Neutral();
            _fallbackData = null;
            Build(_levelIndex, data);
            _rules = LevelRules.For(_def);
            UpdateHud();
            HideRingsForIntro();
            DropInRings();
        }

        /// <summary>Leaving the daily board any way other than a win counts as an unfinished attempt (once).</summary>
        void LeaveDaily()
        {
            if (!_daily) return;
            if (!_dailyEnded && _movesUsed > 0) _ = CompleteDaily(false);
            _daily = false;
            _dailyEnded = true;
        }

        async System.Threading.Tasks.Task CompleteDaily(bool won)
        {
            _dailyEnded = true;
            var port = DailyGateway.Current;
            if (port == null) return;
            try { await port.CompleteDaily(won); }
            catch (Exception e) { Debug.LogWarning("Daily completion pending: " + e.Message); }   // reward retries separately
        }

        void OnDailySolved()
        {
            _levelOver = true;
            _fx?.Cleared();
            _winStars = _rules.StarsFor(_movesUsed);
            _ = CompleteDaily(true);   // completion shows at once; the reward is retried on its own
            _winAt = Time.time + 1.0f;
        }

        void ShowDailyWin()
        {
            _wreath?.Show();
            _hud?.ShowWin("Daily puzzle", _winStars, onNext: BackToDaily, onHome: () => { LeaveDaily(); ShowHome(); }, nextLabel: "Done");
        }

        void BackToDaily()
        {
            bool won = _daily && _levelOver && _model != null && _model.IsSolved;
            ShowDaily();
            if (won) StartCoroutine(OfferOptInSoon());   // a first daily completion is a meaningful moment
        }

        void RetryDaily()
        {
            if (!_dailyEnded && _movesUsed > 0) _ = CompleteDaily(false);
            var port = DailyGateway.Current;
            if (port != null && _dailyData != null && port.StartDaily()) StartDaily(_dailyData);
            else BackToDaily();
        }

        void ShowDailyFail(bool stuck)
        {
            var offer = new LevelHud.ContinueOffer { Title = stuck ? (AnyFrozen() ? "Rusted solid" : "No way out") : "Out of moves" };
            if (stuck && _undo.Count > 0 && BoosterInventory.Count(BoosterKind.Undo) > 0)
                offer.Extra.Add(("Undo", art.iconUndo, () =>
                {
                    _wreath?.Hide();
                    _levelOver = false;
                    _stuck = false;
                    UseBooster(BoosterKind.Undo);
                }));
            _hud?.ShowOutOfMoves(offer, onRetry: RetryDaily, onHome: BackToDaily);
        }

        // ------------------------------------------------------------------ win streak (saga attempts)

        void BeginStreak()
        {
            _tries[_levelIndex] = (_tries.TryGetValue(_levelIndex, out var n) ? n : 0) + 1;
            _freeBooster = null;
            _streakAttempt = null;
            var port = DailyGateway.Current;
            if (port == null) return;
            _streakAttempt = Guid.NewGuid().ToString();
            try { _freeBooster = port.WinStart(_streakAttempt); }
            catch (Exception e) { Debug.LogWarning("Win streak start skipped: " + e.Message); _streakAttempt = null; }
            if (_freeBooster.HasValue) _streakToast = true;
        }

        bool _streakToast;

        /// <summary>After the board landed: say where the free booster came from (applied once, this attempt only).</summary>
        void AnnounceStreakBonus()
        {
            if (!_streakToast || !_freeBooster.HasValue || _hud == null) return;
            _streakToast = false;
            _hud.ShowToast($"Free {DailyUi.BoosterName(_freeBooster.Value)} from your win streak");
        }

        void EndStreak(AttemptOutcome outcome)
        {
            if (_streakAttempt == null) return;
            string id = _streakAttempt;
            _streakAttempt = null;
            _freeBooster = null;
            var result = outcome == AttemptOutcome.Win ? StreakOutcome.Win : outcome == AttemptOutcome.Lose ? StreakOutcome.Loss : StreakOutcome.Quit;
            bool firstTry = _tries.TryGetValue(_levelIndex, out var n) && n == 1;
            try { DailyGateway.Current?.WinEnd(id, result, firstTry); }
            catch (Exception e) { Debug.LogWarning("Win streak end skipped: " + e.Message); }
        }

        void StreakContinued()
        {
            if (_streakAttempt == null) return;
            try { DailyGateway.Current?.WinContinue(_streakAttempt); }
            catch (Exception e) { Debug.LogWarning("Win streak continue skipped: " + e.Message); }
        }

        /// <summary>A loss; if it ends a streak, "A fresh start" comes first and its Try again runs <paramref name="then"/>.</summary>
        void LoseThen(Action then)
        {
            var port = DailyGateway.Current;
            int wins = port?.Wins ?? 0;
            EndAttempt(AttemptOutcome.Lose);
            if (wins > 0 && port != null && _dailyFlow != null) _dailyFlow.ShowFreshStart(port.NextBonus, then);
            else then();
        }

        /// <summary>The win card's streak strip (approved win_streak_B "2/3 wins").</summary>
        void ShowStreakStrip()
        {
            var port = DailyGateway.Current;
            if (port == null || (port.Wins <= 0 && !port.BonusReady)) return;
            var below = _hud.BelowCard();   // one strip per card (BelowCard replaces any earlier one)
            if (below == null) return;
            var strip = DailyUi.Strip(_kit, below, port.BonusReady ? 3 : port.Wins, port.NextBonus);
            strip.anchoredPosition = Vector2.zero;
        }

        int BoosterShown(BoosterKind k) => BoosterInventory.Count(k) + (_freeBooster == k ? 1 : 0);

        // ------------------------------------------------------------------ home moments

        System.Collections.IEnumerator OfferOptInSoon()
        {
            yield return new WaitForSecondsRealtime(0.7f);
            if (_dailyFlow != null && FindFirstObjectByType<AccountOverlay>() == null) _dailyFlow.TryOfferOptIn();
        }

        /// <summary>Home: the daily gift (once per UTC day per session) or, after a win, the opt-in.</summary>
        System.Collections.IEnumerator HomeMomentsSoon(bool afterWin)
        {
            yield return new WaitForSecondsRealtime(0.9f);   // after Home and any starter offer have settled
            if (_home == null || !_home.Visible || _dailyFlow == null || ShopFlow.Instance != null && ShopFlow.Instance.ShopOpen) yield break;
            if (FindFirstObjectByType<AccountOverlay>() != null) yield break;   // an account / shop sheet is up
            if (_dailyFlow.TryOfferGift()) yield break;
            if (afterWin) _dailyFlow.TryOfferOptIn();
        }
    }
}
