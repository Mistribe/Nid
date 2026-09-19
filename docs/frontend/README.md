# Frontend

> Statut : brouillon issu de la discussion technique. Décrit le SPA (React, TypeScript, Vite) : pile, écrans, design system, graphiques, hors ligne, tests.
> Références : `../Project.md` (principes 1 à 6), `../architecture.md` (BFF, API, MCP), `../identity-and-access/` (session, droits), `../import-format.md` (protocole d'import et d'export orchestré par le navigateur).

---

## 1. Contenu

| Document | Sujet |
|---|---|
| `architecture.md` | Pile, arborescence, amorçage, client API, données, formulaires, i18n, sécurité du navigateur, Web Workers |
| `screens.md` | Navigation, écrans, routine mensuelle, boîte de validation, assistants d'import et d'export, états et erreurs |
| `design-system.md` | Jetons, formats, composants métier, accessibilité, ton, **thèmes** |
| `dataviz.md` | Choix des formes, palette validée, couleurs par entité, règles des graphiques |
| `pwa-offline.md` | Installation, service worker, cache du tableau de bord, purge |
| `testing.md` | Stratégie de tests, budgets, CI |

## 2. Principes

1. **Le frontend n'affiche que.** Il ne calcule jamais (gains, coûts amortis, totaux, conversions de devise) : tout arrive de l'API, en chaînes décimales. Une règle de lint interdit l'arithmétique sur les montants.
2. **Le serveur est l'autorité.** Toute validation du navigateur est du confort ; le serveur revalide. C'est vrai pour les formulaires comme pour l'import.
3. **Le navigateur orchestre l'import et l'export** par une suite d'appels ; **aucune tâche de fond** n'y participe (`../import-format.md` §11).
4. **Aucun jeton en JavaScript.** Session par cookie, en-tête CSRF, CSP stricte.
5. **Beau et simple compte autant que juste** (principe 5 de `Project.md`) : peu d'éléments par écran, états vides utiles, ton soigné.
6. **Jamais la couleur seule** : statuts, badges et graphiques ont toujours une icône, un texte ou un libellé.
7. **Chaque donnée garde sa source** : un badge de provenance accompagne les valeurs issues d'un document, de la banque ou d'un import.
8. **Consultation d'abord sur mobile**, saisie d'abord sur bureau, les deux utilisables partout.

## 3. Décisions

| # | Décision | Statut |
|---|---|---|
| F1 | React + TypeScript strict + Vite, PWA | Décidé |
| F2 | Tailwind v4 + shadcn/ui (Radix), icônes Lucide | Décidé |
| F3 | TanStack Query v5 ; client API généré depuis l'OpenAPI (`openapi-typescript`, `openapi-fetch`) | Décidé |
| F4 | Hors ligne : **dernier tableau de bord uniquement**, en IndexedDB, purgé à la déconnexion | Décidé |
| F5 | Import et export **orchestrés par le navigateur**, sans tâche de fond | Décidé |
| F6 | Graphiques : composant `Chart` de shadcn/ui (**Recharts v3**) + SVG maison pour la bande de couverture ; palette du skill de visualisation, validée par script | Décidé |
| F7 | **Thèmes** : 4 jeux de couleurs curés, préférence **par personne côté serveur** | Décidé |
| F8 | Routage : TanStack Router | Retenu par défaut (non revérifié ; alternative : React Router) |
| F9 | Formulaires : React Hook Form + Zod ; i18n : i18next | Retenu par défaut (non revérifié) |
| F10 | Visionneuse PDF : pdf.js, **version 4.2.67 ou plus**, scripts et évaluation désactivés | Retenu par défaut (alternative : visionneuse native) |

## 4. Questions ouvertes

- **Direction visuelle** : couleur d'accent, ambiance et valeurs des quatre thèmes. Passe de design à part, avec maquettes.
- Compatibilité de Recharts v3 (et du composant `Chart` de shadcn/ui) avec la version de React retenue.
- Bibliothèques à choisir : conversion OKLCH vers sRGB pour le test de contraste, analyse CSV en flux, archive ZIP pour l'export CSV, canonicalisation JSON (RFC 8785).
- Précision exacte de `Intl.NumberFormat` sur des chaînes décimales dans les navigateurs visés.
- Politique de claims d'Authelia : impacte l'affichage du nom (`../identity-and-access/authentication.md`).
