using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Orcub.Game;
using Orcub.Net;

namespace Orcub.Tests
{
    /// <summary>Cloud-restore decision rules and account display helpers.</summary>
    public class AccountFlowTests
    {
        [TestCase(Firebase.Auth.AuthError.UserDisabled)]
        [TestCase(Firebase.Auth.AuthError.UserTokenExpired)]
        public void OtherAuthFailuresDoNotConfirmDeletion(Firebase.Auth.AuthError code)
        {
            Assert.ThrowsAsync<Firebase.FirebaseException>(async ()=> {
                await AccountDeletion.ConfirmBackendDeletion(()=>throw new Firebase.FirebaseException((int)code,"auth failure"));
            });
        }
        [Test] public async System.Threading.Tasks.Task UserNotFoundDuringDeletionTokenRefreshConfirmsWipe()
        {
            bool wiped=false, identityDeleted=false;
            await AccountDeletion.Run(()=>System.Threading.Tasks.Task.CompletedTask,
                async ()=> { identityDeleted=await AccountDeletion.ConfirmBackendDeletion(
                    ()=>throw new Firebase.FirebaseException((int)Firebase.Auth.AuthError.UserNotFound,"missing")); },
                ()=> { Assert.IsTrue(identityDeleted); return System.Threading.Tasks.Task.CompletedTask; },
                ()=>Assert.Fail("identity cleanup should not fail"),
                ()=> { wiped=true; return System.Threading.Tasks.Task.CompletedTask; });
            Assert.IsTrue(wiped);
        }
        [Test] public async System.Threading.Tasks.Task LostDeleteResponseRelaunchResetsToGuest()
        {
            bool backendDeleted=false, wiped=false;
            string localPlayer="old-player";
            try {
                await AccountDeletion.Run(()=>System.Threading.Tasks.Task.CompletedTask,
                    ()=> { backendDeleted=true; throw new System.IO.IOException("lost DELETE response"); },
                    ()=>System.Threading.Tasks.Task.CompletedTask,()=>{},()=> { wiped=true; return System.Threading.Tasks.Task.CompletedTask; });
                Assert.Fail("lost response must leave a resumable intent");
            } catch(System.IO.IOException) { }
            Assert.IsFalse(wiped);
            Assert.IsTrue(await AccountDeletion.Resume(true,false,()=>System.Threading.Tasks.Task.FromResult(backendDeleted),()=> {
                wiped=true; localPlayer="fresh-guest"; return System.Threading.Tasks.Task.CompletedTask;
            }));
            Assert.IsTrue(wiped); Assert.AreEqual("fresh-guest",localPlayer);
        }

        [Test] public async System.Threading.Tasks.Task RelaunchWithoutIntentNeverProbesOrWipes()
        {
            bool probed=false, wiped=false;
            Assert.IsFalse(await AccountDeletion.Resume(false,false,
                ()=> { probed=true; return System.Threading.Tasks.Task.FromResult(true); },
                ()=> { wiped=true; return System.Threading.Tasks.Task.CompletedTask; }));
            Assert.IsFalse(probed); Assert.IsFalse(wiped);
        }
        [Test] public async System.Threading.Tasks.Task OfflineDeletionProbeKeepsIntentWithoutFailingBoot()
        {
            Assert.IsFalse(await AccountDeletion.Resume(true,false,
                ()=>throw new System.Exception("offline"),()=>throw new System.Exception("unexpected wipe")));
        }
        [Test] public async System.Threading.Tasks.Task BadLevelEventDoesNotBlockThreeGoodEvents()
        {
            var path=System.IO.Path.Combine(System.IO.Path.GetTempPath(),System.Guid.NewGuid()+".json");
            var queue=new EventQueue(path);
            try {
                for(int i=0;i<4;i++) queue.Add("level_end",new {LevelIndex=i});
                int sent=0, dropped=0;
                await queue.IsolateRejected(queue.Batch(), item=>System.Threading.Tasks.Task.FromResult(++sent==1?400L:200L),
                    (kind,code)=> { Assert.AreEqual("level_end",kind); Assert.AreEqual(400,code); dropped++; });
                Assert.AreEqual(4,sent); Assert.AreEqual(1,dropped); Assert.AreEqual(0,queue.Count);
            } finally { await queue.Stop(); if(System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        }
        static JObject Save(int highest, params (int level, int stars)[] stars)
        {
            var s = new JObject();
            foreach (var (level, n) in stars) s[level.ToString()] = n;
            return new JObject { ["highest_level"] = highest, ["stars"] = s };
        }

        [Test] public void FreshPhoneTakesCloudQuietly()
            => Assert.AreEqual(SaveDiff.Fresh, SaveSnapshot.Compare(Save(0), Save(41, (0, 3), (1, 2))));

        [Test] public void UnsavedLocalProgressIsNotAConflict()
            => Assert.AreEqual(SaveDiff.LocalAhead, SaveSnapshot.Compare(Save(12, (0, 3), (1, 3)), Save(11, (0, 3), (1, 2))));

        [Test] public void IdenticalSavesAreSame()
            => Assert.AreEqual(SaveDiff.Same, SaveSnapshot.Compare(Save(0), Save(0)));

        [Test] public void CloudAheadOfPlayedPhoneAsksThePlayer()
            => Assert.AreEqual(SaveDiff.Conflict, SaveSnapshot.Compare(Save(11, (0, 3)), Save(41, (0, 3), (5, 2))));

        [Test] public void DivergedStarsAskThePlayer()
            => Assert.AreEqual(SaveDiff.Conflict, SaveSnapshot.Compare(Save(5, (0, 1), (1, 3)), Save(5, (0, 3), (1, 1), (2, 2))));

        [Test] public void SummaryIsFrontierLevelAndStarTotal()
        {
            var s = SaveSnapshot.Summary(Save(41, (0, 3), (1, 2)));
            Assert.AreEqual(42, s.Level);
            Assert.AreEqual(5, s.Stars);
        }

        [Test] public void FurtherAheadComparesLevelThenStars()
        {
            Assert.IsTrue(new SaveSummary(42, 10).AheadOf(new SaveSummary(12, 30)));
            Assert.IsTrue(new SaveSummary(12, 31).AheadOf(new SaveSummary(12, 30)));
            Assert.IsFalse(new SaveSummary(12, 30).AheadOf(new SaveSummary(12, 30)));
        }

        [Test] public void EmailIsMasked()
        {
            Assert.AreEqual("a••••@gmail.com", SettingsScreen.MaskEmail("alex@gmail.com"));
            Assert.AreEqual("Google account", SettingsScreen.MaskEmail(null));
        }
    }
}
