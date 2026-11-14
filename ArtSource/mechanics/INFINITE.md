# Infinite saga implementation

Indices in this API are zero based: displayed level 201 is index 200. The shipped Resources/Orcub/levels.json is untouched. Levels 1–200 and all five curated choices remain authoritative; the pack builder now preserves all 200 rather than only chapter one.

## Content and identity

Algorithm v1 uses PCG-XSH-RR 64/32 with explicitly unchecked integer overflow. Seeds include index, variant and algorithm version. Construction uses a bounded, integer-quantised spine/fork family, without System.Random or random rejection searches. The two fork leaves support the introduced mechanics without dependency cycles. Linked/key combinations are excluded because a locked gear pair would hide its own key. Chapter emphasis cycles ratchet, linked, colour clamp, key/padlock, rust/oil, spring and clay; ordinary levels mix a second mechanic, every fifth generated chapter spotlights one. No repeat tutorial popups. Every twentieth level is a boss. Chapter openings and every fifth level are breathers following the preceding peak. Ring counts rise by one per twelve chapters, capped at ten; five variants vary seeds and complexity.

Resources/Orcub/tuning.json supplies Tuning.Infinite. Defaults: BaseRings=6, MaxRings=10, ChaptersPerRing=12, SpotlightEvery=5, GenerationBudgetMs=80, CacheEntries=100, PrefetchCount=5. Apply remote config between attempts with `LevelLibrary.Configure(tuning)`. Parent hook in Adaptive.Configure, after accepting nextTuning: `LevelLibrary.Configure(nextTuning);`. Adaptive.cs and PlayerSkill.cs were deliberately not edited by this implementation.

Changing content parameters necessarily changes content for unseen levels. To distinguish those configurations for cross-player comparisons, LevelData.gen_version contains `algorithm-baseRings-maxRings-chaptersPerRing-spotlightEvery`, e.g. `1-6-10-12-5`. An equal (index, gen_version, variant) reproduces the same board. Do not compare different content versions as identical leaderboard boards. Schedule globally consistent config rollouts if unseen indices must match every player. Existing indices keep their frozen recipe, including after eviction/restart. Operational budgets/cache sizes do not affect content identity. Mono/IL2CPP device byte equality still requires device validation; thread equality in the editor is a separate check.

## Worker and persistence

A serial TaskScheduler.Default worker performs generation, solver validation and disk IO. Generation uses only managed model/math operations; Resources and Application paths are captured on the main thread. JsonUtility serialises plain objects on the worker. Each generated board is exactly searched. An 80 ms search budget falls back to a linear full-sweep solution witness of the same safe board; timing never selects different geometry. This intentionally uses a bounded safe configuration from the start rather than attempting an unbounded random-board search. The solver minimum has a structural certificate: n-1 moves, or n-2 with a linked fork. Existing LevelRules supplies at least two safety moves and ordered reachable star thresholds.

`persistentDataPath/levels/v1/{index}.json` contains all five choices. Board JSON is bounded to 100 entries; a small `{index}.curve.json` recipe is retained permanently to reproduce seen/prefetched boards after eviction. This recipe retention is intentional: fully bounded storage plus arbitrary remote curve changes plus permanent seen-board identity cannot all be guaranteed without some retained history. The hot memory window is bounded. Writes use a temporary file and rename. Corrupt board JSON is rebuilt against the frozen recipe. Unwritable storage records LastError and retains the session recipe; preserving identity across restart requires working persistent storage. Export/restore these recipes with cloud saves before claiming identity continuity across reinstall/devices.

## API and game integration

- `LevelLibrary.IsInfinite`: true.
- `CuratedCount` and legacy `Count`: 200, **not a progression limit**.
- `Get(index, variant)` and `GetVariants(index)`: curated or generated choices, variants 0–4. A cold synchronous call waits on the worker. Never call it cold on the UI thread.
- `ChapterOf(index)`: zero-based chapter, in blocks of twenty.
- `ChapterInfo(chapter)`: Name, Mechanics, FirstIndex, LastIndex, Curated, IsBoss(index). Generated names come from adjective/place word lists. Mechanics names denote chapter emphasis.
- `Prefetch(index)`: nonblocking queue for current plus next five indices. Call at startup/frontier changes and after load. Startup also warms max(195, CurrentLevel).
- `PrepareAsync(index)`: await all five choices before Adaptive.BeginAttempt; `TryGetVariants(index, out choices)` polls readiness without blocking. Invoke the API first on the main thread to initialise paths/resources.

Game/loader work belongs to Opus. Before BeginAttempt, show the existing loader and await PrepareAsync (or yield frames while TryGetVariants is false). Then BeginAttempt selects from that fixed ready array, Get retrieves the selection, and Prefetch(index+1) warms the next window. Cancel stale screen requests before enabling input. **The hard one-second loader ceiling with a ready cached fallback is still a game integration gap**: no timeout-based alternate board is supplied, because that would change identity between fast and slow devices. Normal bounded generation is much faster, but stalled storage/worker scheduling can exceed one second. The legacy synchronous API is compatible, not safe for a cold UI call.

Exact existing finite UI assumptions (line numbers may move with Opus's work):

| File | Lines | Required change |
|---|---|---|
| Scripts/Game/TopBar.cs | 84–90 | Chapters.Count becomes a visible window based on frontier, not library Count; Last=First+19; Frontier=nonnegative HighestUnlocked; generated names use ChapterInfo. |
| Scripts/Game/TopBar.cs | 94–98 | Stars may still scan twenty indices for one chapter; never scan all infinite chapters. |
| Scripts/Game/MapScreen.cs | 54, 117–124 | Clamp chapter to visible window; show twenty slots; remove “of total chapters”; keep per-chapter stars. |
| Scripts/Game/ChaptersScreen.cs | 85–90, 123–131 | Virtualise/window chapter cards around frontier; remove finite total levels/star denominator and all-chapter star scan. |
| Scripts/Game/HomeScreen.cs | 107 onward | Use uncapped frontier from Chapters; remove any implied terminal chapter/level. |
| Scripts/Game/BoardView.cs | 163, 219, 277 onward | Remove frontier/navigation upper clamps and level-index modulo by Count; asynchronously prepare choices before adaptive selection/loading. |
| Scripts/Game/LoaderScreen.cs | 64 | Count is now a constant; load Pack explicitly and prefetch current/frontier. |

## Telemetry handoff for Net

Add `gen_version` and `curated` to level start/end/fail/retry/continue/booster/hint events. Curated means index < 200; use gen_version="curated-v3" for those unchanged pack boards and data.gen_version for generated ones. Keep saga index, displayed level id, variant, seed and tuning version for reproduction. Do not infer generator version from the current remote config: use the actual loaded board. No Net or event uploader files were edited here.

## Validation evidence

The extended all-level sweep includes all 1,000 curated choices plus 1,290 generated choices: indices 200,207,…,1999 (displayed levels 201–2000), all five variants. Generated rows additionally gate geometry/frame/HUD checks, bot clearance, budget/stars and repeated JSON equality. InfiniteLevelTests checks thread-context determinism, bosses/breathers/soft cap, cache eviction/restart with changed tuning, nonblocking prefetch, curated source equality and timing. Full test status and measured timings are recorded below after execution. Real iPhone timing, portrait safe areas and IL2CPP equality are not editor proof.

Net save handoff: Scripts/Net/SaveSnapshot.cs:43 and :67 also loop over Count. Capture/replay actual played indices through HighestUnlocked and include the pinned generated recipes in account saves. They currently omit stars after level 200; the Net owner must update these loops. Core/Meta.cs StarBank now counts earned stars through the actual unlocked frontier. DifficultySimulator intentionally samples the curated calibration corpus; existing curated QA/test loops remain finite on purpose.

Standalone validation on this machine: Unity bundled Roslyn compiled the updated runtime, QA editor and QA tests without errors. A standalone Unity-bundled Mono harness called the actual LevelQA.Inspect checks (solver, budget/stars, bot, model field transfer, geometry and 9:19.5 HUD fit): 1,000 curated choices and 1,290 generated choices, zero hard or advisory failures. Two separate generations in main/worker contexts matched all field signatures for the generated sample. The shipped JSON is byte-identical to git HEAD. This is not a Unity EditMode runner result or JSON-thread test result. Latest generation timing: mean 0.585 ms, p95 0.734 ms, cold/JIT maximum 301.910 ms across 1,290 boards. A rough 3x slower-device estimate is 2.2 ms p95 per board, excluding disk IO; iPhone measurements remain required.

Unity MCP refresh, console, EditMode and scene-reopen attempts repeatedly returned ping-not-answered/session-disconnected/TimeoutError. Full EditMode results, cache/JsonUtility runtime tests and confirmed Game.unity restoration are therefore unverified. No play mode was entered.

A final whole-test-assembly standalone compile encountered stale concurrent Net/Game assembly references (new AccountFlow tests versus cached SaveSnapshot/SettingsScreen assemblies); these are not reported as passing tests. The runtime source compile was repeated successfully with the concurrently added Game sources included.
