# Nid

## Développement

Prérequis : SDK .NET 10 (voir `global.json`), CLI Aspire, Docker, Node.

```bash
aspire run                           # PostgreSQL, Pocket ID, API, frontend Vite, proxy TLS + tableau de bord
pnpm --dir web e2e                   # parcours Playwright, connectés par l'API de Pocket ID
dotnet test Nid.slnx -m:1            # tests unitaires et d'intégration (Docker requis), un projet à la fois
dotnet test tests/Nid.RealEstate.IntegrationTests   # l'intégration d'un seul module
```

L'application se joint sur `https://nid.localtest.me` (Pocket ID sur `https://auth.localtest.me`). Au premier démarrage,
un processus jetable (`pocket-id-bootstrap`) y crée les groupes `nid-users` et `nid-admins`, les trois utilisateurs de développement
(`alice` administratrice, `bob`, `carol` sans accès) et le client de Nid ; ils se connectent par passkey ou, pour les tests,
par un jeton à usage unique (`web/e2e/session.ts`).
Journaux, traces et métriques : tableau de bord Aspire (URL affichée par `aspire run`).
L'AppHost ne sert qu'au développement ; le déploiement est le compose de référence (`docs/architecture.md` §10).

Pour repartir d'une base vide : `docker volume rm nid-dev-pgdata nid-dev-pocket-id`.

### Données de démonstration

`deploy/local/demo/` remplit les écrans d'un foyer fictif (Alice, Bob et Léa) : abonnements, comptes et placements, deux biens et
trois crédits, revenus, objectifs. Les utilisateurs de développement n'ont pas de passkey : un lien à usage unique les connecte.

```bash
node deploy/local/demo/sign-in-link.mjs bob     # ouvrir le lien, puis https://nid.localtest.me : Bob existe dans Nid
node deploy/local/demo/sign-in-link.mjs alice   # dans une autre fenêtre privée : chaque lien sert une fois
```

Puis, connecté en Alice, menu Import : `deploy/local/demo/alice.nid.json` ; connecté en Bob, `deploy/local/demo/bob.nid.json` (ses
éléments à lui, facultatif). Bob doit s'être connecté une fois avant l'import d'Alice, qui partage des éléments avec lui. Les
fichiers sortent de `node deploy/local/demo/generate.mjs` ; `DemoFileTests` vérifie qu'ils s'importent sans erreur ni avertissement.

### Frontend (`web/`)

Prérequis : Node ≥ 24 et pnpm 12 (`corepack enable`, ou installé à part).

```bash
pnpm --dir web install               # dépendances, versions exactes du lockfile
pnpm --dir web check                 # typage, lint, tests, build et budget du bundle
```

`dotnet build` réécrit `web/src/api/openapi.json` ; lancer ensuite `pnpm --dir web gen:api` pour régénérer
`web/src/api/schema.d.ts`, et versionner les deux avant de committer (la CI le vérifie avec `pnpm --dir web check:api`).
