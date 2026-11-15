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
        /// Adds the "+5 moves for N gold" row when the store is up and this attempt's level_start is known to the
        /// server. Spends exactly once per tap (idempotency key kept across a network retry); a replayed key
        /// (lost response) still applies the 5 moves once locally, because this sheet never applied them.
        /// </summary>
        void AddGoldContinue(LevelHud.ContinueOffer offer)
        {
            var port = ShopGateway.Current;
            string attempt = ClientGateway.LastLevelStartEventId?.Invoke();
            if (port == null || (string.IsNullOrEmpty(attempt) && ShopGateway.Fake == null) || !_attemptOpen && ShopGateway.Fake == null) return;
            int level = _levelIndex, session = _session;
            offer.GoldPrice = ShopCatalog.ContinuePrice(_goldContinues);
            offer.SpendGold = key => port.SpendContinue(attempt, key);
            offer.OnGoldContinued = moves =>
            {
                if (this == null || level != _levelIndex || session != _session) return;
                _goldContinues++;
                Continued(moves > 0 ? moves : ContinueMoves);
            };
        }

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
