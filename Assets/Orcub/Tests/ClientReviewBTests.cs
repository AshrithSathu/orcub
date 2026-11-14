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
        [Test] public async Task UnauthorizedInstallRenewsAndRetriesOnce()
        {
            int requests=0,renewals=0,disposed=0;
            int result=await InstallRetry.Run(()=>Task.FromResult(++requests==1 ? 401 : 200),r=>r,r=>disposed++,()=>{renewals++;return Task.CompletedTask;});
            Assert.AreEqual(200,result); Assert.AreEqual(2,requests); Assert.AreEqual(1,renewals); Assert.AreEqual(1,disposed);
        }
        [Test] public async Task DeletePreparationProceedsWhenCloudSaveFails()
        {
            bool flushed=false;
            await AccountDeletion.Prepare(()=>Task.FromException(new Exception("conflict")),()=>{flushed=true;return Task.CompletedTask;});
            Assert.True(flushed);
        }
        [Test] public void NotificationExportValidationPassesWithoutExport()
        {
            var type=Type.GetType("Orcub.EditorTools.OrcubNotificationBuild, Orcub.Editor",true);
            Assert.DoesNotThrow(()=>type.GetMethod("ValidateSettings").Invoke(null,null));
        }
        [Test] public void InfiniteStarsRoundTrip()
        {
            var old=SaveSnapshot.Capture(); int oldStar=UnityEngine.PlayerPrefs.GetInt("orcub.stars.219",0);
            try {
                UnityEngine.PlayerPrefs.SetInt("orcub.highest",220); UnityEngine.PlayerPrefs.SetInt("orcub.stars.219",3);
                var snapshot=SaveSnapshot.Capture(); Assert.AreEqual(3,(int)snapshot["stars"]["219"]);
                UnityEngine.PlayerPrefs.SetInt("orcub.stars.219",0); SaveSnapshot.Apply(snapshot);
                Assert.AreEqual(3,UnityEngine.PlayerPrefs.GetInt("orcub.stars.219"));
            } finally { SaveSnapshot.Apply(old); UnityEngine.PlayerPrefs.SetInt("orcub.stars.219",oldStar); }
        }
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
        [Test] public async Task FinalSaveIsForcedAndAwaitedBeforeDrainOrWipe()
        {
            var saved=new TaskCompletionSource<bool>(); bool forced=false,drained=false,wiped=false;
            async Task Finish() { await FinalAccountSave.Run(force=> { forced=force; return saved.Task; },()=> { drained=true; return Task.CompletedTask; }); wiped=true; }
            var finishing=Finish(); Assert.True(forced); Assert.False(drained); Assert.False(wiped);
            saved.SetResult(true); await finishing; Assert.True(drained); Assert.True(wiped);
            drained=false;
            Assert.ThrowsAsync<Exception>(()=>FinalAccountSave.Run(force=>Task.FromException(new Exception("offline")),()=> { drained=true; return Task.CompletedTask; }));
            Assert.False(drained);
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
                Assert.True(AnalyticsConsent.BackendAllowed("error")); Assert.True(AnalyticsConsent.BackendAllowed("level_start")); Assert.True(AnalyticsConsent.BackendAllowed("level_end"));
                foreach (var kind in new[] { "session_start", "ad_offer", "iap", "daily_gift_claim" }) Assert.False(AnalyticsConsent.BackendAllowed(kind));
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
