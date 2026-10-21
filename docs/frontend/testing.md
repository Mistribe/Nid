# Tests et qualité du frontend

> Statut : brouillon. Stratégie de tests, budgets, CI.
> Références : `README.md`, `../architecture.md` §13 (stratégie générale), `../identity-and-access/access-tests.md` (banc anti-fuite côté serveur).

---

## 1. Niveaux de test

| Niveau | Contenu |
|---|---|
| **Lint et types** | ESLint, TypeScript strict. Règle personnalisée **« pas d'arithmétique sur les montants »**, avec son propre test. Règle contre les chaînes littérales dans le JSX (i18n) |
| **Unitaires et composants** | Vitest + Testing Library + MSW. Formats `Money` et `LocalDate`, y compris les pièges de date : fuseau, changement d'heure, fin d'année |
| **Import** | Conversion CSV → `nid.data` sur des fichiers de référence ; lots **déterministes** (même fichier, mêmes empreintes) ; canonicalisation RFC 8785 ; reprise après coupure |
| **E2E (Playwright)** | Voir §2 |
| **PWA** | Le service worker ne met jamais `/api` en cache ; les données hors ligne disparaissent à la déconnexion (`pwa-offline.md` §10) |
| **Accessibilité** | axe sur les composants et sur les écrans clés ; Lighthouse en CI |
| **Visuel** | Captures des écrans clés en clair et en sombre, mobile et bureau, sur le thème par défaut plus un thème alternatif |
| **Contraste des thèmes** | Test de tous les thèmes dans les deux modes (`design-system.md` §7.7) |
| **Palette de graphiques** | Rejeu du validateur à chaque changement de palette (`dataviz.md` §3) |
| **Taille** | Contrôle du budget de JavaScript en CI |

## 2. Scénarios E2E

L'environnement de test lance le compose applicatif avec un **Pocket ID de test** (clé d'API jetable ; utilisateurs `alice` administratrice, `bob`, `carol` sans accès). Pocket ID n'authentifie que par passkey : les parcours ouvrent sa session par son API (un jeton à usage unique par utilisateur, échangé une fois dans `e2e/global-setup.ts`, car Pocket ID limite ces échanges à environ cinq par minute), puis passent par le vrai flux OIDC (`e2e/session.ts`). En étape 1, les
parcours tournent en local contre la pile Aspire (`pnpm --dir web e2e`), pas en CI (web design §7).

- Connexion, session expirée, déconnexion (et purge du cache hors ligne).
- **Visibilité** : A ne voit pas le privé de B (et inversement), `404` sur un lien direct vers un élément invisible, `403` sur une écriture interdite.
- Routine mensuelle : valeurs préremplies, validation unique, message « Annuler ».
- Boîte de validation : diff, édition, approbation, rejet.
- **Import** : nominal, erreurs de validation, conflits avec « remplacer », reprise après fermeture d'onglet, session expirée.
- **Export** : JSON et CSV, avertissement quand `revision` change.
- Thème : changement sans rechargement, conservé après reconnexion, **pas de flash** au chargement.
- Hors ligne : tableau de bord en lecture seule avec sa date.

## 3. Budgets de performance

| Sujet | Budget |
|---|---|
| JavaScript initial (shell et tableau de bord) | 200 ko gzip au plus |
| Chargement à la demande | Recharts, pdf.js et l'assistant d'import dans des morceaux séparés |
| Chaînes | Celles des modules chargés à la demande hors du bundle initial (`locales/fr.<module>.json`, `architecture.md` §11) : une nouvelle clé propre à un module va dans son bundle |
| Mobile, tableau de bord | LCP sous 2,5 s, CLS sous 0,1 |
| Requêtes du tableau de bord | **Un seul appel agrégé** |

## 4. CI

1. Installation, génération du client OpenAPI et contrôle qu'il est à jour.
2. Lint, types, tests unitaires et de composants.
3. Test de contraste des thèmes ; validateur de palette si elle a changé.
4. Build, contrôle des budgets de taille.
5. E2E sur le compose de test ; axe et Lighthouse sur les écrans clés.
6. Captures visuelles : comparaison, échec sur différence non validée.

## 5. Ce qui n'est pas testé

- Le rendu exact d'un PDF dans pdf.js (délégué à la bibliothèque).
- La validation métier : elle appartient au serveur et à son banc de tests (`../identity-and-access/access-tests.md`, `../architecture.md` §13). Le frontend teste que les rapports du serveur sont **affichés fidèlement**.
