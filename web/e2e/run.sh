#!/usr/bin/env bash
# Runs the Playwright journeys against the Aspire dev stack (web design §7: local only, not in blocking CI).
# Needs the stack up (dotnet run --project src/Nid.AppHost), started from this checkout after deploy/dev/setup.sh.
set -euo pipefail
cd "$(dirname "$0")/.."
URL="${NID_URL:-https://nid.localtest.me}"
if [ "$(curl -sk -o /dev/null -w '%{http_code}' "$URL/auth/session")" != "200" ]; then
  echo "The dev stack does not answer at $URL: start it first (dotnet run --project src/Nid.AppHost)." >&2
  exit 1
fi
pnpm exec playwright install chromium
pnpm exec playwright test "$@"
