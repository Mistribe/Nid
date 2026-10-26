# Helpers of nid-backup (1F2 design §4 to §6): no I/O but their arguments, stdin and stdout, except the status
# helpers at the end, which read and write $STATE in $STATE_DIR. Sourced by nid-backup, tested by nid-backup-test
# (test.sh). POSIX shell on busybox, jq for JSON.

# valid_time HH:MM: a time of day, 00:00 to 23:59.
valid_time() {
  case $1 in
    [01][0-9]:[0-5][0-9] | 2[0-3]:[0-5][0-9]) return 0 ;;
    *) return 1 ;;
  esac
}

# seconds_until HH:MM NOW: seconds from NOW (epoch) to the next HH:MM of the wall clock in $TZ, strictly later. The
# next day is found from today's noon, never by adding 24 hours, so a 23- or 25-hour day keeps the wall-clock time.
seconds_until() {
  valid_time "$1" || return 1
  today=$(date -d "@$2" +%Y-%m-%d)
  target=$(date -d "$today $1" +%s)
  if [ "$target" -le "$2" ]; then
    tomorrow=$(date -d "@$(( $(date -d "$today 12:00" +%s) + 86400 ))" +%Y-%m-%d)
    target=$(date -d "$tomorrow $1" +%s)
  fi
  echo $(( target - $2 ))
}

# iso_to_epoch TIME: restic's JSON time (RFC 3339, nanoseconds and offset optional) to an epoch.
iso_to_epoch() {
  jq -rn --arg t "$1" '
    $t | capture("^(?<d>[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2})(\\.[0-9]+)?(?<z>Z|[+-][0-9]{2}:[0-9]{2})$")
    | (.d + "Z" | fromdateiso8601)
      - (if .z == "Z" then 0
         else (.z[0:1] + "1" | tonumber) * ((.z[1:3] | tonumber) * 3600 + (.z[4:6] | tonumber) * 60) end)'
}

# count_rows: rows per table of a plain data dump (stdin), one "schema.table rows" line each. COPY escapes newlines,
# so each data line is one row; an empty table still has its COPY block.
count_rows() {
  awk '
    /^COPY .* FROM stdin;$/ { table = $2; rows = 0; copying = 1; next }
    copying && $0 == "\\." { print table, rows; copying = 0; next }
    copying { rows++ }
  ' | LC_ALL=C sort
}

# dump_migrations: the migration ids of every __ef_migrations_history table of a plain data dump (stdin).
dump_migrations() {
  awk -F '\t' '
    /^COPY .* FROM stdin;$/ { copying = index($0, "__ef_migrations_history") > 0; next }
    $0 == "\\." { copying = 0; next }
    copying { print $1 }
  ' | LC_ALL=C sort
}

# counts_json: count_rows lines (stdin) to {"schema.table": rows}.
counts_json() {
  jq -Rn '[inputs | select(length > 0) | split(" ") | {(.[0]): (.[1] | tonumber)}] | add // {}'
}

# compare_counts EXPECTED ACTUAL: two counts_json files; prints one line per differing table.
compare_counts() {
  differences=$(jq -rn --slurpfile expected "$1" --slurpfile actual "$2" '
    $expected[0] as $e | $actual[0] as $a
    | ([$e, $a] | map(keys) | add | unique)[] as $t
    | select($e[$t] != $a[$t])
    | "\($t) : attendu \($e[$t] // "absente"), trouvé \($a[$t] // "absente")"')
  if [ -n "$differences" ]; then echo "$differences"; return 1; fi
}

# backup_parts MODE: the parts a snapshot holds, as the manifest lists them. MODE is embedded (Pocket ID is in this
# compose, its base is part of the snapshot) or external (Pocket ID is somebody else's: its base is not). Anything else
# fails, so that a typo never drops Pocket ID from the backup unnoticed.
backup_parts() {
  case $1 in
    embedded) jq -cn '["database", "pocket-id", "dataprotection-keys", "assets", "deploy"]' ;;
    external) jq -cn '["database", "dataprotection-keys", "assets", "deploy"]' ;;
    *) return 1 ;;
  esac
}

# pgpass_line HOST PASSWORD: the .pgpass entry for nid@HOST/nid.
pgpass_line() { printf '%s:5432:nid:nid:%s\n' "$1" "$2"; }

# health_check STATUS NOW MAX_HOURS REMOTE STARTED: the healthcheck's decision (1F2 design §5). REMOTE is yes when a
# remote repository is configured; STARTED is when the service started, the grace period of a new installation.
health_check() {
  result=$(jq -r '.last_result' "$1")
  remote=$(jq -r '.remote_result' "$1")
  since=$(jq -r '.last_success_at // empty' "$1")
  if [ "$result" != ok ] && [ "$result" != none ]; then
    echo "la dernière sauvegarde a échoué (étape : $result)"; return 1
  fi
  if [ $(( $2 - ${since:-$5} )) -gt $(( $3 * 3600 )) ]; then
    echo "aucune sauvegarde réussie depuis plus de $3 h"; return 1
  fi
  # After a restart (result none) the remote is none, or a failure kept by kept_remote.
  if [ "$4" = yes ] && { [ "$result" = ok ] || [ "$remote" != none ]; } && [ "$remote" != ok ]; then
    echo "dépôt distant : $remote"; return 1
  fi
  echo ok
}

# kept_result PREVIOUS: the last_result a start keeps from the status it finds. A failed step stays failed until a backup
# succeeds, so a restart does not clear the alert; ok, none, nothing, and configuration (the start has just checked it) give none.
kept_result() {
  case ${1:-none} in
    ok | none | configuration) echo none ;;
    *) echo "$1" ;;
  esac
}

# kept_remote PREVIOUS CONFIGURED: the remote_result a start keeps. A failed or unreachable remote stays so while a remote
# is configured (yes), so a restart does not hide it until the next run; without a remote, none, so a removed one leaves no stale failure.
kept_remote() {
  case $2:${1:-none} in
    yes:failed | yes:unreachable) echo "$1" ;;
    *) echo none ;;
  esac
}

# --- the status file: $STATE in $STATE_DIR, set by the caller ---

write_status() { # result, remote result, snapshot, success epoch or empty
  mkdir -p "$STATE_DIR"
  jq -n --arg result "$1" --arg remote "$2" --arg snapshot "$3" --argjson success "${4:-null}" \
    '{last_result: $result, remote_result: $remote, last_snapshot: $snapshot, last_success_at: $success}' > "$STATE.new"
  mv "$STATE.new" "$STATE"
}
previous() { jq -r "$1 // empty" "$STATE" 2>/dev/null || true; }

# mark_failed RESULT: a run failed at step RESULT, or the configuration is invalid (RESULT=configuration). The previous
# remote result, snapshot and last success are kept: the age still counts from that success.
mark_failed() {
  remote=$(previous .remote_result)
  write_status "$1" "${remote:-none}" "$(previous .last_snapshot)" "$(previous .last_success_at)"
}
