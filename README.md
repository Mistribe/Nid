# Nid

## Développement

Prérequis : SDK .NET 10 (voir `global.json`), CLI Aspire, Docker, Node.

```bash
aspire run                           # PostgreSQL, Pocket ID, API, frontend Vite, proxy TLS + tableau de bord
pnpm --dir web e2e                   # parcours Playwright, connectés par l'API de Pocket ID
dotnet test --solution Nid.slnx --max-parallel-test-modules 1   # tests unitaires et d'intégration (Docker requis), un projet à la fois
dotnet test --project tests/Nid.RealEstate.IntegrationTests   # l'intégration d'un seul module
```

L'application se joint sur `https://nid.localtest.me` (Pocket ID sur `https://auth.localtest.me`). Au premier démarrage,
un processus jetable (`pocket-id-bootstrap`) y crée les groupes `nid-users` et `nid-admins`, les trois utilisateurs de développement
(`alice` administratrice, `bob`, `carol` sans accès) et le client de Nid ; ils se connectent par passkey ou, pour les tests,
par un jeton à usage unique (`web/e2e/session.ts`).
Journaux, traces et métriques : tableau de bord Aspire (URL affichée par `aspire run`).
L'AppHost ne sert qu'au développement ; le déploiement est le compose de référence (`docs/architecture.md` §10).

Pour repartir d'une base vide : `docker volume rm nid-dev-pgdata nid-dev-pocket-id`.

### Données de démonstration

`deploy/local/demo/` remplit les écrans d'un foyer fictif (Alice, Bob et Léa) : abonnements et assurance, comptes bancaires et
opérations, placements et leur répartition, trois biens et trois crédits, revenus, garde, objectifs. Ses dates finissent le mois
dernier. La pile démarrée (`aspire run`) et Playwright installé (`pnpm --dir web install`) :

```bash
node deploy/local/demo/load.mjs                 # connecte Bob puis Alice, active la banque, importe, valide les mois de garde
node deploy/local/demo/sign-in-link.mjs alice   # un lien à usage unique pour se connecter (les utilisateurs n'ont pas de passkey)
```

La relancer ne change rien. Une base chargée un autre mois refuse le nouvel import (conflits) : repartir d'une base vide. Les
fichiers `alice.nid.json` et `bob.nid.json` sortent de `node deploy/local/demo/generate.mjs`, leurs dates figées à octobre 2026 ;
`DemoFileTests` vérifie qu'ils s'importent sans erreur ni avertissement, chaque section écrivant quelque chose.

### Frontend (`web/`)

Prérequis : Node ≥ 24 et pnpm 12 (`corepack enable`, ou installé à part).

```bash
pnpm --dir web install               # dépendances, versions exactes du lockfile
pnpm --dir web check                 # typage, lint, tests, build et budget du bundle
```

`dotnet build` réécrit `web/src/api/openapi.json` ; lancer ensuite `pnpm --dir web gen:api` pour régénérer
`web/src/api/schema.d.ts`, et versionner les deux avant de committer (la CI le vérifie avec `pnpm --dir web check:api`).
