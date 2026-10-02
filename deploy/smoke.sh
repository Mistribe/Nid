#!/usr/bin/env bash
# 1F1 design §5: from a clean checkout, set up, start, and prove a signed-in Nid. Exits 0 only if every check passed.
# Throwaway: uses localtest.me names and the development password of web/e2e/session.ts. Never run it on a real deployment.
set -euo pipefail
cd "$(dirname "$0")"
COMPOSE=(docker compose -f compose.yaml -f compose.local.yaml)
PASSWORD='Nid-Dev-User-1!'
URL=https://nid.localtest.me
# NID_SMOKE set in front of a call reaches the function, not its children: pass it on explicitly.
playwright() { NID_URL=$URL NID_SMOKE=${NID_SMOKE:-} pnpm --dir ../web exec playwright test "$@"; }
# nid-api has no healthcheck (a chiseled image), so `up --wait` can return before it listens: wait for its readiness
# through nid-web, which proxies /health/ready to it, before a test opens a page.
api_ready() {
  for _ in $(seq 60); do
    "${COMPOSE[@]}" exec -T nid-web wget -q --spider http://127.0.0.1:8080/health/ready 2>/dev/null && return 0
    sleep 2
  done
  echo "FAIL: nid-api not ready after 120 s" >&2
  exit 1
}

./setup.sh --nid-host nid.localtest.me --auth-host auth.localtest.me --user alice --name "Alice Dev" \
  --email alice@example.test --password "$PASSWORD" --extra-user "bob:Bob Dev:bob@example.test"
"${COMPOSE[@]}" up -d --build --wait --wait-timeout 300
api_ready
echo "ok: every service is up and healthy"

# Both adults pre-registered, alice as admin: the first sign-in provisions an adult only when the base has none (1G),
# so pre-registering bob alone would lock alice out.
"${COMPOSE[@]}" exec -T postgres psql -U nid -d nid -v ON_ERROR_STOP=1 -q -c "
  insert into core.person (id, kind, display_name, email, is_admin, session_epoch, preferences, created_at)
  select gen_random_uuid(), 'adult', v.name, v.email, v.admin, 0, '{}', now()
  from (values ('Alice Dev', 'alice@example.test', true), ('Bob Dev', 'bob@example.test', false)) as v(name, email, admin)
  where not exists (select 1 from core.person p where lower(p.email) = v.email);"

# 1F4: the recovery CLI runs inside the chiseled image through its `nid` link.
persons=$("${COMPOSE[@]}" exec -T nid-api nid admin list-persons)
grep -q "alice@example.test" <<<"$persons" && grep -q "bob@example.test" <<<"$persons" \
  || { echo "FAIL: nid admin list-persons: $persons" >&2; exit 1; }
"${COMPOSE[@]}" exec -T nid-api nid admin grant-admin --person bob@example.test --yes
echo "ok: nid admin runs in the image (list-persons, grant-admin)"

curl -skf "$URL/auth/session" | grep -q '"authenticated":false'
echo "ok: the session route answers"

# Hardening (1F1 final review): the key folder is private, only nid-api reaches the Internet, the SPA fallback never
# answers for an asset or an API path.
[ "$(docker run --rm -v nid_dataprotection-keys:/k busybox:1.37 stat -c %a /k)" = "700" ]
echo "ok: the Data Protection key folder is 0700"
for service in authelia nid-web; do
  if "${COMPOSE[@]}" exec -T "$service" wget -q -T 5 -O /dev/null https://raw.githubusercontent.com 2>/dev/null; then
    echo "FAIL: $service reaches the Internet" >&2; exit 1
  fi
done
echo "ok: authelia and nid-web have no way out"
[ "$(curl -sk -o /dev/null -w '%{http_code}' "$URL/assets/missing-chunk.js")" = "404" ]
[ "$(curl -sk -o /dev/null -w '%{content_type}' "$URL/mcp")" != "text/html; charset=utf-8" ]
echo "ok: a missing asset is a 404 and /mcp is not the SPA"

playwright
echo "ok: journeys"

# Data Protection keys on a volume: a session opened before a restart of nid-api is still open after it.
NID_SMOKE=1 playwright e2e/restart.smoke.ts --grep "before"
"${COMPOSE[@]}" restart nid-api
"${COMPOSE[@]}" up -d --wait --wait-timeout 120 nid-web
api_ready
NID_SMOKE=1 playwright e2e/restart.smoke.ts --grep "after"
echo "ok: a session survives a restart of nid-api"

# SkiaSharp in the image: an icon the catalog sync stored and re-encoded (1C2c). Needs outbound access.
if curl -sf --max-time 5 -o /dev/null https://raw.githubusercontent.com; then
  NID_SMOKE=1 playwright e2e/icons.smoke.ts
  echo "ok: an icon was decoded and re-encoded in the image"
else
  echo "skipped: icon check (no outbound access)"
fi
