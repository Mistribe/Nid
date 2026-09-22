#!/usr/bin/env bash
# Generates the throwaway development secrets and Authelia configuration (nothing here is a real secret).
# Needs docker and openssl. Safe to rerun: it overwrites the generated files.
set -euo pipefail
cd "$(dirname "$0")/authelia"

USER_PASSWORD='Nid-Dev-User-1!'
CLIENT_SECRET='nid-dev-client-secret'
AUTHELIA='docker run --rm authelia/authelia:4.39 authelia'

openssl genrsa -out jwks.pem 2048 2>/dev/null
USER_HASH=$($AUTHELIA crypto hash generate argon2 --password "$USER_PASSWORD" | sed 's/^Digest: //')
CLIENT_HASH=$($AUTHELIA crypto hash generate pbkdf2 --variant sha512 --password "$CLIENT_SECRET" | sed 's/^Digest: //')

cat > users.yml <<YAML
users:
  alice:
    displayname: "Alice Dev"
    password: "$USER_HASH"
    email: alice@example.test
    groups: []
  bob:
    displayname: "Bob Dev"
    password: "$USER_HASH"
    email: bob@example.test
    groups: []
  carol:
    displayname: "Carol Dev"
    password: "$USER_HASH"
    email: carol@example.test
    groups: []
YAML

cat > configuration.yml <<YAML
server:
  address: 'tcp://:9091'
log:
  level: info
identity_validation:
  reset_password:
    jwt_secret: 'dev-reset-secret-0123456789abcdef0123456789'
authentication_backend:
  file:
    path: /config/users.yml
access_control:
  default_policy: one_factor
session:
  secret: 'dev-session-secret-0123456789abcdef0123456789'
  cookies:
    - domain: 'localtest.me'
      authelia_url: 'https://auth.localtest.me'
      default_redirection_url: 'https://nid.localtest.me'
storage:
  encryption_key: 'dev-storage-key-0123456789abcdef0123456789'
  local:
    path: /config/db.sqlite3
notifier:
  filesystem:
    filename: /config/notification.txt
identity_providers:
  oidc:
    hmac_secret: 'dev-oidc-hmac-0123456789abcdef0123456789'
    jwks:
      - key: {{ secret "/config/jwks.pem" | mindent 10 "|" | msquote }}
    clients:
      - client_id: 'nid'
        client_name: 'Nid'
        client_secret: '$CLIENT_HASH'
        public: false
        authorization_policy: 'one_factor'
        require_pkce: true
        pkce_challenge_method: 'S256'
        consent_mode: 'implicit'
        redirect_uris: ['https://nid.localtest.me/auth/callback']
        scopes: ['openid', 'profile', 'email']
        response_types: ['code']
        grant_types: ['authorization_code']
        # ASP.NET Core sends the secret in the request body, not as HTTP Basic (docs/spikes.md section 8).
        token_endpoint_auth_method: 'client_secret_post'
YAML

echo "Dev users: alice, bob, carol / $USER_PASSWORD"
echo "OIDC client: nid / $CLIENT_SECRET"
