#!/usr/bin/env bash
# Runs every automated test: backend (unit + integration on real PostgreSQL) and frontend unit tests.
set -euo pipefail
cd "$(dirname "$0")/.."
[ -f .env ] && export RESORT_TEST_DB="${RESORT_TEST_DB:-$(grep -E '^RESORT_TEST_DB=' .env | cut -d= -f2-)}"
: "${RESORT_TEST_DB:?Set RESORT_TEST_DB (run scripts/setup-dev.sh)}"
# Release build: a separate bin/Release copy, so this works while `dotnet watch run` (Debug) has the API running.
dotnet test ResortOS.sln -c Release --logger "console;verbosity=normal" "$@"
(cd frontend && npm run test:ci)
