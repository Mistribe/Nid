#!/usr/bin/env bash
# Generates Nid's secrets and .env (1F1 design §4, then the move to Pocket ID). Safe to rerun: an existing secret is kept.
# Usage: deploy/setup.sh [--nid-host H] [--auth-host H] [--oidc-client-id ID] [--oidc-client-secret S]
#   --auth-host            the identity provider's host name (Pocket ID)
#   --oidc-client-id       the id of Nid's client in Pocket ID (default nid); written to .env
#   --oidc-client-secret   the secret Pocket ID showed once when the client was created; written to
#                          deploy/secrets/oidc_client_secret (until then a placeholder stands there)
set -euo pipefail
cd "$(dirname "$0")"

NID_HOST=""; AUTH_HOST=""; CLIENT_ID=""; CLIENT_SECRET=""
while [ $# -gt 0 ]; do
  case "$1" in
    --nid-host) NID_HOST=$2; shift 2 ;;
    --auth-host) AUTH_HOST=$2; shift 2 ;;
    --oidc-client-id) CLIENT_ID=$2; shift 2 ;;
    --oidc-client-secret) CLIENT_SECRET=$2; shift 2 ;;
    *) echo "Option inconnue : $1" >&2; exit 2 ;;
  esac
done

ask() { local var=$1 prompt=$2; if [ -z "${!var}" ]; then read -r -p "$prompt : " "$var"; fi; }
ask NID_HOST "Nom d'hôte de Nid (ex. nid.example.org)"
ask AUTH_HOST "Nom d'hôte de Pocket ID (ex. auth.example.org)"
if [ "$NID_HOST" = "$AUTH_HOST" ]; then
  echo "Nid et Pocket ID ont besoin de deux noms d'hôte différents." >&2
  exit 2
fi

# deploy/secrets/ is closed (0700); its files are readable (0644) because compose bind-mounts them as they are, and the
# containers read them as their own non-root users (postgres 70, nid 1654, pocket-id 65532).
mkdir -p secrets
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
secret csrf_key hex
# Pocket ID reads the key as it is: a line terminator would be part of it.
secret pocket_id_encryption_key "openssl rand -base64 32 | tr -d '\n'"
secret nid_connection_string "printf 'Host=postgres;Database=nid;Username=nid;Password=%s' \"\$(cat secrets/postgres_password)\""

# Pocket ID chooses the client secret and shows it once, after it is up: compose needs the file now, so a placeholder
# stands in until the hoster passes the real one with --oidc-client-secret.
PLACEHOLDER='set-with-setup.sh---oidc-client-secret'
if [ -n "$CLIENT_SECRET" ]; then
  printf '%s' "$CLIENT_SECRET" > secrets/oidc_client_secret
  echo "mis à jour : secrets/oidc_client_secret"
elif [ ! -s secrets/oidc_client_secret ]; then
  printf '%s' "$PLACEHOLDER" > secrets/oidc_client_secret
  echo "créé : secrets/oidc_client_secret (provisoire)"
fi
chmod 644 secrets/oidc_client_secret

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

# Only its own keys: the backup settings the hoster adds to .env (README §6) survive a rerun.
env_set() { # key, value
  if [ -f .env ] && grep -q "^$1=" .env; then
    sed "s|^$1=.*|$1=$2|" .env > .env.new && mv .env.new .env
  else
    echo "$1=$2" >> .env
  fi
}
env_set NID_HOST "$NID_HOST"
env_set AUTH_HOST "$AUTH_HOST"
[ -z "$CLIENT_ID" ] || env_set NID_OIDC_CLIENT_ID "$CLIENT_ID"

if [ "$(cat secrets/oidc_client_secret)" = "$PLACEHOLDER" ]; then
  cat <<EOF
Prêt à démarrer Pocket ID, pas encore Nid :
  1. docker compose -f deploy/compose.yaml up -d pocket-id
  2. Faites pointer https://$AUTH_HOST vers pocket-id:1411, puis ouvrez https://$AUTH_HOST/setup tout de suite : la
     première personne qui y arrive crée l'administrateur (et sa passkey).
  3. Dans Pocket ID : groupes nid-users et nid-admins, puis un client OIDC « Nid » (redirection
     https://$NID_HOST/auth/callback, déconnexion https://$NID_HOST/signed-out, consentement ignoré, réservé au groupe nid-users).
  4. deploy/setup.sh --oidc-client-id <identifiant du client> --oidc-client-secret <secret>
  5. docker compose -f deploy/compose.yaml up -d --build
EOF
else
  echo "Prêt : docker compose -f deploy/compose.yaml up -d --build"
fi
