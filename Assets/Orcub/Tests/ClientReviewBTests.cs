using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Orcub.Net;
using Orcub.Core;
namespace Orcub.Tests
{
    public class ClientReviewBTests
    {
        [TestCase(false)] [TestCase(true)]
        public async Task DeletionReauthenticatesBeforeBackendAndAlwaysResetsAfterBackendSuccess(bool identityFails)
        {
            var order = new List<string>();
            await AccountDeletion.Run(() => { order.Add("reauth"); return Task.CompletedTask; },
                () => { order.Add("backend"); return Task.CompletedTask; },
                () => { order.Add("firebase"); return identityFails ? Task.FromException(new Exception()) : Task.CompletedTask; },
                () => order.Add("queue"), () => { order.Add("wipe_signout_register"); return Task.CompletedTask; });
            CollectionAssert.AreEqual(identityFails ? new[] { "reauth", "backend", "firebase", "queue", "wipe_signout_register" } : new[] { "reauth", "backend", "firebase", "wipe_signout_register" }, order);
        }
        [Test] public void FailedReauthenticationNeverDeletesBackend()
        {
            bool deleted = false;
            Assert.ThrowsAsync<Exception>(() => AccountDeletion.Run(() => Task.FromException(new Exception()), () => { deleted = true; return Task.CompletedTask; }, () => Task.CompletedTask, () => {}, () => Task.CompletedTask));
            Assert.False(deleted);
        }
        [Test] public void PreconsentOnlyBoundedOperationalErrorsAreAllowed()
        {
            try
            {
                AnalyticsConsent.Resolve(false);
                Assert.True(AnalyticsConsent.BackendAllowed("error"));
                foreach (var kind in new[] { "session_start", "level_start", "ad_offer", "iap", "daily_gift_claim" }) Assert.False(AnalyticsConsent.BackendAllowed(kind));
                AnalyticsConsent.Resolve(true); Assert.True(AnalyticsConsent.BackendAllowed("level_start"));
            }
            finally { AnalyticsConsent.Resolve(false); }
        }
        [Test] public void MemoKeyDistinguishesSpringAgeAndClayPasses()
        {
            var def = new LevelDef(); def.Rings.Add(new RingDef(0,0,1,0)); def.Rings.Add(new RingDef(4,0,1,1));
            def.Links.Add(new LinkDef(0,1)); var m = new PuzzleModel(def);
            var link = m.Links[0]; link.SpringMoves = 3; link.Clay = true;
            var initial = MechanicSolver.MemoKey(m); link.ReleasedAt++;
            Assert.AreNotEqual(initial, MechanicSolver.MemoKey(m)); link.ReleasedAt--;
            Assert.AreEqual(initial, MechanicSolver.MemoKey(m)); link.Passes++;
            Assert.AreNotEqual(initial, MechanicSolver.MemoKey(m));
        }
    }
}
