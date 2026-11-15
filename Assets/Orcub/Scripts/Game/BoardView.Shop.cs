using System;
using Orcub.Core;
using UnityEngine;

namespace Orcub.Game
{
    /// <summary>Gold-bar sinks on the board (approved shop_v1): the gold continue and the starter offer moment.</summary>
    public sealed partial class BoardView
    {
        int _goldContinues;          // this attempt's paid continues: the next one costs ceil(60 * 1.5^n)
        bool _starterAfterWin;       // a calm moment for the starter offer: the next Home after a win

        /// <summary>
        /// review m, M3 / review n, 1: this attempt's gold-continue intent. Its idempotency key lives here, not on the
        /// sheet, so a reopened out-of-moves card (after Retry with no lives, or a lost response) replays the same key
        /// instead of minting a second charge. Reset per attempt (Build).
        /// </summary>
        SpendIntent _continueIntent = new SpendIntent();

        /// <summary>
        /// Adds the "+5 moves for N gold" row when the store is up and this attempt's level_start is known to the
        /// server. Spends exactly once per intent; a replayed key the board never applied still applies the moves
        /// once. A fresh answer without moves grants nothing (review m, L6).
        /// </summary>
        void AddGoldContinue(LevelHud.ContinueOffer offer)
        {
            var port = ShopGateway.Current;
            string attempt = ClientGateway.LastLevelStartEventId?.Invoke();
            if (port == null || (string.IsNullOrEmpty(attempt) && ShopGateway.Fake == null) || !_attemptOpen && ShopGateway.Fake == null) return;
            int level = _levelIndex, session = _session;
            offer.GoldPrice = ShopCatalog.ContinuePrice(_goldContinues);
            offer.Intent = _continueIntent;
            offer.SpendGold = key => port.SpendContinue(attempt, key);
            offer.OnGoldContinued = r =>
            {
                if (this == null || level != _levelIndex || session != _session) return;
                int moves = ContinueMovesFor(r);
                _goldContinues++;   // the server counted this continue either way: the next one is priced up
                if (moves <= 0)
                {
                    _hud?.ShowToast("Couldn’t add moves. Try again");
                    CheckOutOfMovesAgain();
                    return;
                }
                Continued(moves);
            };
        }

        /// <summary>review m, L6: the server's moves; a replayed key this board never applied gets the standard 5; else 0.</summary>
        public static int ContinueMovesFor(SpendResult r) => r.ExtraMoves > 0 ? r.ExtraMoves : r.Replayed ? ShopCatalog.ContinueMoves : 0;

        /// <summary>Home after a win: offer the server's starter pack once (its own cooldown applies).</summary>
        void MaybeOfferStarter()
        {
            if (!_starterAfterWin) return;
            _starterAfterWin = false;
            StartCoroutine(OfferStarterSoon());
        }

        System.Collections.IEnumerator OfferStarterSoon()
        {
            yield return new WaitForSecondsRealtime(0.6f);   // after Home has settled in
            if (_home != null && _home.Visible && (_hud == null || !_hud.PopupOpen)) ShopFlow.TryOfferStarter();
        }
    }
}
