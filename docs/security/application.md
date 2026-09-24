# Sécurité de l'application

> Statut : brouillon. Navigateur, API, import et export, SSRF, uploads, emails, données tierces.
> Références : `README.md`, `../frontend/architecture.md` §9 (CSP), `../identity-and-access/`, `../import-format.md`.

---

## 1. Navigateur

| Risque | Mesure |
|---|---|
| **XSS** | Échappement par défaut de React ; **lint interdisant `dangerouslySetInnerHTML`** ; les données importées (noms, notes, libellés du catalogue) sont du texte ; CSP `script-src 'self'` sans `unsafe-eval` (`../frontend/architecture.md` §9) |
| **Visionneuse PDF** | pdf.js **4.2.67 ou plus** (la [CVE-2024-4367](https://codeanlabs.com/2024/05/cve-2024-4367-arbitrary-js-execution-in-pdf-js/) permettait l'exécution de JavaScript par les polices), `isEvalSupported: false`, `enableScripting: false` |
| CSRF | En-tête personnalisé obligatoire, contrôle de `Origin` et de `Sec-Fetch-Site`, aucune configuration CORS |
| Redirection ouverte | `returnUrl` limité à un **chemin relatif** |
| Clickjacking | `frame-ancestors 'none'` |
| Vol de session | Cookie `__Host-`, `HttpOnly`, `SameSite=Lax` ; durée absolue de 30 jours ; « déconnecter partout » (`../identity-and-access/authentication.md`) |
| Appareil volé | Purge du cache hors ligne à la déconnexion ; verrouillage de l'appareil ; verrouillage applicatif en v2 |
| Liens externes | `website` et `pricing_page` en `http` ou `https` seulement, avec `rel="noopener noreferrer"` ; **jamais récupérés** par le serveur |

## 2. API et données

| Risque | Mesure |
|---|---|
| Contrôle d'accès (IDOR) | Pipeline d'autorisation, garde de sortie, banc anti-fuite (`../identity-and-access/`) |
| Injection SQL | EF Core paramétré ; **aucun SQL brut concaténé** (règle de revue) |
| Sur-assignation | DTO explicites (types source-générés), jamais de liaison directe aux entités |
| Désérialisation | JSON source-généré ; **membres inconnus refusés** à l'import |
| ReDoS | Expressions régulières générées avec un **délai maximal** |
| Erreurs | Problem Details sans trace d'exécution ; les `500` renvoient un identifiant de requête |
| Journaux | Ni libellés ni montants au niveau info ; jetons masqués |
| Limitation de débit | Upload, import, export, création de jeton, échecs d'authentification |
| OIDC | Validation de `iss`, `aud`, `nonce`, PKCE, signature par les clés publiées ; découverte en HTTPS |

## 3. Client HTTP sortant unique (SSRF)

Toute requête que `nid-api` émet vers l'extérieur passe par un **`SafeHttpClient`** (D21) : taux de change, catalogue, icônes, fournisseurs LLM, SMTP. Les [bonnes pratiques MCP](https://modelcontextprotocol.io/specification/draft/basic/security_best_practices) décrivent les mêmes attaques (plages privées, métadonnées cloud, redirections, liaison DNS).

| Règle | Détail |
|---|---|
| HTTPS seulement | Aucun `http`, aucun schéma exotique |
| **Plages refusées** | Après résolution DNS : privées (`10/8`, `172.16/12`, `192.168/16`, `fc00::/7`), loopback, link-local (dont `169.254.169.254`), réservées. Ne pas écrire l'analyse d'adresses à la main (encodages octal, hexadécimal, IPv4 mappé en IPv6) : utiliser une bibliothèque ou les primitives de la plateforme |
| **Redirections** | Non suivies automatiquement ; **chaque saut est revalidé** (mêmes règles) |
| Résolution épinglée | La résolution vérifiée est celle utilisée pour la connexion (contre la liaison DNS) |
| Bornes | Délais, taille maximale de réponse |
| **Liste d'hôtes** | Configurable et **explicite** : dépôt `SubTracker-Data` (`raw.githubusercontent.com`), historique de la BCE (`www.ecb.europa.eu`), hôtes d'icônes, hôtes des profils LLM **activés**, serveur SMTP. Tout autre hôte est refusé |
| Journal | Chaque refus est journalisé (hôte, motif) |

Un éventuel proxy de sortie (par exemple Smokescreen) peut s'ajouter côté hébergeur.

## 4. Icônes du catalogue

Les `icon` du catalogue sont des URL de tiers, donc du contenu non fiable.

- Téléchargement par le `SafeHttpClient`, **hôte dans la liste autorisée** ; sinon monogramme.
- **512 Ko** au plus, type vérifié par le contenu, **ré-encodage en WebP**, **SVG refusé**.
- Servies depuis l'origine, jamais depuis l'URL d'origine (`../subscription-data-model.md`, « Icons »).

## 5. Uploads

- Liste blanche de types **vérifiés par le contenu** ; taille maximale ; nombre de pages pour les PDF.
- Le **nom d'origine** est une métadonnée : jamais utilisé dans un chemin. Stockage par empreinte.
- Téléchargement en `Content-Disposition: attachment` hors visionneuse ; `X-Content-Type-Options: nosniff`.
- Les scripts et fichiers embarqués d'un PDF ne sont jamais exécutés.

## 6. Import et export

| Risque | Mesure |
|---|---|
| JSON malveillant (bombe, imbrication profonde) | Profondeur maximale (32), taille de requête, longueur de chaînes, nombre d'entités (`../import-format.md` §11.5) |
| **Détournement de session d'import** | Identifiant **aléatoire sur 128 bits** ; la session appartient à la personne qui l'a créée, vérifié à **chaque** appel (`404` sinon) |
| Surcharge | Une session active par personne, expiration à 24 h, durée maximale des requêtes |
| **Injection de formules CSV** | À l'export, toute cellule **texte** commençant par `=`, `+`, `-`, `@`, tabulation ou retour chariot est **neutralisée** (préfixe d'une apostrophe). Les colonnes numériques ne sont pas modifiées. Testé (`../frontend/testing.md`) |
| CSV importé | Lu comme des données ; jamais interprété |

## 7. Emails

- **Sans montants ni libellés d'éléments** par défaut (D22) : comptes, rappels, liens. Une option permet d'inclure les montants.
- En-têtes nettoyés (retours à la ligne interdits), HTML échappé, SMTP avec TLS.
- Le résumé de chaque adulte est construit **à son nom** (`../identity-and-access/authorization.md` §8).

## 8. Données tierces

| Source | Risque | Mesure |
|---|---|---|
| Taux de change | Taux aberrants ou empoisonnés | Variation quotidienne aberrante **refusée et signalée** (`fx_sync_run.rejected`) ; source affichée ; saisie manuelle prioritaire sur `subtracker_data` et `ecb` ; taux indicatifs |
| Catalogue | Contenu piégé, plans trompeurs | Commit **épinglé** ; schéma validé ; textes bornés ; plans en **suggestions** jamais appliquées sans validation ; rapport de synchronisation |
| Dépôt du taux | Compromission de l'amont | Bornes de plausibilité ; **la BCE (`www.ecb.europa.eu`) sert de second point de comparaison et d'historique** (`../core-data-model.md` §8.4), un cours SubTracker étant vérifié contre elle ; miroir possible (`../core-data-model.md` §8.3) |

## 9. Tests

| Test | Vérifie |
|---|---|
| SSRF | Refus des IP privées, loopback, link-local et métadonnées ; redirection vers l'interne refusée ; hôte hors liste refusé |
| Icônes | SVG refusé ; image non conforme refusée ; sortie ré-encodée |
| CSV | Cellules dangereuses neutralisées, colonnes numériques intactes |
| Import | Bombe JSON, imbrication, membres inconnus, session d'une autre personne → `404` |
| Uploads | Faux type (extension trompeuse), fichier trop gros, nom hostile |
| Emails | Aucun montant par défaut ; injection d'en-tête refusée |
| Navigateur | CSP sans `unsafe-eval` ; lint `dangerouslySetInnerHTML` ; pdf.js à la version minimale |
