#!/usr/bin/env bash
# 1F2 design §8: on the stack deploy/smoke.sh left up, back up, verify, simulate a disaster and restore. Exits 0 only
# if every check passed. Throwaway: never run it on a real deployment.
set -euo pipefail
cd "$(dirname "$0")"
# The throwaway remote (an append-only rest-server) is on egress: backup reaches it only through the remote overlay.
COMPOSE=(docker compose -f compose.yaml -f compose.local.yaml -f local/compose.backup-test.yaml)
REMOTE=("${COMPOSE[@]}" -f compose.backup-remote.yaml)
RR='restic --no-cache --repository-file /run/backup-secrets/remote_repository --password-file /run/backup-secrets/restic_password'
R='restic --no-cache --repo /repo --password-file /run/backup-secrets/restic_password'
fail() { echo "FAIL: $*" >&2; exit 1; }
backup() { "${COMPOSE[@]}" exec -T backup nid-backup "$@"; }
in_backup() { "${COMPOSE[@]}" exec -T backup sh -c "$1"; }
snapshots() { in_backup "$R snapshots --json | jq length"; }

# A previous run left its remote configured: the first checks are about the local repository alone.
rm -f backup-secrets/remote_repository
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

# Review Focus 5: a remote configured without the overlay fails within bounded time, keeps the local snapshot.
echo 'rest:http://backup-remote-test:8000/nid' > backup-secrets/remote_repository
start=$(date +%s)
backup backup-now || fail "the local backup failed because of the remote"
[ $(( $(date +%s) - start )) -lt 180 ] || fail "an unreachable remote took $(( $(date +%s) - start )) s"
if backup status >/dev/null; then fail "healthy with an unreachable remote"; fi
health=$(backup status || true)
[[ $health == *"dépôt distant : unreachable"* ]] || fail "status: $health"
echo "ok: an unreachable remote is reported, the local backup still happens"

NID_BACKUP_REMOTE_FORGET=false "${REMOTE[@]}" up -d --wait --wait-timeout 120 backup
backup backup-now
backup status >/dev/null || fail "unhealthy with a reachable remote: $(backup status)"
[ "$("${REMOTE[@]}" exec -T backup sh -c "$RR snapshots --json | jq length")" -ge 1 ] || fail "no remote snapshot"
echo "ok: the remote overlay sends each snapshot to an append-only remote"

./restore.sh --verify || fail "restore.sh --verify from the local repository"
./restore.sh --verify --from remote --compose-file "$PWD/local/compose.backup-test-client.yaml" ||
  fail "restore.sh --verify from the remote"
echo "ok: --verify passes from both repositories while the real stack runs"

# Review Focus 2: a failed --verify leaves nothing behind, not even its copy of the secrets.
mv backup-secrets/restic_password backup-secrets/restic_password.keep
echo wrong > backup-secrets/restic_password
if ./restore.sh --verify 2>/tmp/nid-verify.log; then fail "--verify passed with a wrong password"; fi
mv backup-secrets/restic_password.keep backup-secrets/restic_password
grep -qi "mot de passe" /tmp/nid-verify.log || fail "no clear message for a wrong password: $(tail -5 /tmp/nid-verify.log)"
[ -z "$(docker compose -p nid-restore-test ps -a -q)" ] || fail "the throwaway project is still there"
[ -z "$(docker volume ls -q --filter name=nid-restore-test)" ] || fail "throwaway volumes are still there"
[ -z "$(find "${TMPDIR:-/tmp}" -maxdepth 1 -name 'nid-restore.*' 2>/dev/null)" ] || fail "the temporary folder is still there"
echo "ok: a failed --verify says why and leaves nothing behind"
