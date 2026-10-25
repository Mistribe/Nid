# Architecture du frontend

> Statut : brouillon. Pile, structure, amorçage, données, sécurité du navigateur.
> Références : `README.md`, `../architecture.md` §7 (API), `../identity-and-access/authentication.md`, `../import-format.md`.

---

## 1. Pile

| Couche | Choix | Vérification |
|---|---|---|
| Build | Vite, React, TypeScript strict | **TypeScript épinglé en 5.9** : typescript-eslint 8.70 ne supporte que `<6.1`, et `openapi-typescript` 7 déclare `^5` |
| Routage | TanStack Router : routes et paramètres de recherche typés | **Décidé (utilisé en 1E1)** |
| Données serveur | TanStack Query v5 | Documentation lue : `persistQueryClient`, `maxAge`, `buster`, `dehydrateOptions`, `QueryClient.clear()` |
| Client API | `openapi-typescript` (types) + `openapi-fetch` (client) + `openapi-react-query` | Documentation lue : génération `npx openapi-typescript spec -o schema.d.ts`, intercepteurs `onRequest` / `onResponse` |
| UI | Tailwind v4 + shadcn/ui (Radix), icônes Lucide | Documentation lue : installation Vite, variables OKLCH, mode sombre par classe `.dark` |
| Conversion de couleurs | culori, pour l'OKLCH → sRGB du test de contraste | **Vérifié** (1E1) : bibliothèque retenue et utilisée par `contrast.test.ts` |
| Graphiques | Composant `Chart` de shadcn/ui (Recharts v3) + SVG maison | Documentation lue (shadcn) ; compatibilité React à vérifier |
| Formulaires | React Hook Form + Zod | Non revérifié |
| i18n | i18next, français par défaut | **Décidé (utilisé en 1E1)** |
| PWA | vite-plugin-pwa, `injectManifest`, mise à jour en mode « prompt » | Documentation lue |
| Validation de schéma | Validateur compilé au **build** (mode standalone d'Ajv) | Documentation lue : la compilation à l'exécution utilise `Function`, incompatible avec la CSP |
| Tests | Vitest, Testing Library, MSW, Playwright, axe | Voir `testing.md` |

## 2. Arborescence `/web`

```
/web/src
  /app          routeur, providers, layout, amorçage de session, i18n, PWA, thème
  /api          schema.d.ts généré, client, intercepteurs, mappage des erreurs
  /features     dashboard, subscriptions, investments, real-estate, inbox, routine,
                items, documents, import-export, security, settings
  /components   ui/ (shadcn), Money, LocalDate, AccessBadge, StaleValue,
                ProvenanceBadge, EmptyState, ProblemMessage, charts/
  /lib          format, erreurs, permissions, canonical-json, hash
  /workers      csv-convert, batch-hash
  /styles       globals.css, themes/*.css
  /test         handlers MSW, jeux de données
/web/public
  theme-init.js   script externe bloquant (voir §9)
```

- Les dossiers de `/features` reprennent les modules du backend. Un module désactivé disparaît de la navigation : le SPA lit `GET /api/v1/platform/capabilities`. Ses champs : `modules` et `admin` (l'acteur est-il admin ; la feature `admin` n'est routée et affichée que si vrai, le serveur refusant de toute façon en `403 platform.admin_required`).
- `schema.d.ts` est généré par le développeur depuis l'OpenAPI (`dotnet build` réécrit `src/api/openapi.json`, puis `pnpm --dir web gen:api`), **versionné** ; la CI vérifie qu'il est à jour (`pnpm check:api`, `../architecture.md` §13).

## 3. Amorçage

1. Chargement de `theme-init.js` (avant tout rendu) : pose le thème et le mode depuis `localStorage`.
2. `GET /auth/session` → si non authentifié : redirection vers `/auth/login?returnUrl=` avec un **chemin relatif**.
3. Sinon : réponse `{ person, csrf, preferences, sessionEpoch }` ; les préférences remplacent la copie locale si elles diffèrent.
4. `GET /api/v1/platform/capabilities` ; puis rendu du routeur. Si la session ou les capacités ne se lisent pas, une page d'erreur propose « Réessayer » : jamais de page blanche ni de liste de modules devinée.
5. Le persister IndexedDB restaure le dernier tableau de bord **seulement si** `buster` (version + `sessionEpoch`) et `maxAge` correspondent (`pwa-offline.md`).

Exception : `/signed-out`, où mène la déconnexion, s'affiche sans redirection vers le login (`../identity-and-access/authentication.md` §6).

## 4. Client API

- Types et client **générés** ; aucun appel HTTP écrit à la main.
- Intercepteur `onRequest` : ajoute l'en-tête CSRF sur les méthodes non sûres, `credentials: same-origin`.
- Intercepteur `onResponse` : `401` → nettoyage (`queryClient.clear()`, persister) puis redirection vers le login ; les autres erreurs sont converties (§5).
- Idempotence : les écritures rejouables portent `Idempotency-Key` (`../architecture.md` §7).

## 5. Erreurs

Les réponses d'erreur sont des Problem Details (RFC 9457) avec un `code` stable. Un module unique (`/api/errors`) les convertit en présentation :

| Code | Présentation |
|---|---|
| `not_found` (`404`) | Écran « introuvable » ; jamais de distinction entre absent et non visible |
| `forbidden` (`403`) | Message et état lecture seule, avec la raison |
| `insufficient_scope` (`403`) | Message dédié (jeton, portée) |
| `conflict` (`409`) | « Modifié entre-temps », action Recharger |
| `validation` (`422`) | Erreurs rattachées aux champs par leur chemin |
| `fx_rate_missing` | Message avec l'action « Saisir le taux » |
| `import_too_large` | Message avec la limite et le conseil de découper |
| `access_violation` / `500` | Message générique ; l'identifiant de requête est affiché pour le support |

## 6. Données serveur

- **Clés de requête** : `[module, ressource, paramètres]` ; la portée `scope` (Foyer / Moi) fait partie de la clé.
- **`staleTime`** : 60 s pour le tableau de bord, 30 s par défaut ailleurs. Pas de nouvelle tentative sur les `4xx`.
- **Écritures pessimistes** : pas de mise à jour optimiste sur des données financières ; l'interface attend la réponse du serveur, qui fait foi. Le message « Annuler » (10 s) appelle `Void`.
- **Invalidation** ciblée après écriture (le tableau de bord et l'élément touché).
- **Persistance** : une seule requête est persistée (`['dashboard', personId]`), voir `pwa-offline.md`.

## 7. Formulaires et validation

- React Hook Form + Zod pour le confort (champs obligatoires, formats, montants bien formés).
- **Le serveur reste l'autorité** : ses erreurs de validation (chemins de champs) sont rattachées aux champs par le mappage du §5.
- Les montants sont saisis et transportés comme **chaînes décimales**, jamais convertis en nombres flottants.

## 8. Routage

- Routes typées, paramètres de recherche typés (`scope`, période, filtres) : l'état des vues est **partageable par URL**.
- Garde par capacités : une route de module désactivé redirige vers l'accueil.
- Les écrans d'erreur d'accès (`404`, `403`) sont des routes à part entière.

## 9. Sécurité du navigateur

**En-têtes servis par `nid-web`** (l'hébergeur peut en ajouter, par exemple HSTS) :

| En-tête | Valeur de référence |
|---|---|
| `Content-Security-Policy` | `default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self'; connect-src 'self'; worker-src 'self'; manifest-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'; object-src 'none'` |
| `Referrer-Policy` | `no-referrer` |
| `X-Content-Type-Options` | `nosniff` |
| `Permissions-Policy` | Caméra, micro, géolocalisation désactivés |
| `Cross-Origin-Opener-Policy` | `same-origin` |

- **Scripts strictement `'self'`** : aucun script inline, aucun `eval`. Les composants (Radix, graphiques) posent des styles inline pour le positionnement, d'où `'unsafe-inline'` limité à `style-src`.
- **`theme-init.js`** : petit script externe **bloquant** dans `<head>`, servi par la même origine, qui lit `localStorage` et pose `data-theme` et `.dark` avant le premier rendu. Un script inline serait bloqué par la CSP.
- Les icônes du catalogue sont servies par l'origine (`../subscription-data-model.md`, « Icons »).
- Le SPA ne stocke **aucun secret** ; `localStorage` ne garde que des préférences d'interface.
- **Lint** contre `dangerouslySetInnerHTML` ; les sorties de modèles, notes et libellés importés s'affichent comme du **texte brut**. pdf.js **4.2.67 ou plus**, avec `isEvalSupported: false` et `enableScripting: false` (`../security/application.md` §1).

## 10. Web Workers

Deux traitements lourds tournent hors du fil principal :

- **`csv-convert`** : lecture en flux d'un CSV et conversion en sections `nid.data`, selon la correspondance de colonnes choisie.
- **`batch-hash`** : découpage en lots, sérialisation **canonique** et SHA-256 de chaque lot avec Web Crypto (`crypto.subtle`, disponible car le HTTPS est terminé en amont).

La sérialisation canonique suit la **RFC 8785** (JSON Canonicalization Scheme). Le serveur utilise la même règle pour vérifier les empreintes (`../import-format.md` §11.2). Les montants étant des chaînes, aucune divergence de formatage numérique n'est possible. **Vérifié** (`../spikes.md` §5) : 405 documents, dont des cas adverses, donnent des empreintes identiques en JavaScript et en C#. Les surrogates isolées sont refusées côté .NET, ce qui est voulu.

## 11. Internationalisation et formats

- Français par défaut, **toutes les chaînes par clé** dès le départ (règle de lint contre les chaînes littérales dans le JSX).
- **Chaînes par module** : `locales/fr.json` ne garde que ce que le bundle initial affiche (shell, navigation, tableau de bord, composants partagés, erreurs). Les clés qu'un seul module ou quelques modules chargés à la demande utilisent vivent dans `locales/fr.<module>.json` (impôts, admin, revenus, placements, prêts, biens, propositions, routine, sécurité, simulations, palette ⌘K, guides, formulaires : erreurs de champ, assistant, listes, constats) ; `locales/<module>.ts` les ajoute à l'espace `translation` avec `addResourceBundle`, sous les **mêmes clés** (aucun appel `t()` ne change). Tout fichier qui emploie une de ces clés importe `@/locales/<module>` : le chunk du fichier emporte ses chaînes, quelle que soit la route qui le charge. `lazyStrings.test.ts` le vérifie, et qu'aucune clé de premier niveau n'existe deux fois. Le typage (`app/i18next.d.ts`) réunit `fr.json` et les bundles de modules. Seul `subscriptions-board` (vue d'ensemble et calendrier des abonnements) est un espace de noms à part, enregistré par ses onglets.
- Les libellés du catalogue (`label.<key>`) sont traduits côté interface : la source est monolingue.
- Montants et dates : composants `<Money>` et `<LocalDate>` (`design-system.md` §4).
- **Dates** : les valeurs `AAAA-MM-JJ` s'affichent par un utilitaire en **UTC** ; `new Date("2026-09-19")` décalerait le jour selon le fuseau.
- **Coller une date** : `DateField` lit une date collée sous les formes courantes (`2026-10-18`, `18.10.26`, `18 octobre 2026` ; toujours jour avant mois) par `lib/date-paste.ts` ; la saisie au clavier reste stricte (`JJ/MM/AAAA` ou `JJMMAAAA`). Un texte illisible est laissé tel quel ; un champ en lecture seule n'est jamais modifié.
- **Montants** : `Intl.NumberFormat` formate une chaîne décimale sans perte : vérifié par `Money.test.tsx` (un montant de 19 chiffres les garde tous).

## 12. Build et livraison

- Vite produit des fichiers hachés, servis par `nid-web` en cache immuable ; `index.html` et le service worker ne sont jamais mis en cache durablement.
- Découpage par route ; Recharts, pdf.js et l'assistant d'import sont chargés à la demande (budgets dans `testing.md`).
- Configuration d'exécution minimale : le SPA utilise des chemins relatifs sur une **seule origine** (`../identity-and-access/authentication.md` §7).
