Review b supersedes the integration details below: see [REVIEW_B.md](REVIEW_B.md) for wallet authority, consent, deletion recovery, asynchronous persistence and required Game-owner hooks.

# Orcub connected client

`ClientServices` boots before the scene, without UI. `BaseUrl` is the single production API setting. No admin token ships in the client. Gameplay publishes SDK-free events through `ClientGateway`.

## Packages

Firebase App, Analytics, Auth, Crashlytics and Remote Config 13.17.1 are Google's official UPM tgz distributions. Firebase 14.1.0 is current but 14.x requires Swift Package Manager on iOS, so 13.17.1 is the latest maintained CocoaPods-compatible version for this export pipeline. The App tgz has exactly one packaging adjustment: package.json's old `com.google.external-dependency-manager:1.2.186` dependency is replaced by `com.unity.external-dependency-manager:2.1.1`. No SDK code/binaries are changed. Other tgz files are unmodified. Do not install a second EDM copy. Google Mobile Ads remains 11.7.0. Newtonsoft JSON 3.2.2 provides the backend's dictionary-shaped saves.

Sources: https://firebase.google.com/support/release-notes/unity and https://developers.google.com/unity/archive .

## Config and telemetry

Precedence for each config object is activated Firebase Remote Config remote value, then last good backend `/v1/config`, then bundled `Tuning`, `PlayerModel` and `AdsConfig`. RC keys are `tuning`, `skill_model`, `ad_policy` (JSON strings), `version`, and `experiments`. Empty RC defaults preserve backend values. ETag is cached. Adaptive config applies only before BeginAttempt; a frozen attempt never changes. Invalid adaptive overrides leave its current defaults intact. Ad policy overrides interval and daily caps; UTC daily counters persist locally.

Install credentials are an iOS Keychain generic-password item, AfterFirstUnlockThisDeviceOnly. Editor/non-iOS use PlayerPrefs fallback. Install UUID is freshly generated per registration; a lost first response is retried with a fresh UUID. First-party install metadata contains no advertising identifier. The queue retains 2,000 events / 4 MiB, drops oldest first, writes atomically, sends at most 100 / 1 MiB per gzip batch and retains IDs on retry. Oversize single events are discarded. Pause persists synchronously and starts a best-effort asynchronous flush; iOS may suspend before it completes, so disk replay remains necessary. Backoff caps at 256 seconds plus jitter; 429 honours Retry-After seconds. Schema 400/422 drops the rejected batch and warns. Gameplay never awaits any networking.

`ad_paid` is generated exclusively by OnAdPaid. The backend expressly rejects ad-unit IDs, so unit and mediation network details are included only in Firebase's standard `ad_impression` event with value in currency units and currency. Editor ads are simulated; they never fabricate paid callbacks.

## Account API for later UI

Use `ClientServices.Instance.Account`:

- `User`, `IsAnonymous`; `Changed` event; `Failed(string code)` event.
- `Task SignInWithGoogle()` invokes native iOS Google Sign-In and links the anonymous Firebase user. Existing credentials use sign-in plus verified previous anonymous ID token on `/v1/link`.
- `Task SignOut()` saves, signs out and resets to a fresh guest install.
- `Task DeleteAccount()` deletes the first-party backend, then Firebase Auth user, then local state. Firebase may require recent authentication; show reauthentication UI when UI is approved.
- `Task Save()` / `RequestSave()` back up progress, per-level stars, inventory, workshop and StarBank spend, lives, preferences, and skill samples.
- `Task Link()` refreshes canonical player ownership and downloads/merges the cloud snapshot. Startup performs anonymous Firebase auth and link automatically.

Save writes use the revision last read. On 409, maximum progress is merged into server mutable state; old consumables are never replayed at a new revision. On an ambiguous lost write acknowledgement, GET resolves current state. This is backup, not a server-authoritative economy. Concurrent requests serialize; callers should invoke Save after future UI economy operations.

Account UI (approved account_v1/v2) reaches this service only through the SDK-free `Orcub.Game.AccountGateway`, installed by `AccountBridge`. `ClientServices` reports real boot progress in `AccountGateway.Boot` for the loader. When `/v1/save` holds progress this phone lacks while this phone has its own, `Link()` keeps it as `PendingCloud` (saves pause) and the cloud-restore sheet resolves it via `ResolveConflict(keepCloud)`; a phone with no progress takes the cloud quietly and home shows Welcome back. Sign in with Apple stays hidden behind `AccountGateway.ShowAppleSignIn`.

TODO before store release: Sign in with Apple and token revocation once the paid Apple programme is available. Enable Anonymous and Google providers in Firebase Console. Anonymous auth was verified live in the editor. The supplied plist has IS_ANALYTICS_ENABLED=false: the export explicitly enables collection, but the owner must verify the project is connected to Google Analytics and inspect device DebugView before claiming delivery. Complete external Firebase Analytics / Google identity deletion workflow; DELETE /v1/account only covers first-party records. Test Google linking, existing-account merge, recent-login deletion and offline saves on a signed device.

## iOS

Assets/GoogleService-Info.plist is the SDK configuration. The build postprocessor adds its REVERSED_CLIENT_ID URL scheme and Security.framework. The native AppController forwards Google callback URLs. EDM adds GoogleSignIn 9.x and Firebase 12.19.3 pods. Normal BuildIOS always uses Google test IDs. BuildIOSRelease injects ORCUB_RELEASE only for that player build and sets the live app ID while exporting, then restores editor settings. Do not use the release menu for the owner's development phone. Add AdMob hashed test device IDs to AdsConfig.testDeviceIds; device SDK logs provide the hash. No IDFA is collected by our telemetry.

Crashlytics is enabled after dependency checks; dSYM generation uses dwarf-with-dsym. Firebase's Crashlytics editor postprocessor adds upload support. If symbols remain missing, run the generated Pods/FirebaseCrashlytics/upload-symbols tool with `-gsp Assets/GoogleService-Info.plist -p ios <path-to-dSYMs>` on an authorised build machine. Verify symbolication with a deliberate test crash on a non-production device; compilation is not crash-delivery proof.

## Integration verification (15 November 2026)

Unity MCP compiled all new runtime assemblies. Final EditMode XML: 73 passed, 0 failed, 0 skipped, 5.60 seconds; six new tests cover durable event IDs, exact gzip envelope, corrupt/oversize queue input, conflict merging without consumable restoration and initial-save merging. MCP's last job status did not receive completion, so the final result was checked directly in Unity's TestResults.xml. Game.unity was reopened afterwards.

The editor smoke registered player 8b17e27a-01bc-4ad1-b587-3777178f2758. The authenticated account export returned exactly one install, one save and four retained events at inspection: session_start, level_start for saga 3 and 4, and a level_end win for saga 4. The admin funnel independently returned level 4 starts=1, ends=1, wins=1, median_moves=1. A counted press/release was invoked through the board path before the existing autosolver finished the board; this is editor integration proof, not phone touch or ad presentation proof. Firebase anonymous auth and backend linking worked. The telemetry queue drained without schema errors. No admin or identity tokens were printed or added to the repository.

The first export hit ENOSPC; only Orcub's regenerable Builds/dd cache and this task's temporary SDK extraction were removed. The retry exported 1,486 MB with zero Unity build errors. CocoaPods resolved Firebase umbrella 12.19.3 (Analytics/Auth/Crashlytics/RC component pods 12.19.0), GoogleSignIn 9.2.0 and Google-Mobile-Ads-SDK 13.12.0. The generated plist has the official Google test app ID, callback scheme and explicit Analytics collection flag. No phone install was performed.

## Files

Added: Net/ClientServices.cs, EventQueue.cs, InstallStore.cs, AccountService.cs, SaveSnapshot.cs, Orcub.Net.asmdef and this README; Game/ClientGateway.cs; Editor/Net/ClientDependencies.xml; Editor/OrcubIdentityBuild.cs; Tests/ClientTests.cs; Assets/Plugins/iOS/OrcubIdentity.mm; Assets/GoogleService-Info.plist; five Firebase UPM tgz files in Packages, plus Unity metadata.

Changed: Game/BoardView.cs and GameSettings.cs; Ads/AdService.cs, AdPolicy.cs, AdsConfig.cs and Resources/AdsConfig.json; Editor/OrcubBuild.cs and OrcubAdsBuild.cs; Tests/AdPolicyTests.cs and Orcub.Tests.asmdef; Packages/manifest.json and packages-lock.json. Layouts, scenes and prototype art are excluded. Existing ProjectSettings edits and concurrent backend work are excluded from this commit.

Signed workspace verification ended **BUILD SUCCEEDED** with `-configuration Debug -sdk iphoneos -destination generic/platform=iOS -allowProvisioningUpdates DEVELOPMENT_TEAM=3HB3HKUZ4Q CODE_SIGN_STYLE=Automatic COMPILER_INDEX_STORE_ENABLE=NO LD_GENERATE_MAP_FILE=NO`. The first Xcode pass was interrupted while emitting a slow 77 MiB diagnostic link map; the incremental retry disabled only that map and retained dSYM generation. The framework dSYM is 554 MiB. Firebase's CocoaPods Crashlytics run scripts executed, but Console delivery/symbolication remains unverified. During final packaging, the regenerable Unity Library/Bee/artifacts/iOS cache was also removed to recover space; native build products and source were retained.

App: Builds/dd/Build/Products/Debug-iphoneos/Orcub.app. Full final log: Builds/xcodebuild-integration-final.log. EditMode result: Builds/EditMode-integration.xml. These generated outputs are excluded from Git. Only the test-ID build above was generated; release/live-ad builds are reserved for store distribution.
