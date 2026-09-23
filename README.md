# Nid

## Développement

Prérequis : SDK .NET 10 (voir `global.json`), CLI Aspire, Docker, Node, `openssl`.

```bash
./deploy/dev/setup.sh                # secrets et configuration Authelia jetables (une fois)
aspire run                           # PostgreSQL, Authelia, API, frontend Vite, proxy TLS + tableau de bord
./deploy/dev/smoke.sh alice          # connexion OIDC réelle et appels d'API
dotnet test Nid.slnx                 # tests unitaires et d'intégration (Docker requis)
```

L'application se joint sur `https://nid.localtest.me` (Authelia sur `https://auth.localtest.me`).
Journaux, traces et métriques : tableau de bord Aspire (URL affichée par `aspire run`).
L'AppHost ne sert qu'au développement ; le déploiement est le compose de référence (`docs/architecture.md` §10).

Pour repartir d'une base vide : `docker volume rm nid-dev-pgdata`.

### Frontend (`web/`)

Prérequis : Node ≥ 24 et pnpm 11 (`corepack enable`, ou installé à part).

```bash
pnpm --dir web install               # dépendances, versions exactes du lockfile
pnpm --dir web check                 # typage, lint, tests, build et budget du bundle
```

`dotnet build` réécrit `web/src/api/openapi.json` ; lancer ensuite `pnpm --dir web gen:api` pour régénérer
`web/src/api/schema.d.ts`, et versionner les deux avant de committer (la CI le vérifie avec `pnpm --dir web check:api`).
