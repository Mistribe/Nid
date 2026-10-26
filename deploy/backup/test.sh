#!/bin/sh
# Unit tests of lib.sh (1F2 design §8), run inside the image:
#   docker run --rm --entrypoint nid-backup-test nid-backup:local
. /usr/local/lib/nid-backup/lib.sh
failures=0
check() { # name, expected, actual
  if [ "$2" = "$3" ]; then echo "ok: $1"; else echo "FAIL: $1: expected [$2], got [$3]"; failures=$((failures + 1)); fi
}
succeeds() { if "$@" >/dev/null 2>&1; then echo yes; else echo no; fi; }
at() { TZ=$1 date -d "$2" +%s; } # zone, "YYYY-MM-DD hh:mm" → epoch

check "valid time 03:30" yes "$(succeeds valid_time 03:30)"
check "valid time 23:59" yes "$(succeeds valid_time 23:59)"
check "valid time 00:00" yes "$(succeeds valid_time 00:00)"
check "invalid time 24:00" no "$(succeeds valid_time 24:00)"
check "invalid time 3:30" no "$(succeeds valid_time 3:30)"
check "invalid time 25:00" no "$(succeeds valid_time 25:00)"
check "invalid time empty" no "$(succeeds valid_time '')"

check "next run later today" 1800 "$(TZ=UTC seconds_until 03:30 "$(at UTC '2026-09-26 03:00')")"
check "next run at the very time is tomorrow" 86400 "$(TZ=UTC seconds_until 03:30 "$(at UTC '2026-09-26 03:30')")"
check "next run after the time is tomorrow" 84600 "$(TZ=UTC seconds_until 03:30 "$(at UTC '2026-09-26 04:00')")"
# Europe/Paris: 2026-10-25 has 25 hours, 2026-03-29 has 23.
check "next run across the autumn change" 88200 "$(TZ=Europe/Paris seconds_until 03:30 "$(at Europe/Paris '2026-10-24 04:00')")"
check "next run across the spring change" 81000 "$(TZ=Europe/Paris seconds_until 03:30 "$(at Europe/Paris '2026-03-28 04:00')")"
check "next run on the 25-hour day itself" 89700 "$(TZ=Europe/Paris seconds_until 00:05 "$(at Europe/Paris '2026-10-25 00:10')")"
check "no next run for an invalid time" no "$(succeeds seconds_until 25:00 0)"

check "restic time in UTC" 1790393400 "$(iso_to_epoch 2026-09-26T03:30:00Z)"
check "restic time with an offset and nanoseconds" 1790393400 "$(iso_to_epoch 2026-09-26T05:30:00.123456789+02:00)"
check "restic time with a negative offset" 1790393400 "$(iso_to_epoch 2026-09-25T22:30:00-05:00)"

data='COPY core.person (id, name) FROM stdin;
1	Alice
2	Bob\nsur deux lignes
\.

COPY subscriptions.subscription (id) FROM stdin;
\.

COPY core.__ef_migrations_history ("MigrationId", "ProductVersion") FROM stdin;
20260922100729_InitialCore	10.0.0
20260924085507_AdminArea	10.0.0
\.

COPY subscriptions.__ef_migrations_history ("MigrationId", "ProductVersion") FROM stdin;
20260922171441_InitialSubscriptions	10.0.0
\.'
check "rows per table, empty table included" "core.__ef_migrations_history 2
core.person 2
subscriptions.__ef_migrations_history 1
subscriptions.subscription 0" "$(printf '%s\n' "$data" | count_rows)"
check "migrations of every schema" "20260922100729_InitialCore
20260922171441_InitialSubscriptions
20260924085507_AdminArea" "$(printf '%s\n' "$data" | dump_migrations)"
check "counts as JSON" '{"core.person":2,"subscriptions.subscription":0}' \
  "$(printf 'core.person 2\nsubscriptions.subscription 0\n' | counts_json | jq -c .)"
check "counts of nothing" '{}' "$(printf '' | counts_json | jq -c .)"

echo '{"core.person":2,"subscriptions.subscription":0}' > /tmp/expected.json
echo '{"core.person":2,"subscriptions.subscription":0}' > /tmp/same.json
echo '{"core.person":3,"core.extra":1}' > /tmp/other.json
check "same counts" yes "$(succeeds compare_counts /tmp/expected.json /tmp/same.json)"
check "different counts" no "$(succeeds compare_counts /tmp/expected.json /tmp/other.json)"
check "differences named" "core.extra : attendu absente, trouvé 1
core.person : attendu 2, trouvé 3
subscriptions.subscription : attendu 0, trouvé absente" "$(compare_counts /tmp/expected.json /tmp/other.json)"

now=1790393400
state() { echo "$1" > /tmp/status.json; }
state '{"last_result":"ok","remote_result":"none","last_snapshot":"a1","last_success_at":1790390000}'
check "healthy after a recent success" yes "$(succeeds health_check /tmp/status.json $now 26 no $((now - 999999)))"
state '{"last_result":"database","remote_result":"none","last_snapshot":"a1","last_success_at":1790390000}'
check "unhealthy after a failed run" no "$(succeeds health_check /tmp/status.json $now 26 no 0)"
check "the failed step is named" "la dernière sauvegarde a échoué (étape : database)" "$(health_check /tmp/status.json $now 26 no 0)"
state '{"last_result":"ok","remote_result":"none","last_snapshot":"a1","last_success_at":'$((now - 27 * 3600))'}'
check "unhealthy when the last success is too old" no "$(succeeds health_check /tmp/status.json $now 26 no 0)"
check "the age is named" "aucune sauvegarde réussie depuis plus de 26 h" "$(health_check /tmp/status.json $now 26 no 0)"
state '{"last_result":"none","remote_result":"none","last_snapshot":"","last_success_at":null}'
check "healthy while a new installation waits for its first run" yes "$(succeeds health_check /tmp/status.json $now 26 no $((now - 3600)))"
check "unhealthy when the first run never came" no "$(succeeds health_check /tmp/status.json $now 26 no $((now - 27 * 3600)))"
state '{"last_result":"ok","remote_result":"unreachable","last_snapshot":"a1","last_success_at":1790390000}'
check "unhealthy when the configured remote failed" no "$(succeeds health_check /tmp/status.json $now 26 yes 0)"
check "the remote result is named" "dépôt distant : unreachable" "$(health_check /tmp/status.json $now 26 yes 0)"
state '{"last_result":"ok","remote_result":"none","last_snapshot":"a1","last_success_at":1790390000}'
check "healthy without a remote" yes "$(succeeds health_check /tmp/status.json $now 26 no 0)"
state '{"last_result":"none","remote_result":"failed","last_snapshot":"a1","last_success_at":1790390000}'
check "unhealthy after a restart while the remote still fails" no "$(succeeds health_check /tmp/status.json $now 26 yes 0)"
state '{"last_result":"none","remote_result":"none","last_snapshot":"a1","last_success_at":1790390000}'
check "healthy after a restart with a remote not yet tried" yes "$(succeeds health_check /tmp/status.json $now 26 yes 0)"

check "parts with Pocket ID embedded" '["database","pocket-id","dataprotection-keys","assets","deploy"]' "$(backup_parts embedded)"
check "parts with Pocket ID external" '["database","dataprotection-keys","assets","deploy"]' "$(backup_parts external)"
check "parts refuse a mode they do not know" no "$(succeeds backup_parts extern)"
check "pgpass line of the bundled database" "postgres:5432:nid:nid:s3cret" "$(pgpass_line postgres s3cret)"
check "pgpass line of an external database" "postgres-server:5432:nid:nid:s3cret" "$(pgpass_line postgres-server s3cret)"

check "a restart keeps a failed step" database "$(kept_result database)"
check "a restart clears a success" none "$(kept_result ok)"
check "a restart keeps none" none "$(kept_result none)"
check "a restart clears a configuration error" none "$(kept_result configuration)"
check "a first start has no result" none "$(kept_result '')"

check "a restart keeps a failed remote" failed "$(kept_remote failed yes)"
check "a restart keeps an unreachable remote" unreachable "$(kept_remote unreachable yes)"
check "a restart clears a remote success" none "$(kept_remote ok yes)"
check "a first start has no remote result" none "$(kept_remote '' yes)"
check "a removed remote leaves no stale failure" none "$(kept_remote failed no)"

STATE_DIR=/tmp/nid-state
STATE=$STATE_DIR/status.json
rm -rf "$STATE_DIR"
mark_failed configuration
check "a configuration error with no status yet" \
  '{"last_result":"configuration","remote_result":"none","last_snapshot":"","last_success_at":null}' "$(jq -c . "$STATE")"
write_status ok ok a1 1790390000
mark_failed configuration
check "a configuration error keeps the last success" \
  '{"last_result":"configuration","remote_result":"ok","last_snapshot":"a1","last_success_at":1790390000}' "$(jq -c . "$STATE")"
check "a configuration error is unhealthy" no "$(succeeds health_check "$STATE" $now 26 no 0)"

if [ "$failures" -gt 0 ]; then echo "$failures failure(s)"; exit 1; fi
echo "all lib.sh tests passed"
