#!/usr/bin/env bash
# Generates Nid's secrets and Authelia's configuration (1F1 design §4). Safe to rerun: an existing secret is kept, only the
# configuration derived from the answers is written again.
# Usage: deploy/setup.sh [--nid-host H] [--auth-host H] [--user login] [--name "Display"] [--email e] [--password p]
#                        [--extra-user login:Name:email]   (the last one is for tests only: same password as the first user)
set -euo pipefail
cd "$(dirname "$0")"

AUTHELIA_IMAGE='authelia/authelia:4.39@sha256:bd97cff4fcbf715b5ff1f9ae286afbe6033afce385302520b0368122d43a6f54'
NID_HOST=""; AUTH_HOST=""; LOGIN=""; NAME=""; EMAIL=""; PASSWORD=""; EXTRA=()
while [ $# -gt 0 ]; do
  case "$1" in
    --nid-host) NID_HOST=$2; shift 2 ;;
    --auth-host) AUTH_HOST=$2; shift 2 ;;
    --user) LOGIN=$2; shift 2 ;;
    --name) NAME=$2; shift 2 ;;
    --email) EMAIL=$2; shift 2 ;;
    --password) PASSWORD=$2; shift 2 ;;
    --extra-user) EXTRA+=("$2"); shift 2 ;;
    *) echo "Option inconnue : $1" >&2; exit 2 ;;
  esac
done

ask() { local var=$1 prompt=$2; if [ -z "${!var}" ]; then read -r -p "$prompt : " "$var"; fi; }
ask NID_HOST "Nom d'hôte de Nid (ex. nid.example.org)"
ask AUTH_HOST "Nom d'hôte d'Authelia (ex. auth.example.org)"
COOKIE_DOMAIN=${AUTH_HOST#*.}
if [ "${NID_HOST#*.}" != "$COOKIE_DOMAIN" ]; then
  echo "Les deux noms d'hôte doivent partager le même domaine parent (le cookie d'Authelia couvre ce domaine)." >&2
  exit 2
fi

authelia() { docker run --rm "$AUTHELIA_IMAGE" authelia "$@"; }

# deploy/secrets/ is closed (0700); its files are readable (0644) because compose bind-mounts them as they are, and the
# containers read them as their own non-root users (postgres 70, nid-api 1654, authelia 8000).
mkdir -p secrets authelia
chmod 700 secrets
secret() { # name, command printing the value: written once, never replaced
  if [ ! -s "secrets/$1" ]; then
    eval "$2" > "secrets/$1"
    echo "créé : secrets/$1"
  fi
  chmod 644 "secrets/$1"
}
hex() { openssl rand -hex 32 | tr -d '\n'; }
secret postgres_password hex
secret oidc_client_secret hex
secret csrf_key hex
secret authelia_session hex
secret authelia_storage hex
secret authelia_jwt hex
secret authelia_hmac hex
secret authelia_jwks "openssl genrsa 2048 2>/dev/null"
secret nid_connection_string "printf 'Host=postgres;Database=nid;Username=nid;Password=%s' \"\$(cat secrets/postgres_password)\""

# The backup's own password stays outside deploy/secrets/, which the backup holds (1F2 design §3).
mkdir -p backup-secrets backups
chmod 700 backup-secrets
if [ ! -s backup-secrets/restic_password ]; then
  hex > backup-secrets/restic_password
  chmod 600 backup-secrets/restic_password
  echo "créé : backup-secrets/restic_password"
  echo "Mot de passe de la sauvegarde : $(cat backup-secrets/restic_password)"
  echo "Notez-le hors de cette machine : sans lui, la sauvegarde est illisible."
fi

client_hash=$(authelia crypto hash generate pbkdf2 --variant sha512 --password "$(cat secrets/oidc_client_secret)" | sed 's/^Digest: //')
sed -e "s|@NID_HOST@|$NID_HOST|g" -e "s|@AUTH_HOST@|$AUTH_HOST|g" -e "s|@COOKIE_DOMAIN@|$COOKIE_DOMAIN|g" \
    -e "s|@CLIENT_SECRET_HASH@|$client_hash|g" authelia/configuration.template.yml > authelia/configuration.yml

# Users are created once. Adding one later means editing deploy/authelia/users.yml (deploy/README.md).
if [ ! -s authelia/users.yml ]; then
  ask LOGIN "Identifiant du premier utilisateur"
  ask NAME "Nom affiché"
  ask EMAIL "Email"
  if [ -z "$PASSWORD" ]; then read -r -s -p "Mot de passe : " PASSWORD; echo; fi
  user_block() { # login, name, email, password
    local hash; hash=$(authelia crypto hash generate argon2 --password "$4" | sed 's/^Digest: //')
    printf '  %s:\n    displayname: "%s"\n    password: "%s"\n    email: %s\n    groups: []\n' "$1" "$2" "$hash" "$3"
  }
  {
    echo "users:"
    user_block "$LOGIN" "$NAME" "$EMAIL" "$PASSWORD"
    for extra in "${EXTRA[@]+"${EXTRA[@]}"}"; do
      IFS=: read -r l n m <<<"$extra"
      user_block "$l" "$n" "$m" "$PASSWORD"
    done
  } > authelia/users.yml
  echo "créé : authelia/users.yml"
fi
# Read by Authelia's own user; no secret inside, only the client secret's hash and the users' password hashes.
chmod 644 authelia/configuration.yml authelia/users.yml

printf 'NID_HOST=%s\nAUTH_HOST=%s\n' "$NID_HOST" "$AUTH_HOST" > .env
echo "Prêt : docker compose -f deploy/compose.yaml up -d --build"
