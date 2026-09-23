#!/usr/bin/env bash
# Logs in as an Authelia user through the real OIDC flow (no browser), then exercises the API.
# Usage: smoke.sh [user]   (default: alice). Needs the stack up and the host running.
set -euo pipefail
USER_NAME="${1:-alice}"
PASSWORD='Nid-Dev-User-1!'
APP=https://nid.localtest.me
IDP=https://auth.localtest.me
JAR=$(mktemp)
curl_() { curl -sk -c "$JAR" -b "$JAR" "$@"; }

AUTHORIZE=$(curl_ -o /dev/null -w '%{redirect_url}' "$APP/auth/login")
PORTAL=$(curl_ -o /dev/null -w '%{redirect_url}' "$AUTHORIZE")
FLOW_ID=$(python3 -c 'import sys,urllib.parse as u;print(u.parse_qs(u.urlparse(sys.argv[1]).query)["flow_id"][0])' "$PORTAL")

FIRST_FACTOR=$(curl_ -X POST "$IDP/api/firstfactor" -H 'Content-Type: application/json' \
  -d "{\"username\":\"$USER_NAME\",\"password\":\"$PASSWORD\",\"keepMeLoggedIn\":false,\"flowID\":\"$FLOW_ID\",\"flow\":\"openid_connect\",\"subflow\":\"\",\"requestMethod\":\"GET\",\"targetURL\":\"$AUTHORIZE\"}")
NEXT=$(python3 -c 'import json,sys;print(json.loads(sys.argv[1])["data"]["redirect"])' "$FIRST_FACTOR")

# The response is an auto-submitting form (response_mode=form_post): read it and post it to the callback.
python3 - "$(curl_ "$NEXT")" > "$JAR.form" <<'PY'
import html, re, sys
page = sys.argv[1]
print("ACTION=" + html.unescape(re.search(r'<form[^>]*action="([^"]+)"', page).group(1)))
for name, value in re.findall(r'<input[^>]*name="([^"]+)"[^>]*value="([^"]*)"', page):
    print("F " + name + "=" + html.unescape(value))
PY
ACTION=$(grep '^ACTION=' "$JAR.form" | cut -d= -f2-)
ARGS=(); while IFS= read -r line; do ARGS+=(--data-urlencode "${line#F }"); done < <(grep '^F ' "$JAR.form")
CALLBACK=$(curl_ -o /dev/null -w '%{http_code}' -X POST "${ARGS[@]}" "$ACTION")
echo "callback status: $CALLBACK (302 expected)"

SESSION=$(curl_ "$APP/auth/session"); echo "session: $SESSION"
if ! grep -q '"authenticated":true' <<<"$SESSION"; then echo "login refused (third adult?)"; exit 0; fi
CSRF=$(python3 -c 'import json,sys;print(json.loads(sys.argv[1])["csrf"])' "$SESSION")

echo -n "web app served through the proxy: "
if curl_ "$APP/" | grep -q '<div id="root">'; then echo ok; else echo "MISSING"; exit 1; fi
echo -n "session carries preferences: "
python3 -c 'import json,sys;print(json.loads(sys.argv[1])["preferences"])' "$SESSION"
echo "capabilities: $(curl_ "$APP/api/v1/platform/capabilities")"
echo -n "save a theme (200 expected): "
curl_ -o /dev/null -w '%{http_code}\n' -X PATCH "$APP/api/v1/core/me/preferences" \
  -H "X-Nid-Csrf: $CSRF" -H 'Content-Type: application/json' -d '{"theme":"sauge"}'
curl_ -o /dev/null -X PATCH "$APP/api/v1/core/me/preferences" \
  -H "X-Nid-Csrf: $CSRF" -H 'Content-Type: application/json' -d '{"theme":"neutre"}'

echo -n "create dependent without CSRF header (403 expected): "
curl_ -o /dev/null -w '%{http_code}\n' -X POST "$APP/api/v1/core/persons/dependents" -H 'Content-Type: application/json' -d '{"displayName":"Kid"}'
echo "create dependent with CSRF header:"
curl_ -X POST "$APP/api/v1/core/persons/dependents" -H "X-Nid-Csrf: $CSRF" -H 'Content-Type: application/json' -d '{"displayName":"Kid"}'; echo
echo "list persons:"; curl_ "$APP/api/v1/core/persons"; echo
echo -n "logout (204 expected): "
curl_ -o /dev/null -w '%{http_code}\n' -X POST "$APP/auth/logout" -H "X-Nid-Csrf: $CSRF"
echo "session after logout: $(curl_ "$APP/auth/session")"
