# Nid

## Développement

Prérequis : SDK .NET 10 (voir `global.json`), CLI Aspire, Docker, Node.

```bash
aspire run                           # PostgreSQL, Pocket ID, API, frontend Vite, proxy TLS + tableau de bord
pnpm --dir web e2e                   # parcours Playwright, connectés par l'API de Pocket ID
dotnet test Nid.slnx                 # tests unitaires et d'intégration (Docker requis)
```

L'application se joint sur `https://nid.localtest.me` (Pocket ID sur `https://auth.localtest.me`). Au premier démarrage,
un processus jetable (`pocket-id-bootstrap`) y crée les groupes `nid-users` et `nid-admins`, les trois utilisateurs de développement
(`alice` administratrice, `bob`, `carol` sans accès) et le client de Nid ; ils se connectent par passkey ou, pour les tests,
par un jeton à usage unique (`web/e2e/session.ts`).
Journaux, traces et métriques : tableau de bord Aspire (URL affichée par `aspire run`).
L'AppHost ne sert qu'au développement ; le déploiement est le compose de référence (`docs/architecture.md` §10).

Pour repartir d'une base vide : `docker volume rm nid-dev-pgdata nid-dev-pocket-id`.

### Frontend (`web/`)

Prérequis : Node ≥ 24 et pnpm 12 (`corepack enable`, ou installé à part).

```bash
pnpm --dir web install               # dépendances, versions exactes du lockfile
pnpm --dir web check                 # typage, lint, tests, build et budget du bundle
```

`dotnet build` réécrit `web/src/api/openapi.json` ; lancer ensuite `pnpm --dir web gen:api` pour régénérer
`web/src/api/schema.d.ts`, et versionner les deux avant de committer (la CI le vérifie avec `pnpm --dir web check:api`).
