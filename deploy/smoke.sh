#!/usr/bin/env bash
# 1F1 design §5: from a clean checkout, set up, start, and prove a signed-in Nid. Exits 0 only if every check passed.
# Throwaway: uses localtest.me names and a Pocket ID with a static API key (compose.local.yaml). Never run it on a real deployment.
set -euo pipefail
cd "$(dirname "$0")"
COMPOSE=(docker compose -f compose.yaml -f compose.local.yaml)
URL=https://nid.localtest.me
# NID_SMOKE set in front of a call reaches the function, not its children: pass it on explicitly.
playwright() { NID_URL=$URL NID_SMOKE=${NID_SMOKE:-} pnpm --dir ../web exec playwright test "$@"; }
# After a restart of nid, wait for its readiness (nid healthcheck: the image has no shell or wget) before a test opens a page.
api_ready() {
  for _ in $(seq 60); do
    "${COMPOSE[@]}" exec -T nid nid healthcheck 2>/dev/null && return 0
    sleep 2
  done
  echo "FAIL: nid not ready after 120 s" >&2
  exit 1
}

./setup.sh --nid-host nid.localtest.me --auth-host auth.localtest.me
# Pocket ID first, so its groups, users and Nid's client exist (and Nid's client secret is known) before nid starts.
"${COMPOSE[@]}" up -d --wait --wait-timeout 120 pocket-id proxy
read -r CLIENT_ID CLIENT_SECRET < <(node local/bootstrap-pocket-id.mjs)
./setup.sh --nid-host nid.localtest.me --auth-host auth.localtest.me --oidc-client-id "$CLIENT_ID" --oidc-client-secret "$CLIENT_SECRET" >/dev/null
"${COMPOSE[@]}" up -d --build --wait --wait-timeout 300
api_ready
echo "ok: every service is up and healthy"

curl -skf "$URL/auth/session" | grep -q '"authenticated":false'
echo "ok: the session route answers"

# Hardening (1F1 final review): the key folder is private, only nid reaches the Internet, the SPA fallback never
# answers for an asset or an API path.
[ "$(docker run --rm -v nid_dataprotection-keys:/k busybox:1.37 stat -c %a /k)" = "700" ]
echo "ok: the Data Protection key folder is 0700"
docker run --rm -v nid_logs:/l:ro busybox:1.37 sh -c 'test "$(stat -c %a /l)" = 700 && cat /l/nid-*.log' | grep -q "Now listening on"
echo "ok: nid writes its log file to the logs volume"
if "${COMPOSE[@]}" exec -T pocket-id wget -q -T 5 -O /dev/null https://raw.githubusercontent.com 2>/dev/null; then
  echo "FAIL: pocket-id reaches the Internet" >&2; exit 1
fi
echo "ok: pocket-id has no way out"
# The image runs hardened: read-only root file system, every capability dropped, a non-root user.
nid_container=$("${COMPOSE[@]}" ps -q nid)
[ "$(docker inspect --format '{{.HostConfig.ReadonlyRootfs}} {{.HostConfig.CapDrop}} {{.Config.User}}' "$nid_container")" = "true [ALL] 1654" ]
echo "ok: nid runs with a read-only root, no capability and user 1654"
[ "$(curl -sk -o /dev/null -w '%{http_code}' "$URL/assets/missing-chunk.js")" = "404" ]
[ "$(curl -sk -o /dev/null -w '%{content_type}' "$URL/mcp")" != "text/html; charset=utf-8" ]
echo "ok: a missing asset is a 404 and /mcp is not the SPA"

# NID_JOURNEYS=sample runs only the subscriptions journeys, which sign alice and bob in (the persons check below needs both): a quick local run. CI (ci.yml) runs them all.
if [ "${NID_JOURNEYS:-all}" = "all" ]; then playwright; else playwright e2e/subscriptions.spec.ts; fi
echo "ok: journeys (${NID_JOURNEYS:-all})"

# 1F4: the recovery CLI runs inside the chiseled image through its `nid` link. The journeys signed alice and bob in, which
# is what creates them (the identity provider's groups let them through).
persons=$("${COMPOSE[@]}" exec -T nid nid admin list-persons)
grep -q "alice@example.test" <<<"$persons" && grep -q "bob@example.test" <<<"$persons" \
  || { echo "FAIL: nid admin list-persons: $persons" >&2; exit 1; }
"${COMPOSE[@]}" exec -T nid nid admin revoke-sessions --person bob@example.test
echo "ok: nid admin runs in the image (list-persons, revoke-sessions)"

# Data Protection keys on a volume: a session opened before a restart of nid is still open after it.
NID_SMOKE=1 playwright e2e/restart.smoke.ts --grep "before"
"${COMPOSE[@]}" restart nid
"${COMPOSE[@]}" up -d --wait --wait-timeout 120 nid
api_ready
NID_SMOKE=1 playwright e2e/restart.smoke.ts --grep "after"
echo "ok: a session survives a restart of nid"

# SkiaSharp in the image: an icon the catalog sync stored and re-encoded (1C2c). Needs outbound access.
if curl -sf --max-time 5 -o /dev/null https://raw.githubusercontent.com; then
  NID_SMOKE=1 playwright e2e/icons.smoke.ts
  echo "ok: an icon was decoded and re-encoded in the image"
else
  echo "skipped: icon check (no outbound access)"
fi
