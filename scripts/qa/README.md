# Orcub QA

Run `scripts/qa/run_all.sh` from any directory. It checks Rust formatting and clippy, then tests against the explicitly selected disposable database. It never deploys.

For PostgreSQL, follow `backend/README.md`: start only the local Compose `db` service (`cd backend && docker compose up -d db`), then export:

```sh
export DATABASE_URL=postgres://orcub:local-development-only@127.0.0.1:55432/orcub
export ORCUB_QA_DISPOSABLE_DB=1
./scripts/qa/run_all.sh
```

The PostgreSQL role needs CREATEDB: `sqlx::test` creates/drops disposable databases and runs migrations. Do not use a production URL. The script sets `SQLX_OFFLINE=true`; this enables compilation without database metadata but does not replace live SQL tests. CI uses a fresh PostgreSQL 17 service with a test-only superuser, a Cargo cache, and no deployment step. Deployment remains manual (`railway up`).

## Unity locally

Use the editor version in `ProjectSettings/ProjectVersion.txt` (currently 6000.6.9f1). Wait for compilation, then choose **Orcub > QA > Verify All Levels**. Alternatively open **Window > General > Test Runner**, choose **EditMode**, filter `QA_AllLevelsSolvable`, and run. No Play Mode is needed. Reopen `Assets/Scenes/Game.unity` afterwards in the shared editor.

Both paths reload the current `LevelLibrary` pack and write `ArtSource/qa/level_report.md` and `.csv`. These generated reports are deliberately not committed. All slots and variants are checked, plus the base board separately if it differs from variant zero. Tutorial slots 1–3 use tutorial rules. Budgets exclude adaptive assistance, so a pass also fits any valid AttemptPlan that only adds moves. The independent bot uses greedy priorities followed by 23 deterministic random restarts, with at most 3x the budget per attempt; it never calls a solver. Removal is free and each 720-degree gesture calls `CompleteMove` once.

Solver/budget and star failures fail the test, as do pack/model load exceptions and unknown JSON fields (which would otherwise be silently discarded). Bot failures, geometry problems and data-to-model field transfer failures are reported as advisory check failures. Rating drops exceeding 25 Elo between adjacent slot medians are outliers only. Reflection checks every serialized ring/link mechanic field through data, definition and model; this catches newly added but unmapped fields without a frozen mechanic allow-list. It cannot prove the intended semantics of a future mechanic. Geometry checks rings plus teeth against the dynamically fitted 9:19.5 viewport with HUD reserves including the development toolbar. It is a mathematical fit check, not device notch/safe-area or visual readability proof.

## GitHub Unity activation

The workflow uses [GameCI test runner v4](https://game.ci/docs/github/test-runner/) and its [Personal activation procedure](https://game.ci/docs/github/activation/). Setup for an eligible Unity Personal account:

1. Install Unity Hub and sign in with the account intended for CI.
2. In Hub, choose **Preferences > Licenses > Add > Get a free personal license**. Complete activation even if a license already appears, to generate a `.ulf` file.
3. Locate `Unity_lic.ulf`: macOS `/Library/Application Support/Unity/Unity_lic.ulf`, Windows `C:\ProgramData\Unity\Unity_lic.ulf`, Linux `~/.local/share/unity3d/Unity/Unity_lic.ulf`. If Hub cannot generate it, follow GameCI's current activation troubleshooting; do not commit license files.
4. In **AshrithSathu/orcub > Settings > Secrets and variables > Actions > New repository secret**, add all three:
   - `UNITY_LICENSE`: the entire `.ulf` file contents.
   - `UNITY_EMAIL`: the Unity account email.
   - `UNITY_PASSWORD`: that account's password.
5. Open **Actions > Unity EditMode tests > Run workflow**, select `dev`, and run. Check the test step and download `unity-editmode-results` for test XML/logs and level reports.

The workflow reads the editor version from ProjectVersion.txt and requires a corresponding GameCI Linux editor image. Missing any activation secret produces a clear notice and job summary; test steps skip gracefully. Fork pull requests also skip because they receive no repository secrets. A green skipped job is not test evidence. The workflow runs the full EditMode suite, not only QA. Backend CI needs no owner secrets. Paid licenses require GameCI's Professional (`UNITY_SERIAL`) or license-server configuration instead of this Personal setup; choose the activation method matching your licence.

Only run trusted repository code with repository secrets; this uses `pull_request`, never `pull_request_target`. GitHub-hosted CI and Unity licensing have not been validated merely by installing these workflow files.
