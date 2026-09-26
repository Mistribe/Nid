#!/usr/bin/env bash
# Restores a Nid backup (1F2 design §6).
#   deploy/restore.sh [--from local|remote] [--snapshot ID] [--force]   for real, on an installation without data
#   deploy/restore.sh --verify [--from local|remote] [--snapshot ID]    into a throwaway project, touching nothing
#   --compose-file FILE (absolute path, repeatable): one more compose file for the restore, e.g. extra_hosts for a remote
set -euo pipefail
cd "$(dirname "$0")"
DEPLOY=$PWD
VERIFY=0; FROM=local; SNAPSHOT=latest; FORCE=0; EXTRA=()
while [ $# -gt 0 ]; do
  case "$1" in
    --verify) VERIFY=1; shift ;;
    --compose-file) EXTRA+=(-f "$2"); shift 2 ;;
    --from) FROM=$2; shift 2 ;;
    --snapshot) SNAPSHOT=$2; shift 2 ;;
    --force) FORCE=1; shift ;;
    *) echo "Option inconnue : $1" >&2; exit 2 ;;
  esac
done
die() { echo "$*" >&2; exit 1; }
ok() { echo "ok: $*"; }
case $FROM in local | remote) ;; *) die "--from attend local ou remote" ;; esac
[ -s backup-secrets/restic_password ] ||
  die "deploy/backup-secrets/restic_password manque : remettez-y le mot de passe de la sauvegarde."
[ "$FROM" = local ] || [ -s backup-secrets/remote_repository ] ||
  die "deploy/backup-secrets/remote_repository manque : l'URL du dépôt distant."

# Missing on a new machine until the restore puts it back: not an error (pipefail would make it one).
from_env() { if [ -f .env ]; then sed -n "s/^$1=//p" .env | tail -1; fi; }
NID_BACKUP_DIR=${NID_BACKUP_DIR:-$(from_env NID_BACKUP_DIR)}
NID_BACKUP_DIR=${NID_BACKUP_DIR:-backups}
case $NID_BACKUP_DIR in /*) ;; *) NID_BACKUP_DIR=$DEPLOY/$NID_BACKUP_DIR ;; esac
export NID_BACKUP_DIR NID_RESTORE_FROM=$FROM NID_HOST_UID NID_HOST_GID
NID_HOST_UID=$(id -u); NID_HOST_GID=$(id -g)

PROJECT=(); WORK=""
if [ "$VERIFY" = 1 ]; then
  for image in "${NID_API_IMAGE:-nid-api:local}" "${NID_WEB_IMAGE:-nid-web:local}" "${NID_BACKUP_IMAGE:-nid-backup:local}"; do
    docker image inspect "$image" >/dev/null 2>&1 ||
      die "image $image absente : construisez d'abord la pile (docker compose -f deploy/compose.yaml build)."
  done
  WORK=$(mktemp -d "${TMPDIR:-/tmp}/nid-restore.XXXXXX")
  mkdir -m 700 "$WORK/backup-secrets"
  cp backup-secrets/* "$WORK/backup-secrets/"
  cp compose.yaml compose.verify.yaml compose.backup-remote.yaml "$WORK/"
  PROJECT=(-p nid-restore-test --project-directory "$WORK")
  FILES=(-f "$WORK/compose.yaml" -f "$WORK/compose.verify.yaml")
  [ "$FROM" = local ] || FILES+=(-f "$WORK/compose.backup-remote.yaml")
else
  FILES=(-f compose.yaml)
  [ "$FROM" = local ] || FILES+=(-f compose.backup-remote.yaml)
fi
FILES+=(${EXTRA[@]+"${EXTRA[@]}"})

# Compose on the target project; until .env is back, placeholder host names let the file interpolate.
c() {
  if [ -f "${WORK:-$DEPLOY}/.env" ]; then
    docker compose ${PROJECT[@]+"${PROJECT[@]}"} "${FILES[@]}" "$@"
  else
    NID_HOST=restore.invalid AUTH_HOST=restore.invalid docker compose ${PROJECT[@]+"${PROJECT[@]}"} "${FILES[@]}" "$@"
  fi
}
tool() { c run --rm --no-deps -T backup-restore "$@"; }

HAS_DATA=0
if [ "$VERIFY" = 0 ]; then
  if docker volume inspect nid_pgdata >/dev/null 2>&1 || [ -n "$(ls -A secrets 2>/dev/null)" ]; then HAS_DATA=1; fi
  [ "$HAS_DATA" = 0 ] || [ "$FORCE" = 1 ] || die "Cette installation contient déjà des données (volume nid_pgdata ou deploy/secrets/).
Pour tout remplacer par la sauvegarde : deploy/restore.sh --force. Pour l'essayer sans risque : deploy/restore.sh --verify."
  # A remote-only restore on a new machine: the local repository folder the compose file mounts must exist.
  [ "$FROM" = local ] || mkdir -p "$NID_BACKUP_DIR"
else
  # Whatever happens, the throwaway project and its copy of the secrets go.
  cleanup() { c down -v --remove-orphans >/dev/null 2>&1 || true; rm -rf "$WORK"; }
  trap cleanup EXIT
  cleanup_first() { c down -v --remove-orphans >/dev/null 2>&1 || true; }
  cleanup_first
fi

id=$(tool snapshot-id "$SNAPSHOT")
if [ "$FROM" = local ]; then where=local; else where=distant; fi
ok "instantané $id (dépôt $where)"

# Captured first: a failing read must stop the restore, not look like a backup without migrations.
migrations=$(tool migrations "$id") || die "impossible de lire le manifeste de l'instantané $id"
[ -n "$migrations" ] || die "l'instantané $id ne liste aucune migration : ce n'est pas une sauvegarde de Nid lisible"
missing=0
for migration in $migrations; do
  if ! ls ../src/*/Persistence/Migrations/"$migration".cs >/dev/null 2>&1; then
    echo "migration inconnue de ce code : $migration" >&2; missing=1
  fi
done
[ "$missing" = 0 ] || die "Cette sauvegarde vient d'une version plus récente de Nid : mettez le dépôt à jour d'abord (git pull)."
ok "le code connaît toutes les migrations de la sauvegarde"

# Only now that the backup is known to open and to fit this code: the installation it replaces goes.
if [ "$HAS_DATA" = 1 ]; then
  read -r -p "Tout remplacer par la sauvegarde $id ? Tapez RESTAURER : " answer || answer=""
  [ "$answer" = RESTAURER ] || die "Abandon : rien n'a été modifié."
  c down -v --remove-orphans
fi

tool restore-files "$id"
ok "volumes et fichiers de deploy/ restaurés"
c up -d --wait --wait-timeout 120 postgres
tool restore-database "$id"
ok "base rechargée"
ok "$(tool check-database "$id")"

c up -d --wait --wait-timeout 300
[ "$(c exec -T nid-web wget -qO- http://nid-api:8080/health/ready)" = Healthy ] || die "nid-api n'est pas prêt"
ok "nid-api est prêt"
[ "$(docker inspect --format '{{.State.Health.Status}}' "$(c ps -q authelia)")" = healthy ] ||
  die "Authelia ne lit pas sa base restaurée"
ok "Authelia lit sa base restaurée"
ok "$(tool check-keys "$id")"

if [ "$VERIFY" = 1 ]; then echo "Sauvegarde $id vérifiée : elle se restaure."; else echo "Restauration de $id terminée."; fi
