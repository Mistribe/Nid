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

./setup.sh --nid-host nid.localtest.me --auth-host auth.localtest.me --user alice --name "Alice Dev" \
  --email alice@example.test --password "$PASSWORD" --extra-user "bob:Bob Dev:bob@example.test"
"${COMPOSE[@]}" up -d --build --wait --wait-timeout 300
echo "ok: every service is up and healthy"

# Both adults pre-registered, alice as admin: the first sign-in provisions an adult only when the base has none (1G),
# so pre-registering bob alone would lock alice out.
"${COMPOSE[@]}" exec -T postgres psql -U nid -d nid -v ON_ERROR_STOP=1 -q -c "
  insert into core.person (id, kind, display_name, email, is_admin, session_epoch, preferences, created_at)
  select gen_random_uuid(), 'adult', v.name, v.email, v.admin, 0, '{}', now()
  from (values ('Alice Dev', 'alice@example.test', true), ('Bob Dev', 'bob@example.test', false)) as v(name, email, admin)
  where not exists (select 1 from core.person p where lower(p.email) = v.email);"

curl -skf "$URL/auth/session" | grep -q '"authenticated":false'
echo "ok: the session route answers"

playwright
echo "ok: journeys"

# Data Protection keys on a volume: a session opened before a restart of nid-api is still open after it.
NID_SMOKE=1 playwright e2e/restart.smoke.ts --grep "before"
"${COMPOSE[@]}" restart nid-api
"${COMPOSE[@]}" up -d --wait --wait-timeout 120 nid-web
NID_SMOKE=1 playwright e2e/restart.smoke.ts --grep "after"
echo "ok: a session survives a restart of nid-api"

# SkiaSharp in the image: an icon the catalog sync stored and re-encoded (1C2c). Needs outbound access.
if curl -sf --max-time 5 -o /dev/null https://raw.githubusercontent.com; then
  NID_SMOKE=1 playwright e2e/icons.smoke.ts
  echo "ok: an icon was decoded and re-encoded in the image"
else
  echo "skipped: icon check (no outbound access)"
fi
