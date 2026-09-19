# Architecture technique

> Statut : brouillon issu de la discussion technique. Les décisions marquées **retenu par défaut** n'ont pas été contestées mais ne sont pas explicitement confirmées : voir §16.
> Références : `Project.md` (vision, périmètre fonctionnel, décisions produit), `subscription-data-model.md` (module Abonnements).

---

## 1. Objet et périmètre

Ce document décrit **comment** l'application est construite : stack, découpage en modules, conventions de données, API, MCP, ingestion automatisée, déploiement, sauvegarde, tests.

Hors périmètre :

- **L'accès depuis l'extérieur** (VPN, tunnel, reverse proxy public) : c'est le choix de celui qui héberge. L'application documente ses hypothèses (§10.2) mais n'impose aucune solution.
- Les choix de matériel au-delà de « un serveur maison / mini PC ».

## 2. Contraintes issues de `Project.md`

| Contrainte | Conséquence technique |
|---|---|
| Auto-hébergé, deux utilisateurs | Pas de recherche de scalabilité ; priorité à la simplicité d'exploitation |
| L'IA ne calcule jamais | Calculs en fonctions pures déterministes, testées ; l'IA ne sert qu'à l'ingestion |
| L'IA propose, les humains valident | File de validation obligatoire pour toute donnée automatique |
| Chaque donnée garde sa source | Provenance sur toutes les données saisies ou importées |
| Confidentialité | Extraction par modèle local par défaut ; aucun document ne quitte l'infra sans opt-in |
| Propriétaires multiples, visibilité privé/partagé | Autorisation centralisée, appliquée à l'API, au tableau de bord et au MCP |
| Ajouter des modules sans tout remettre en cause | Monolithe modulaire à frontières strictes |
| PWA, notifications email interchangeables | SPA installable ; canal de notification derrière une interface |
| Sauvegarde chiffrée 3-2-1, export complet | Sauvegarde base + documents + clés ; export JSON/CSV versionné |

## 3. Décisions

| # | Décision | Statut |
|---|---|---|
| D1 | Backend **C# / .NET**, ASP.NET Core | Décidé |
| D2 | Frontend **SPA TypeScript** (React + Vite), PWA | Décidé (SPA) ; React **retenu par défaut** contre Vue |
| D3 | Base **PostgreSQL** | Décidé |
| D4 | Hébergement **Docker Compose** sur un serveur maison / mini PC | Décidé |
| D5 | **Monolithe modulaire** | Décidé |
| D6 | Authentification par **OIDC externe, Authelia** | Décidé |
| D7 | Extraction de documents : **port pluggable, Ollama par défaut** | Décidé |
| D8 | Jobs domaine en **Quartz.NET in-process** ; jobs d'ingestion en **workers conteneurisés** via table de jobs | Décidé (frontière) ; Quartz **retenu par défaut** |
| D9 | Registre **`Item` central** dans Core pour propriété et visibilité | **Retenu par défaut** |
| D10 | Écritures MCP : **proposition par défaut**, écriture directe via scope dédié | **Retenu par défaut** |
| D11 | Accès externe : **hors scope**, choix de l'hébergeur | Décidé |

## 4. Stack et topologie

### 4.1 Stack

| Couche | Choix | Remarque |
|---|---|---|
| Backend | .NET (dernière LTS au démarrage du projet, à figer dans un ADR), ASP.NET Core | `decimal` natif pour l'argent |
| Dates | NodaTime | `LocalDate`, `Instant` ; mapping via le plugin Npgsql |
| Accès données | EF Core + Npgsql | Un `DbContext` par module |
| API | REST + OpenAPI, versionnée | Client TypeScript généré |
| Frontend | React, TypeScript, Vite | PWA |
| Base | PostgreSQL | Un schéma par module |
| Authentification | Authelia (OIDC), l'application est cliente OIDC | SPA via BFF, cookie de session |
| Jobs domaine | Quartz.NET in-process, magasin persistant Postgres | Voir §9 |
| Workers | Conteneurs, techno libre (Python pour l'extraction) | Voir §9 |
| LLM | Ollama, derrière `IDocumentExtractor` | Local par défaut |
| Notifications | SMTP derrière `INotificationChannel` | Push PWA, ntfy, Home Assistant plus tard |
| Fichiers | Volume de fichiers, adressage par contenu | Métadonnées en base |

### 4.2 Topologie

```
[reverse proxy de l'hébergeur : hors application]
        │
        ├──> nid-web   (SPA statique)
        └──> nid-api   (.NET : API + BFF + MCP + Quartz)
                │  ▲
   OIDC ◄───────┘  │ API interne (port séparé, non routé par le proxy)
   Authelia        │
                   ├── nid-worker-* (conteneurs d'ingestion)
                   ├── postgres
                   ├── volume documents
                   └── ollama (optionnel)

backup (restic, chiffré) ──> cible locale + cible distante
```

Un seul processus `nid-api` porte l'API, le BFF, l'endpoint MCP et les tâches planifiées. On ne le scinde que si un besoin réel apparaît.

## 5. Modules et propriété des données

### 5.1 Règle de base

Un module est composé de ses projets .NET (`Contracts`, `Domain`, `Application`, `Infrastructure`), de son **schéma Postgres** et de son `DbContext`. Aucun module ne lit les tables d'un autre ni ne référence son domaine ; ils communiquent par leurs `Contracts` (interfaces et DTO). Des **tests d'architecture** (NetArchTest) le vérifient en CI.

### 5.2 Modules

| Module | Possède | Expose (Contracts) | Dépend de |
|---|---|---|---|
| **Core** | `Person` (lié au `sub` OIDC), `Item` (registre), `Ownership` (part par personne), visibilité, `Valuation`, `Flow`, `Document`, `Proposal` (file de validation), provenance, `fx_rate` | `IAccessPolicy`, `IVisibleItems`, types `Money`/`Duration`/`DateRange`, `IValuations`, `IFlows`, `IProposals` | rien |
| **Subscriptions** | `Subscription`, `BillingPhase`, `Purchase`, `Grant`, caches dérivés `CoveragePeriod`/`Charge` | Contributions au tableau de bord (coût mensuel réel/amorti, échéances), charges attendues | Core |
| **Investments** | `Envelope` (PEA, assurance-vie, PER, livret…) ; valorisations et flux via Core | Gain €/%, TRI plus tard, contribution au patrimoine | Core |
| **RealEstate** | `Property`, `Loan`, `LoanPhase`, `LoanEvent`, `Insurance` | Tableau d'amortissement, capital restant dû, valeur nette, engagements | Core |
| **Ingestion** | Pipeline de documents, table `job`, contrats de jobs, résultats d'extraction | Entrée des propositions vers Core, API interne pour les workers | Core |
| **Dashboard** | Rien (lecture seule, composition) | Agrégats patrimoine / engagements / évolution | Core + `Contracts` des autres |
| **Platform** (technique) | Notifications, audit, export, planification | `INotificationChannel`, `IExporter` | Tous, via contrats |

### 5.3 Décisions de conception

1. **Registre `Item` central (Core).** Chaque bien, abonnement, enveloppe ou crédit a une ligne `Item(id, module, kind, visibility)` et ses `Ownership`. Le module garde ses données et référence `item_id` (lien logique, pas de clé étrangère entre schémas). `IAccessPolicy` applique les règles de `Project.md` §5.1 (privé, lecture seule pour l'autre, totaux du foyer hors privé) pour l'API, le tableau de bord et le MCP : un seul endroit à auditer.
2. **Tableau de bord par contributions enfichables.** Chaque module implémente `INetWorthContribution`, `ICommitmentsContribution`, `IUpcomingEvents`, découvertes par injection de dépendances. Un nouveau module apporte de nouveaux fournisseurs sans modifier Dashboard. La courbe d'évolution est **calculée à la demande** à partir des valorisations, flux et capitaux restants dus ; des instantanés matérialisés ne sont ajoutés que si un besoin réel apparaît.
3. **File de validation dans Core, application dans le module.** Une `Proposal` porte un payload typé, sa provenance et son document source. À la validation, Core appelle l'`IProposalHandler` du module concerné, qui écrit dans son schéma. Ingestion ne touche jamais aux données métier.
4. **`Valuation` et `Flow` génériques (Core)** pour Investments et RealEstate. Subscriptions conserve ses propres `Purchase` / `BillingPhase` : sa logique de couverture n'est pas un `Flow`.
5. **Calculs = fonctions pures dans `Domain`** (timeline, amortissement, gain, TRI) : aucune entrée/sortie, aucune IA. Les caches dérivés sont reconstruisibles à tout moment.
6. **Communication** : appels synchrones in-process via les Contracts ; événements de domaine in-process après validation de la transaction pour les réactions (ex. abonnement modifié → recalcul du cache). Pas de bus externe.

## 6. Conventions de données

| Sujet | Décision |
|---|---|
| Argent | `Money(decimal, Currency)`. Postgres `numeric(19,4)` + `char(3)`. Jamais de flottant. Devise d'origine stockée, conversion à l'affichage. |
| Taux de change | Table `fx_rate(date, base, quote, rate)` dans Core, alimentée par un job (source BCE) avec saisie manuelle en secours. Conversion au taux de la date concernée. |
| Arrondis | Précision pleine en interne, arrondi à l'affichage. Crédit : politique d'arrondi **configurable par prêt** pour coller au tableau de la banque, qui fait référence. |
| Dates | NodaTime `LocalDate` → `date`. Instants en `timestamptz` UTC. Intervalles **semi-ouverts** `[début, fin)` ; `daterange` Postgres quand utile. |
| Durées | `{count, unit}` (`day`/`week`/`month`/`year`), jamais converties en jours. Arithmétique des mois avec jour d'ancrage et écrasement en fin de mois (voir `subscription-data-model.md`). Le comportement de la bibliothèque de dates est couvert par des tests dédiés (31 janvier + 1 mois, années bissextiles). |
| Identifiants | UUIDv7 générés côté application (triables par date). |
| Valorisations et flux | **Append-only** avec `recorded_at`. Une correction crée une nouvelle ligne ; la plus récente gagne par `(item, as_of)`. Historique conservé. |
| Provenance | Colonnes communes sur toute donnée saisie ou importée : `source` (manuel / api_bancaire / document / import), `document_id?`, `proposal_id?`, `created_by`. |
| Audit | Table `audit_log` append-only (Platform) : qui, quand, entité, action, diff JSONB. Écrite par un interceptor EF. Enregistre aussi « agissant pour » quand l'acteur est le MCP. |
| Suppression | Suppression logique (`deleted_at`) sur les agrégats utilisateur ; purge réelle par action explicite. |
| Concurrence | Verrou optimiste via la colonne système `xmin`. Conflit → HTTP 409. |
| Documents | Stockés par contenu : `/data/documents/{sha[0:2]}/{sha256}`, dédupliqués. Métadonnées en base. Chiffrement au repos = disque de l'hôte ; sauvegarde chiffrée. |
| Secrets | Jetons et consentements bancaires chiffrés avec ASP.NET Data Protection ; clés sur volume sauvegardé. |

## 7. API

- REST JSON `/api/v1/{module}/…`, OpenAPI généré, **client TypeScript généré** pour le SPA. Erreurs en Problem Details (RFC 9457).
- Sur le fil : montants en **chaînes** (`{"amount":"12.34","currency":"EUR"}`), dates ISO 8601. Intervalles en champs explicites `startsOn` / `endsBefore` (le `ends_on` du modèle d'abonnements est ambigu et n'a été précisé « semi-ouvert » qu'après coup). **Retenu par défaut.**
- **Authentification** : le SPA passe par un **BFF** (code + PKCE côté serveur, cookie `HttpOnly`, protection CSRF). L'API accepte aussi des jetons bearer (MCP, automatisations) avec les scopes `read` et `write`.
- Écritures idempotentes via l'en-tête `Idempotency-Key` (workers, MCP).
- Couche application en handlers simples, **sans MediatR** (licence commerciale depuis 2025).
- **API interne des workers** : groupe de routes `/internal/v1/jobs/…` sur un port / listener séparé, non routé par le proxy, avec identifiants de service par worker.

## 8. Serveur MCP

- Endpoint `/mcp`, transport Streamable HTTP, dans le même processus, avec le SDK C# officiel (`ModelContextProtocol.AspNetCore`). Mode sans session (par défaut, recommandé tant qu'il n'y a pas de requêtes serveur → client). Autorisation via les filtres d'autorisation du SDK, `MapMcp()` protégé par `RequireAuthorization()`.
- Les outils sont de **minces enveloppes** sur les services d'application utilisés par REST : même `IAccessPolicy`, mêmes droits ; un élément privé reste privé.
- **Lectures** (`net_worth`, `list_subscriptions`, `loan_schedule`, `envelope_performance`…) : chiffres calculés par les services déterministes, avec provenance. Le modèle ne calcule rien.
- **Écritures** : par défaut, elles créent une `Proposal` dans la file de validation. Un jeton peut recevoir le scope `write:direct` quand l'utilisateur dicte lui-même la saisie. **Retenu par défaut.**
- Authentification : OAuth via Authelia, ou jeton d'accès personnel révocable.
- Le MCP **client** (messagerie, stockage de documents) vit dans les workers d'ingestion, pas dans le cœur.

## 9. Ingestion, jobs et file de validation

### 9.1 Flux de bout en bout

```
Source (dépôt UI / mail / banque)
  → Document (Core, SHA-256)
  → Job (Ingestion) ──claim──> Worker (conteneur, techno libre)
  → JSON structuré (schéma versionné) ──> API interne
  → validation du schéma + contrôles métier .NET
  → Proposal (Core, statut pending)
  → humain : approuve / édite / rejette
  → IProposalHandler du module → écriture + audit + provenance
```

### 9.2 Où s'exécute quoi

| Type de travail | Exécution | Raison |
|---|---|---|
| Rappels, résumé mensuel, alertes d'échéance, recalcul des timelines | **Quartz.NET in-process** | Réutilise les règles métier (couverture, coût amorti, droits) ; les conteneuriser dupliquerait la logique |
| Extraction PDF/OCR/LLM, scrapers woob, lecture de mails | **Workers conteneurisés** | Écosystème Python, travaux lourds et fragiles, isolation et mise à jour indépendantes |

Le planificateur n'a pas accès à `docker.sock` : lancer des conteneurs depuis l'application donnerait de fait un accès root à la machine, et déléguer à un cron d'hôte lierait l'application à l'orchestrateur de l'hébergeur.

Le magasin persistant de Quartz est en Postgres, avec un préfixe de tables qualifié par schéma (`platform.QRTZ_`).

### 9.3 Jobs

- Table `job` (schéma Ingestion) : `type`, `schema_version`, `payload`, `status` (`queued` / `running` / `succeeded` / `failed` / `dead`), `attempts`, `run_after`, `lease_until`, `result`, `error`, `created_by`.
- Les workers **n'accèdent pas à la base**. Ils appellent l'API interne : `claim` (long-poll, `FOR UPDATE SKIP LOCKED` côté API), `heartbeat`, `complete`, `fail`. Bail expiré → remise en file. Retry avec délai croissant ; état `dead` après N essais, avec alerte email.
- **Idempotence** : `(type, hash de l'entrée)` évite de retraiter le même document.
- **Contrats** dans le dépôt : `contracts/jobs/{type}/v{n}/{input,output}.schema.json`. L'API valide la sortie du worker avant toute suite ; version inconnue = rejet. Ajouter une techno = respecter le contrat.
- Types initiaux : `extract-statement` (relevés de placements), `extract-amortization`, `extract-invoice`. Plus tard : `fetch-mail`, `sync-bank`, `scrape-woob`.
- Les workers récupèrent le document par l'API (flux à jeton court), sans volume partagé : ils restent sans état.
- Traçage OpenTelemetry API ↔ worker (`traceparent` dans le job). Page « jobs » dans l'interface.

### 9.4 Extraction (worker Python + Ollama par défaut)

1. PDF → texte (PyMuPDF, pdfplumber) ; si scanné, OCR ou modèle de vision.
2. LLM avec **sortie JSON contrainte** par le schéma.
3. Contrôles déterministes : totaux, dates cohérentes, devise connue.
4. Sortie : valeurs **et** avertissements de confiance. Jamais de calcul par le modèle.

Pour `extract-amortization`, RealEstate **recalcule** le tableau depuis les conditions du prêt et le compare à celui de la banque (`Project.md` §8.5) ; l'écart figure dans la proposition. Une extraction douteuse devient visible.

Avant d'engager un modèle : constituer un **jeu d'évaluation** de vrais relevés avec le JSON attendu pour mesurer la précision par champ. Le choix du modèle relève de la configuration, pas de l'architecture. L'extracteur est un port (`IDocumentExtractor`) : un extracteur cloud éventuel est un **opt-in par document**, tracé dans la provenance.

### 9.5 Proposal (Core)

- Champs : `kind`, `target_item_id?`, `payload` + version, `status` (`pending` / `approved` / `rejected` / `superseded`), `source`, `document_id`, `job_id`, `warnings`, `decided_by/at`, payload **original et édité** conservés.
- **Visibilité héritée** de l'élément cible ; pour un nouvel élément, le propriétaire est celui qui a déposé le document. Une proposition sur un élément privé n'apparaît que chez son propriétaire.
- **Dédoublonnage** par empreinte `(kind, cible, hash du payload)`. Une nouvelle proposition remplace (`superseded`) une ancienne en attente sur la même cible et la même période.
- **Contrat de handler** `IProposalHandler<TPayload>` : `Validate` (problèmes), `Preview` (diff contre l'état actuel), `Apply` (écriture idempotente dans **une transaction** avec le changement de statut et l'audit).
- **Jamais d'auto-approbation.** Les propositions en attente sont rappelées dans le résumé mensuel, sans expiration silencieuse.
- Interface : boîte de réception avec le PDF à côté, valeurs extraites, diff et avertissements.

### 9.6 Déclencheurs

| Déclencheur | Effet |
|---|---|
| Dépôt manuel d'un PDF | Job d'extraction selon le type |
| Routine mensuelle (Quartz) | Rappels par email ; formulaire **pré-rempli** depuis le mois précédent pour chaque enveloppe |
| Mail (étape 4 et après) | `fetch-mail` → factures → `extract-invoice` |
| Banque (étape 5) | `sync-bank` (Enable Banking) → rapprochement avec les charges attendues exposées par Subscriptions → propositions « prélèvement observé », « hausse de prix », « prélèvement récurrent inconnu ». Lecture seule ; consentements chiffrés ; alerte avant expiration. |

### 9.7 Sécurité de l'ingestion

- Identifiants de service **par worker**, limités à ses types de jobs. Réseau compose segmenté : un worker n'atteint que l'API interne, Ollama et ce que son rôle exige (woob → banques, mail → IMAP).
- Aucun document ne quitte l'infrastructure par défaut.

## 10. Déploiement

### 10.1 Docker Compose de référence

| Service | Rôle | Obligatoire |
|---|---|---|
| `nid-api` | API + BFF + MCP + Quartz | oui |
| `nid-web` | SPA statique | oui |
| `postgres` | Base | oui |
| `authelia` | OIDC | oui |
| `nid-worker-*` | Workers d'ingestion | à la carte |
| `ollama` | LLM local | optionnel (avec les workers d'extraction) |
| `backup` | restic chiffré | oui |

- Configuration par variables d'environnement et secrets Docker (fichiers). Aucun secret dans l'image ni dans le dépôt.
- Images multi-architecture (amd64/arm64), utilisateur non-root, système de fichiers en lecture seule sauf volumes, healthchecks.
- **Migrations** EF Core par module, appliquées au démarrage de `nid-api` sous verrou advisory Postgres. Migrations rétro-compatibles (expand/contract) pour permettre un retour arrière d'image.
- Volumes : `pgdata`, `documents`, `dataprotection-keys`, `authelia`. Tous couverts par la sauvegarde.
- Mises à jour : tags d'image épinglés, procédure documentée (sauvegarde → pull → up). Pas de mise à jour automatique.

### 10.2 Hypothèses envers l'hébergeur

Le reverse proxy, le TLS et l'accès externe sont fournis par l'hébergeur, hors du compose applicatif. L'application suppose seulement :

- HTTPS terminé en amont ;
- en-têtes `X-Forwarded-*` de confiance (liste de proxys configurable) ;
- une URL publique configurable (`PUBLIC_URL`) utilisée pour les redirections OIDC, les liens des emails et le manifeste PWA.

Un exemple de reverse proxy peut figurer dans `deploy/`, à titre optionnel.

## 11. Sauvegarde et export

- **restic**, chiffré, règle 3-2-1 : cible locale + cible distante au choix de l'hébergeur (S3, SFTP, autre disque).
- Contenu : dump Postgres (`pg_dump` cohérent, pas de copie à chaud du répertoire de données) + volume `documents` + clés Data Protection + configuration Authelia. **Sans les clés Data Protection, les jetons bancaires chiffrés sont perdus.**
- **Test de restauration** documenté et scripté (restauration dans un compose jetable).
- Alerte email si la sauvegarde échoue ou dépasse N jours.
- **Export complet** JSON/CSV à la demande (`IExporter`), format documenté et versionné, pour ne jamais dépendre de l'application. Respecte la visibilité : chacun exporte ce qu'il voit.

## 12. Observabilité

- Logs structurés JSON, **sans données financières** (montants et libellés masqués hors niveau debug).
- OpenTelemetry : traces API ↔ workers ; métriques (durée des jobs, propositions en attente, âge de la dernière sauvegarde, âge des consentements bancaires). Export OTLP optionnel vers ce que l'hébergeur fournit ; sans backend, logs des conteneurs et page d'état dans l'interface.
- `/health/live` et `/health/ready` (base, migrations, Authelia joignable).

## 13. Stratégie de tests

| Niveau | Cible |
|---|---|
| **Unitaires purs** | `Domain` : timeline d'abonnement, coût amorti, amortissement de crédit, gain, TRI. Sans E/S. |
| **Propriétés** (FsCheck) | Invariants : phases sans chevauchement, couverture contiguë, somme des coûts amortis = base de coût, capital restant dû final = 0, capital + intérêts = somme des mensualités. |
| **Cas dorés** | Tableaux d'amortissement réels de la banque, exemples de `subscription-data-model.md`, reprise du JSON existant avec ses anomalies connues. |
| **Intégration** | Testcontainers Postgres : handlers, migrations, `IAccessPolicy` (matrice privé / partagé / multi-propriétaires × lecture / écriture). |
| **Contrats** | Schémas de jobs validés avec des exemples ; chaque type de job possède un schéma d'entrée et de sortie. |
| **Architecture** | NetArchTest : aucune dépendance entre modules hors `Contracts` ; `Domain` sans référence à l'infrastructure. |
| **Extraction** | Jeu d'évaluation de relevés, mesuré hors CI standard (précision par champ). |
| **E2E** | Playwright sur les parcours clés (saisie mensuelle, validation d'une proposition, tableau de bord). |

## 14. Structure du dépôt (monorepo)

```
/src
  /Nid.Core/{Contracts,Domain,Application,Infrastructure}
  /Nid.Subscriptions/…   /Nid.Investments/…   /Nid.RealEstate/…
  /Nid.Ingestion/…       /Nid.Dashboard/…     /Nid.Platform/…
  /Nid.Host              (racine de composition : API, BFF, MCP, Quartz)
/web                     (React + TS + Vite PWA, client généré)
/workers/{extract-python,…}
/contracts/jobs/…        (schémas JSON versionnés)
/deploy/compose          (compose de référence, exemples d'environnement)
/docs                    (Project.md, subscription-data-model.md, architecture.md, adr/)
/tests/…
```

- CI : build, tests (unitaires, propriétés, architecture, intégration), génération du client OpenAPI et contrôle qu'il est à jour, analyse des images.
- **ADR** dans `docs/adr/` : une décision structurante par fichier (stack, monolithe modulaire, OIDC, workers par table de jobs, registre `Item` central…), pour rendre les décisions de ce document traçables.

## 15. PWA et notifications

- **PWA** : manifeste et service worker avec cache des assets et du dernier tableau de bord consulté (lecture hors ligne). Saisie mobile en ligne uniquement.
- **Notifications** : `INotificationChannel`, email SMTP en premier. Types : critique (consentement bancaire, validation en attente), échéance (renouvellement, hausse de prix), résumé mensuel. Préférences par utilisateur.

## 16. Questions ouvertes et points à confirmer

**Retenus par défaut, à confirmer**

- React plutôt que Vue (D2).
- Quartz.NET plutôt que Hangfire pour les tâches planifiées du domaine (D8).
- Registre `Item` central plutôt que visibilité stockée par module (D9).
- Écritures MCP en proposition par défaut, écriture directe via scope dédié (D10).
- Champs d'intervalle `startsOn` / `endsBefore` dans l'API (§7).

**À trancher plus tard**

- Version LTS de .NET à figer dans un ADR au démarrage du code.
- Modèle local d'extraction : dépend du jeu d'évaluation et du matériel disponible (§9.4).
- Durée de conservation des transactions bancaires brutes (étape 5). Recommandation : ne garder que les observations rapprochées, l'application n'étant pas un outil de comptabilité.
- Cible distante de la sauvegarde : choix de l'hébergeur.
- Comportement de la bibliothèque de dates sur l'arithmétique des mois : à valider par tests (§6) ; non vérifié dans la documentation consultée.

## 17. Vérifications faites lors de la rédaction

Consultées dans la documentation à jour :

- **SDK MCP C#** : `AddMcpServer().WithHttpTransport(...)`, mode sans session par défaut, `MapMcp()` et `RequireAuthorization()`, `AddAuthorizationFilters()`.
- **Quartz.NET** : `AddQuartz` / `AddQuartzHostedService`, magasin persistant avec clustering, préfixe de tables qualifié par schéma pour Postgres.
- **Npgsql EF Core** : plugin `UseNodaTime()`, concurrence optimiste via une propriété `uint` mappée sur `xmin`.
