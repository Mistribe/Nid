#!/usr/bin/env bash
# 1F1 design §5: from a clean checkout, set up, start, and prove a signed-in Nid. Exits 0 only if every check passed.
# Throwaway: uses localtest.me names and a Pocket ID with a static API key (compose.local.yaml). Never run it on a real deployment.
set -euo pipefail
cd "$(dirname "$0")"
COMPOSE=(docker compose -f compose.yaml -f compose.local.yaml -f local/compose.worker-gate.yaml)
# The volumes' prefix: COMPOSE_PROJECT_NAME runs the test next to another installation, whose nid_* volumes it leaves alone.
P=${COMPOSE_PROJECT_NAME:-nid}
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
# reaches SERVICE PORT ADDRESS...: whether one of the addresses answers from the service's network (a busybox in its network
# namespace: the service's own image may have no shell, no wget, no nc).
reaches() {
  local service=$1 port=$2; shift 2
  docker run --rm --network "container:$("${COMPOSE[@]}" --profile worker ps -q "$service")" busybox:1.37 \
    sh -c "for a in $*; do nc -z -w 3 \$a $port && exit 0; done; exit 1"
}

./setup.sh --nid-host nid.localtest.me --auth-host auth.localtest.me
# Pocket ID first, so its groups, users and Nid's client exist (and Nid's client secret is known) before nid starts.
"${COMPOSE[@]}" up -d --wait --wait-timeout 120 pocket-id proxy
read -r CLIENT_ID CLIENT_SECRET < <(node local/bootstrap-pocket-id.mjs)
./setup.sh --nid-host nid.localtest.me --auth-host auth.localtest.me --oidc-client-id "$CLIENT_ID" --oidc-client-secret "$CLIENT_SECRET" >/dev/null
"${COMPOSE[@]}" up -d --build --wait --wait-timeout 300
api_ready
echo "ok: every service is up and healthy"

curl -skf "$URL/auth/session" | grep '"authenticated":false' >/dev/null
echo "ok: the session route answers"

# Hardening (1F1 final review): the key folder is private, only nid reaches the Internet, the SPA fallback never
# answers for an asset or an API path.
[ "$(docker run --rm -v "${P}_dataprotection-keys:/k" busybox:1.37 stat -c %a /k)" = "700" ]
echo "ok: the Data Protection key folder is 0700"
# grep reads to the end (no -q): with pipefail, -q closes the pipe at the first match and the writer dies of SIGPIPE.
docker run --rm -v "${P}_logs:/l:ro" busybox:1.37 sh -c 'test "$(stat -c %a /l)" = 700 && cat /l/nid-*.log' | grep "Now listening on" >/dev/null
echo "ok: nid writes its log file to the logs volume"
# A public address (by IP: the route, not only the name) answers from nid's network, which has a way out, and not from Pocket
# ID's. Without the first, this machine has no outbound access and the second proves nothing.
if reaches nid 443 1.1.1.1; then
  if reaches pocket-id 443 1.1.1.1; then echo "FAIL: pocket-id reaches the Internet" >&2; exit 1; fi
  echo "ok: pocket-id has no way out"
else
  echo "skipped: pocket-id's way out (no outbound access)"
fi
# The image runs hardened: read-only root file system, every capability dropped, a non-root user.
nid_container=$("${COMPOSE[@]}" ps -q nid)
[ "$(docker inspect --format '{{.HostConfig.ReadonlyRootfs}} {{.HostConfig.CapDrop}} {{.Config.User}}' "$nid_container")" = "true [ALL] 1654" ]
echo "ok: nid runs with a read-only root, no capability and user 1654"
[ "$(curl -sk -o /dev/null -w '%{http_code}' "$URL/assets/missing-chunk.js")" = "404" ]
[ "$(curl -sk -o /dev/null -w '%{content_type}' "$URL/mcp")" != "text/html; charset=utf-8" ]
echo "ok: a missing asset is a 404 and /mcp is not the SPA"

# #476: a model worker next to Nid for the « Réglages › IA » journey, which pairs it with the code and fingerprint of its console
# (« Pairing code: ABCD-EFGH  Key fingerprint: <hex> »). #528: with the scripted engine of compose.local.yaml, for the assistant.
# #603: behind local/compose.worker-gate.yaml's gate, the worker has no route to nid.
"${COMPOSE[@]}" --profile worker up -d --build --wait --wait-timeout 300 worker engine worker-gate
# `unpair` rather than `pairing-code`: a worker-data volume left by an earlier run holds a pairing; its last line is the new code.
read -r _ _ code _ _ fingerprint < <("${COMPOSE[@]}" --profile worker exec -T worker dotnet Nid.Ai.Worker.dll unpair | tail -1) || true
[[ $code =~ ^[A-Z0-9]{4}-[A-Z0-9]{4}$ && -n $fingerprint ]] || { echo "FAIL: the worker gave no pairing code (unpair: $code $fingerprint)" >&2; exit 1; }
export NID_WORKER_PAIRING="$code $fingerprint"
echo "ok: a model worker is up, waiting for its pairing"

# NID_JOURNEYS=sample runs only the subscriptions journeys, which sign alice and bob in (the persons check below needs both), and
# the worker's, which runs a job on it: a quick local run, without the whole chromium project the worker's waits for
# (--no-deps, web/playwright.config.ts). CI (ci.yml) runs them all.
if [ "${NID_JOURNEYS:-all}" = "all" ]; then playwright; else playwright --no-deps e2e/subscriptions.spec.ts e2e/ai-workers.spec.ts; fi
echo "ok: journeys (${NID_JOURNEYS:-all})"

# #603: the worker ran the journey's job (its « Tester ») with no route to nid. From the worker's network, none of nid's
# addresses answers; from the gate's, the same probe reaches nid, so a refusal is the network's, not the probe's.
read -r -a nid_ips <<<"$(docker inspect --format '{{range .NetworkSettings.Networks}}{{.IPAddress}} {{end}}' "$nid_container")"
reaches worker-gate 8080 "${nid_ips[@]}" || { echo "FAIL: the probe does not reach nid from the gate (${nid_ips[*]})" >&2; exit 1; }
if reaches worker 8080 "${nid_ips[@]}"; then echo "FAIL: the worker has a route to nid" >&2; exit 1; fi
echo "ok: the worker runs jobs with no route to nid"

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
