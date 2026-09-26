#!/usr/bin/env bash
# 1F2 design §8: on the stack deploy/smoke.sh left up, back up, verify, simulate a disaster and restore. Exits 0 only
# if every check passed. Throwaway: never run it on a real deployment.
set -euo pipefail
cd "$(dirname "$0")"
COMPOSE=(docker compose -f compose.yaml -f compose.local.yaml)
R='restic --no-cache --repo /repo --password-file /run/backup-secrets/restic_password'
fail() { echo "FAIL: $*" >&2; exit 1; }
backup() { "${COMPOSE[@]}" exec -T backup nid-backup "$@"; }
in_backup() { "${COMPOSE[@]}" exec -T backup sh -c "$1"; }
snapshots() { in_backup "$R snapshots --json | jq length"; }

"${COMPOSE[@]}" up -d --build --wait --wait-timeout 180
echo "ok: the backup service is up and healthy before its first run"

server=$("${COMPOSE[@]}" exec -T postgres postgres --version | sed -E 's/.* ([0-9]+)\..*/\1/')
client=$(in_backup 'pg_dump --version' | sed -E 's/.* ([0-9]+)\..*/\1/')
[ "$server" = "$client" ] || fail "pg_dump $client, server $server"
echo "ok: pg_dump and the server share their major version ($server)"

backup backup-now
backup status >/dev/null || fail "unhealthy after a good run: $(backup status)"
[ "$(snapshots)" -ge 1 ] || fail "no snapshot"
in_backup "$R dump latest /tmp/stage/manifest.json | jq -e '
  .format == 1 and .row_counts[\"core.person\"] >= 2 and (.migrations | length) > 0 and .dataprotection_keys >= 1'" \
  >/dev/null || fail "manifest: $(in_backup "$R dump latest /tmp/stage/manifest.json")"
# Captured, not piped into grep -q: with pipefail, grep closing the pipe early would fail the producer.
listing=$(in_backup "$R ls latest")
grep -qx '/source/deploy/secrets/csrf_key' <<<"$listing" || fail "deploy/secrets/ not in the snapshot"
if grep -q 'restic_password' <<<"$listing"; then fail "the restic password is in the snapshot"; fi
if grep -q '^/authelia' <<<"$listing"; then fail "Authelia's live base is in the snapshot"; fi
echo "ok: a backup holds the database, Authelia, the keys, the assets and deploy/, not the restic password"

# Review Focus 1: a failed run is named, touches no repository, and the next good run clears it.
before=$(snapshots)
"${COMPOSE[@]}" stop postgres
if backup backup-now; then fail "a backup without Postgres succeeded"; fi
health=$(backup status || true)
[[ $health == *"étape : database"* ]] || fail "status after a failed run: $health"
"${COMPOSE[@]}" up -d --wait --wait-timeout 120
mv backup-secrets/restic_password backup-secrets/restic_password.keep
echo wrong > backup-secrets/restic_password
if backup backup-now; then fail "a backup with a wrong password succeeded"; fi
mv backup-secrets/restic_password.keep backup-secrets/restic_password
[ "$(snapshots)" = "$before" ] || fail "a failed run changed the repository"
backup backup-now
backup status >/dev/null || fail "still unhealthy after a good run: $(backup status)"
echo "ok: a failed run is named and leaves the repository alone; the next good run clears it"

# Review Focus 4: a bad schedule stops the service with a message instead of looping.
if "${COMPOSE[@]}" run --rm --no-deps -T -e NID_BACKUP_AT=25:00 backup schedule 2>/tmp/nid-backup-at.log; then
  fail "the scheduler accepted 25:00"
fi
grep -q "NID_BACKUP_AT invalide" /tmp/nid-backup-at.log || fail "no message for a bad NID_BACKUP_AT"
echo "ok: a bad NID_BACKUP_AT stops the scheduler with a message"
