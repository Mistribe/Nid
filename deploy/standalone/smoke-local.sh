#!/usr/bin/env bash
# Issue 215 (docs/identity-and-access/local-accounts.md §18.2): a fresh install of deploy/standalone without Pocket ID, as
# the README tells it, then the Playwright journeys of local accounts (web/playwright.local.config.ts). Exits 0 only if
# every check passed. Throwaway: its own compose project, volumes removed before and after. Never on a real deployment.
#
#   NID_IMAGE   the image to try (default nid:local, built from this checkout: docker build -f src/Hosting/Nid.Host/Dockerfile -t nid:local .)
#   NID_PORT    where it listens on this machine (default 8080)
#   KEEP=1      leaves the stack running at the end, to look at it
set -euo pipefail
cd "$(dirname "$0")"
PROJECT=nid-smoke-local
PORT=${NID_PORT:-8080}
URL=http://localhost:$PORT
WORK=$(mktemp -d)
COMPOSE=(docker compose -p "$PROJECT" --project-directory "$WORK" -f "$WORK/compose.yaml" --profile postgres)
cleanup() {
  if [ "${KEEP:-}" != 1 ]; then "${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true; fi
  rm -rf "$WORK"
}
trap cleanup EXIT

# The README's steps: the two files, .env from its example with what it asks for, then up.
cp compose.yaml .env.example "$WORK/"
cp .env.example "$WORK/.env"
sed -i \
  -e "s|^NID_URL=.*|NID_URL=$URL|" \
  -e "s|^NID_CSRF_KEY=.*|NID_CSRF_KEY=$(openssl rand -hex 32)|" \
  -e "s|^NID_DB_PASSWORD=.*|NID_DB_PASSWORD=$(openssl rand -hex 16)|" \
  -e "s|^NID_WEB_PORT=.*|NID_WEB_PORT=$PORT|" \
  -e "s|^# NID_IMAGE=.*|NID_IMAGE=${NID_IMAGE:-nid:local}|" \
  "$WORK/.env"
"${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
"${COMPOSE[@]}" up -d --wait --wait-timeout 180
echo "ok: nid and its Postgres are up, without an identity provider"

session=$(curl -sf "$URL/auth/session")
grep -q '"methods":\["password"\]' <<<"$session" && grep -q '"setupRequired":true' <<<"$session" \
  || { echo "FAIL: /auth/session: $session" >&2; exit 1; }
echo "ok: local accounts, the setup still to do"

# The README's next step: the setup address, in the log.
setup=$("${COMPOSE[@]}" logs nid 2>&1 | grep -o "$URL/setup#nid_setup_[A-Za-z0-9_-]*" | tail -1)
[ -n "$setup" ] || { echo "FAIL: no setup address in the log" >&2; "${COMPOSE[@]}" logs nid | tail -50 >&2; exit 1; }
echo "ok: the log gives the setup address"

NID_URL=$URL NID_SETUP_URL=$setup pnpm --dir ../../web exec playwright test -c playwright.local.config.ts
echo "ok: the journeys of local accounts"

# The recovery CLI, inside the image: the journeys created alice (the setup) and bob (a link).
persons=$("${COMPOSE[@]}" exec -T nid nid admin list-persons)
grep -q "Alice" <<<"$persons" && grep -q "Bob" <<<"$persons" || { echo "FAIL: nid admin list-persons: $persons" >&2; exit 1; }
"${COMPOSE[@]}" exec -T nid nid admin reset-totp --person Bob >/dev/null
echo "ok: nid admin runs in the image (list-persons, reset-totp)"
