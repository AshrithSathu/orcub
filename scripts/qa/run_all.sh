#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
cat <<'HELP'
Unity QA: open this project in the version in ProjectSettings/ProjectVersion.txt.
Run Orcub > QA > Verify All Levels, or Window > General > Test Runner > EditMode
and filter QA_AllLevelsSolvable. Reports: ArtSource/qa/level_report.{md,csv}.
Do not enter Play Mode in the shared editor; reopen Assets/Scenes/Game.unity after tests.
HELP
cd "$repo_root/backend"
export SQLX_OFFLINE=true
cargo fmt --check
cargo clippy --all-targets --locked -- -D warnings
if [[ -z "${DATABASE_URL:-}" ]]; then
  echo 'DATABASE_URL must identify a disposable LOCAL test server with CREATEDB (see backend/README.md).' >&2
  exit 1
fi
# Explicit opt-in protects against inherited production/staging credentials.
if [[ "${ORCUB_QA_DISPOSABLE_DB:-}" != 1 ]]; then
  echo 'Confirm DATABASE_URL is disposable, then set ORCUB_QA_DISPOSABLE_DB=1 to run sqlx database tests.' >&2
  exit 1
fi
cargo test --locked
