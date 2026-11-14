# Orcub ads

Installed Google's official `com.google.ads.mobile` 11.7.0 from the OpenUPM scoped registry (`com.google`). Its dependency `com.unity.external-dependency-manager` 2.1.1 comes from Unity's registry. Both are pinned in Packages/manifest.json and packages-lock.json; no plugin binaries need copying into Assets.

## Build IDs

Edit only `Assets/Orcub/Scripts/Ads/Resources/AdsConfig.json`:

- `iosAppId`: AdMob iOS app ID (contains `~`).
- `rewardedUnitId`: iOS rewarded unit ID.
- `interstitialUnitId`: iOS interstitial unit ID.
- `trackingUsageDescription`: ATT purpose string.

The default fields remain Google's official iOS test IDs. The `liveAppId`, `liveRewardedUnitId` and `liveInterstitialUnitId` fields contain the real Orcub IDs, selected only by the build-scoped `ORCUB_RELEASE` define in Orcub/Build iOS Release (live ads). Normal BuildIOS and development builds keep test IDs. Never use the release menu for the owner's phone. Populate `testDeviceIds` with AdMob hashed test device IDs when testing the native SDK; these are passed to MobileAds.SetRequestConfiguration. The build preprocessor copies the app ID and purpose string into Google's settings asset before its plist postprocessor runs. Google's plugin adds its bundled SKAdNetworkItems list automatically. Do not edit generated Info.plist or GoogleMobileAdsSettings to replace IDs.

## Policy

The same JSON owns the kill switch (`adsEnabled`) and all policy numbers: level 8 minimum, wins only, 150 seconds and 3 completed levels between interstitials, 6 per session, and a 60-second exclusion after rewarded ads. The first interstitial also requires 150 seconds in-session and 3 wins. Replay wins count as completed levels. Timing uses monotonic real time. Win/session counters are session-only; interstitial/rewarded UTC daily caps persist across sessions. Backend config and activated Remote Config can override the interval and daily caps. No ad state is read by puzzle generation or difficulty logic.

## Consent and rewards

On the first completed level, ATT requests permission only while iOS status is undetermined, then UMP updates consent and shows a form if required. On later launches UMP refreshes consent again. If ATT is still undetermined after an interrupted prompt, privacy waits for another completed level rather than requesting ATT at launch. Play never waits for these operations. No SDK initialization or ad request happens until UMP CanRequestAds is true. Network/consent failures leave ads unavailable without granting anything. Once a required UMP privacy entry point exists, the existing Settings card's Done control becomes Privacy choices and opens the native UMP form; no new visual component is added.

Editor and non-iOS play use a delayed simulation, never device network failure fallback. On iOS, only the SDK earned-reward callback grants a reward, at most once. Close/failure callbacks restore the existing card. Interstitials run only on win-card Next, and next-level loading waits for dismissal. Load failures retry after 30 seconds; consumed ads reload automatically.

`AdsGateway` is the SDK-free interface in Orcub.Runtime. Orcub.Ads references Runtime and Google's precompiled assemblies, preventing a circular assembly reference from BoardView. The native ATT bridge links AppTrackingTransparency.

## Build and verification

Use Orcub/Build iOS (Xcode project), which calls OrcubBuild.BuildIOS and exports Builds/iOS-v2. EDM is configured for CocoaPods workspace integration and runs pod install on export. OrcubAdsBuild adds a post_install hook at callback order 45 (between EDM generation at 40 and installation at 50) to set every pod target to iOS 15, as required by Xcode 27. Wait for the full Unity build to finish (including lib_burst_generated.cpp) before xcodebuild. Build the Unity-iPhone workspace scheme with DEVELOPMENT_TEAM=3HB3HKUZ4Q and -allowProvisioningUpdates.

Run all EditMode tests through MCP run_tests, including AdPolicyTests. Device ad presentation, ATT behaviour after a fresh install, UMP region-specific messages, and reward callbacks still require a phone run. Publish the appropriate UMP messages in the real AdMob account when swapping IDs. Disable the account's UMP IDFA explainer if using this native ATT prompt, so its timing remains owned here.

Sources: https://developers.google.com/admob/unity/quick-start, https://developers.google.com/admob/unity/privacy, https://developers.google.com/admob/unity/test-ads.

Verified 2026-11-15: MCP EditMode run 67/67 passed (including 14 AdPolicy tests and all 44 original tests). OrcubBuild.BuildIOS exported successfully; EDM installed both pods automatically. The Xcode 27 pod deployment-target fix was applied to that export with the same checked-in hook and pod install rerun. Signed Debug workspace build with team 3HB3HKUZ4Q ended BUILD SUCCEEDED. No phone installation or on-device ad presentation was performed. Xcode output: Builds/dd/Build/Products/Debug-iphoneos/Orcub.app.

Client integration verification: all 73 EditMode tests passed, and the signed Debug test-ID workspace build ended BUILD SUCCEEDED. See ../Net/README.md for Firebase, telemetry, config, account/save APIs and detailed evidence.
