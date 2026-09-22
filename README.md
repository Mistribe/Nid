# Nid

## Développement

Prérequis : SDK .NET 10 (voir `global.json`), Docker, `openssl`.

```bash
./deploy/dev/setup.sh                                   # secrets et configuration Authelia jetables
docker compose -f deploy/dev/compose.yml up -d          # PostgreSQL, Authelia, proxy TLS
./deploy/dev/run-host.sh                                # API sur :5080, migrations au démarrage
./deploy/dev/smoke.sh alice                             # connexion OIDC réelle et appels d'API
dotnet test Nid.slnx                                    # tests unitaires et d'intégration (Docker requis)
```

L'application se joint sur `https://nid.localtest.me` (Authelia sur `https://auth.localtest.me`).
