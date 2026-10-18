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
URL=https://nid.localtest.me
playwright() { NID_URL=$URL NID_SMOKE=1 pnpm --dir ../web exec playwright test "$@"; }
fail() { echo "FAIL: $*" >&2; exit 1; }
backup() { "${COMPOSE[@]}" exec -T backup nid-backup "$@"; }
in_backup() { "${COMPOSE[@]}" exec -T backup sh -c "$1"; }
snapshots() { in_backup "$R snapshots --json | jq length"; }
# nid-api has no healthcheck (a chiseled image), so `up --wait` can return before it listens: wait for its readiness
# through nid-web, which proxies /health/ready to it, before a test opens a page.
api_ready() {
  for _ in $(seq 60); do
    "${COMPOSE[@]}" exec -T nid-web wget -q --spider http://127.0.0.1:8080/health/ready 2>/dev/null && return 0
    sleep 2
  done
  fail "nid-api not ready after 120 s"
}

# A previous run left its remote configured: the first checks are about the local repository alone.
rm -f backup-secrets/remote_repository
"${COMPOSE[@]}" up -d --build --wait --wait-timeout 180
echo "ok: the backup service is up and healthy before its first run"

server=$("${COMPOSE[@]}" exec -T postgres postgres --version | sed -E 's/.* ([0-9]+)\..*/\1/')
client=$(in_backup 'pg_dump --version' | sed -E 's/.* ([0-9]+)\..*/\1/')
# pg_dump reads older servers but refuses a newer one: the 18 client serves the bundled 16 and a shared server up to 18.
[ "$client" -ge "$server" ] || fail "pg_dump $client is older than the server $server"
echo "ok: pg_dump $client is not older than the server $server"

backup backup-now
backup status >/dev/null || fail "unhealthy after a good run: $(backup status)"
# On Linux the folder is the host user's and the service root without DAC_OVERRIDE could not write it (CI, 1F2).
[ -z "$(find backups ! -user "$(id -u)" -print -quit)" ] || fail "the local repository holds files the host user does not own"
echo "ok: the local repository stays the host user's"
[ "$(snapshots)" -ge 1 ] || fail "no snapshot"
in_backup "$R dump latest /tmp/stage/manifest.json | jq -e '
  .format == 1 and .row_counts[\"core.person\"] >= 2 and (.migrations | length) > 0 and .dataprotection_keys >= 1'" \
  >/dev/null || fail "manifest: $(in_backup "$R dump latest /tmp/stage/manifest.json")"
# Captured, not piped into grep -q: with pipefail, grep closing the pipe early would fail the producer.
listing=$(in_backup "$R ls latest")
grep -qx '/source/deploy/secrets/csrf_key' <<<"$listing" || fail "deploy/secrets/ not in the snapshot"
if grep -q 'restic_password' <<<"$listing"; then fail "the restic password is in the snapshot"; fi
if grep -q '^/pocket-id' <<<"$listing"; then fail "Pocket ID's live base is in the snapshot"; fi
grep -q 'pocket-id.sqlite3' <<<"$listing" || fail "Pocket ID's hot copy is not in the snapshot"
echo "ok: a backup holds the database, Pocket ID, the keys, the assets and deploy/, not the restic password"

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

# Review Focus 4 (final review): a bad schedule keeps the deployed service up and unhealthy with the reason; it does
# not restart forever under restart: unless-stopped.
NID_BACKUP_AT=25:00 "${COMPOSE[@]}" up -d backup
sleep 10
state=$(docker inspect --format '{{.RestartCount}} {{.State.Running}}' "$("${COMPOSE[@]}" ps -q backup)")
[ "$state" = "0 true" ] || fail "a bad NID_BACKUP_AT made the service restart (restarts, running: $state)"
health=$(backup status || true)
[[ $health == *"NID_BACKUP_AT invalide"* ]] || fail "status with a bad NID_BACKUP_AT: $health"
"${COMPOSE[@]}" up -d --wait --wait-timeout 120 backup
echo "ok: a bad NID_BACKUP_AT leaves the service up, unhealthy, with the reason"

# Final review: compose never creates a missing deploy/backup-secrets/ (as root, which setup.sh could not fix).
mv backup-secrets backup-secrets.keep
if "${COMPOSE[@]}" up -d --force-recreate backup 2>/tmp/nid-missing.log; then
  rm -rf backup-secrets; mv backup-secrets.keep backup-secrets; fail "up started backup without deploy/backup-secrets/"
fi
[ ! -e backup-secrets ] || { mv backup-secrets.keep backup-secrets.keep2; fail "compose created deploy/backup-secrets/"; }
mv backup-secrets.keep backup-secrets
"${COMPOSE[@]}" up -d --wait --wait-timeout 120 backup
echo "ok: a missing deploy/backup-secrets/ stops up instead of being created"

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

# Final review: restore-files and check-keys on scratch volumes, which are Linux filesystems like a CI runner's (a bind
# mount on macOS shows root's files as the user's). deploy/'s folders go to the host user, so --verify can remove its
# work folder; a key nid-api adds after a restore is fine, a key of the backup gone is not.
scratch() {
  docker run --rm --network none -v "$PWD/backups":/repo -v "$PWD/backup-secrets":/run/backup-secrets:ro \
    -v nid-scratch-keys:/target/volumes/dataprotection-keys -v nid-scratch-assets:/target/volumes/assets \
    -v nid-scratch-pocket-id:/target/volumes/pocket-id -v nid-scratch-deploy:/target/deploy \
    -e NID_HOST_UID=4242 -e NID_HOST_GID=4242 "$@"
}
id=$(scratch nid-backup:local snapshot-id)
scratch nid-backup:local restore-files "$id"
owners=$(scratch --entrypoint stat nid-backup:local -c '%u' /target/deploy/secrets /target/deploy/.env | sort -u)
[ "$owners" = 4242 ] || fail "deploy/ files not given to the host user: $owners"
scratch --entrypoint touch nid-backup:local /target/volumes/dataprotection-keys/key-00000000-0000-0000-0000-000000000000.xml
scratch nid-backup:local check-keys "$id" >/dev/null || fail "a key added after the restore failed the check"
scratch --entrypoint sh nid-backup:local -c \
  'rm "$(ls /target/volumes/dataprotection-keys/key-*.xml | grep -v key-00000000 | head -1)"'
if scratch nid-backup:local check-keys "$id" >/dev/null 2>&1; then fail "a key of the backup went missing unnoticed"; fi
docker volume rm nid-scratch-keys nid-scratch-assets nid-scratch-pocket-id nid-scratch-deploy >/dev/null
echo "ok: restored deploy/ folders belong to the host user; the key check wants the backup's keys, not an exact count"

# The guard: the running installation holds data.
if ./restore.sh </dev/null 2>/tmp/nid-guard.log; then fail "restore.sh replaced a running installation"; fi
grep -q -- "--force" /tmp/nid-guard.log || fail "the guard does not say how to go on"
echo "ok: restore.sh refuses an installation that holds data"

# The simulated disaster: a session and a backup, then nothing left but backup-secrets/ and backups/.
playwright e2e/backup.smoke.ts --grep "before"
backup backup-now
"${REMOTE[@]}" down -v --remove-orphans
rm -rf secrets .env
./restore.sh
"${COMPOSE[@]}" up -d --wait --wait-timeout 180
api_ready
playwright e2e/backup.smoke.ts --grep "after"
echo "ok: after a disaster, a backup restores into a new stack with its sessions and data"

# Review Focus 3: owners and modes, and deploy/ still the host user's.
[ "$(docker run --rm -v nid_dataprotection-keys:/k busybox:1.37 stat -c '%u %a' /k)" = "1654 700" ] || fail "keys folder"
[ "$(docker run --rm -v nid_pocket-id:/a busybox:1.37 stat -c '%u' /a/pocket-id.db)" = "65532" ] || fail "Pocket ID base"
[ "$(stat -c '%a' secrets 2>/dev/null || stat -f '%Lp' secrets)" = "700" ] || fail "deploy/secrets/ mode"
[ -O secrets/csrf_key ] && [ -O .env ] || fail "deploy/ files not owned by the host user"
# Final review: the backup settings the README puts in .env survive a rerun of setup.sh.
echo "NID_BACKUP_MAX_AGE=26" >> .env
./setup.sh --nid-host nid.localtest.me --auth-host auth.localtest.me | tee /tmp/nid-setup.log
if grep -q "créé" /tmp/nid-setup.log; then fail "setup.sh created a secret on a restored deploy/"; fi
grep -qx "NID_BACKUP_MAX_AGE=26" .env || fail "setup.sh erased the backup settings of .env"
grep -qx "NID_HOST=nid.localtest.me" .env || fail "setup.sh lost NID_HOST"
echo "ok: restored owners and modes; setup.sh reruns on the restored deploy/ without creating anything"

# Final review (Critical): --force with a backup that cannot be read must stop before it deletes anything.
mv backup-secrets/restic_password backup-secrets/restic_password.keep
echo wrong > backup-secrets/restic_password
if printf 'RESTAURER\n' | ./restore.sh --force 2>/tmp/nid-force.log; then
  mv backup-secrets/restic_password.keep backup-secrets/restic_password; fail "--force restored with a wrong password"
fi
mv backup-secrets/restic_password.keep backup-secrets/restic_password
docker volume inspect nid_pgdata >/dev/null 2>&1 || fail "--force deleted the data before checking the backup"
[ -n "$("${COMPOSE[@]}" ps -q postgres)" ] || fail "--force stopped the stack before checking the backup"
echo "ok: --force checks the backup before it deletes anything"
