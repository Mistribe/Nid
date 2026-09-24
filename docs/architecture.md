# Architecture technique

> Statut : brouillon issu de la discussion technique. Les décisions marquées **retenu par défaut** n'ont pas été contestées mais ne sont pas explicitement confirmées : voir §16.
> Références : `Project.md` (vision, périmètre fonctionnel, décisions produit), `core-data-model.md` (modèle de données du Core), `subscription-data-model.md` (module Abonnements), `import-format.md` (format d'import et d'export), `identity-and-access/` (authentification, autorisation, jetons, audit), `frontend/` (SPA, écrans, design system, graphiques, hors ligne), `ingestion/llm-providers.md` (fournisseurs LLM et OCR), `security/` (modèle de menaces), `spikes.md` (résultats des spikes), `investments-data-model.md` et `realestate/` (modules Investments et RealEstate).

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
| D7 | Extraction de documents : **port pluggable** ; local (Ollama) par défaut, fournisseurs cloud sur opt-in (D23, D24) | Décidé |
| D8 | Jobs domaine en **Quartz.NET in-process** ; jobs d'ingestion en **workers conteneurisés** via table de jobs | Décidé (frontière) ; Quartz **retenu par défaut** |
| D9 | Registre **`Item` central** dans Core pour propriété et visibilité | **Retenu par défaut** |
| D10 | Écritures MCP : **proposition par défaut**, écriture directe via la portée `write:direct` | Décidé |
| D11 | Accès externe : **hors scope**, choix de l'hébergeur | Décidé |
| D12 | CQRS avec **Synapse** (`UnambitiousFx.Synapse` + générateur), résultats en `Result<T>` (`UnambitiousFx.Functional`) | Décidé |
| D13 | **Générateurs de sources en priorité** (JSON, Synapse, endpoints, EF, logs…) ; **Native AOT non visé** (§4.3) | Décidé |
| D14 | Frontière : requêtes Synapse aux points d'entrée et événements entre modules ; interfaces `Contracts` pour les lectures inter-modules et points d'extension | **Retenu par défaut** |
| D15 | Visibilité respectée par **filtrage applicatif** (pipeline, garde de sortie) et banc anti-fuite en CI ; pas de Row-Level Security | Décidé |
| D16 | Authentification MCP et automatisations par **jetons d'accès personnels** en v1 ; OAuth via Authelia écarté (pas d'enregistrement dynamique de clients) | Décidé |
| D17 | Import et export **orchestrés par le navigateur**, sessions d'import en lots avec staging côté serveur, **aucune tâche de fond** (`import-format.md` §11) | Décidé |
| D18 | **Thèmes** : quatre jeux de couleurs curés, préférence par personne côté serveur (`frontend/design-system.md` §7) | Décidé |
| D19 | Sources d'ingestion : **dépôt manuel seulement en v1** ; emails et banque à l'étape 4 ou 5, avec revue de risque préalable (`security/`) | Décidé |
| D20 | Extraction **sans outils** ; workers d'extraction **sans Internet ni clés d'API** | Décidé |
| D21 | **Client HTTP sortant unique** (`SafeHttpClient`) avec garde SSRF et liste d'hôtes (`security/application.md` §3) | Décidé |
| D22 | Emails **sans montants** par défaut | Décidé |
| D23 | Fournisseurs LLM multiples : offres gratuites éligibles, puis local, puis payant **avec accord** ; local par défaut ; consentement à chaque dépôt ; offres qui entraînent sur les données inéligibles (`ingestion/llm-providers.md`) | Décidé |
| D24 | **Passerelle LLM** dans `nid-api` : seule à contacter les fournisseurs, détient les clés, applique la politique | Décidé |
| D25 | **Aspire** (`Nid.AppHost`) orchestre la pile de **développement local** : API, frontend Vite, PostgreSQL, Authelia, proxy TLS. Le déploiement reste Docker Compose (D4) ; Aspire n'est ni déployé ni requis en production (§10.3) | Décidé |

## 4. Stack et topologie

### 4.1 Stack

| Couche | Choix | Remarque |
|---|---|---|
| Backend | .NET (dernière LTS au démarrage du projet, à figer dans un ADR), ASP.NET Core | `decimal` natif pour l'argent |
| Dates | NodaTime | `LocalDate`, `Instant` ; mapping via le plugin Npgsql |
| Accès données | EF Core + Npgsql | Un `DbContext` par module |
| API | REST + OpenAPI, versionnée, **Minimal APIs** | Client TypeScript généré ; Request Delegate Generator (§5.5) |
| Couche application | **Synapse** (CQRS, pipelines, événements, outbox) | `Result<T>` plutôt qu'exceptions (§5.4) |
| Sérialisation JSON | `System.Text.Json` en **source generation** | Un `JsonSerializerContext` par module (§5.5) |
| Frontend | React, TypeScript strict, Vite, Tailwind v4 + shadcn/ui, TanStack Query, client API généré | PWA, hors ligne limité au tableau de bord ; détail dans `frontend/` |
| Base | PostgreSQL | Un schéma par module |
| Authentification | Authelia (OIDC), l'application est cliente OIDC | SPA via BFF, cookie de session |
| Jobs domaine | Quartz.NET in-process, magasin persistant Postgres | Voir §9 |
| Workers | Conteneurs, techno libre (Python pour l'extraction) | Voir §9 |
| LLM | Ollama (local) et fournisseurs cloud optionnels (Gemini, OpenAI, Anthropic), derrière la passerelle LLM | Local par défaut ; voir `ingestion/llm-providers.md` |
| Environnement local | Aspire (AppHost .NET) | Lance la pile de développement d'une commande ; tableau de bord OpenTelemetry ; voir §10.3 |
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

### 4.3 Native AOT : évalué et écarté

Les générateurs de sources sont la règle (D13), mais **`nid-api` n'est pas publié en Native AOT**. État constaté dans la documentation :

| Brique | Support Native AOT |
|---|---|
| ASP.NET Core | Minimal APIs : partiel ; MVC non supporté ; « Other Authentication » non supporté (seul JWT l'est) |
| **Authentification OIDC + cookie (BFF, D6)** | **Bloquant** : non supporté, il faudrait réécrire le flux à la main |
| **EF Core** | Expérimental, « pas adapté à la production » (page EF 9 ; stabilisation annoncée pour EF 10, non vérifiée). Requêtes dynamiques non supportées |
| Npgsql | Compatible depuis 8.0 ; JSON dynamique non compatible (d'où la source generation JSON) |
| **Quartz.NET** | Pas prêt : chargement de types par chaîne, réflexion sans annotations |
| SDK MCP C# | `WithToolsFromAssembly` annoté `RequiresUnreferencedCode` ; enregistrement explicite possible |
| Synapse | Conçu pour l'AOT, sans réflexion à l'exécution |

Remplacer EF, Quartz et l'authentification OIDC coûterait cher (migrations par module, `xmin`, audit par interceptor…) pour un gain qui ne compte pas ici : démarrage sans importance sur un conteneur permanent pour deux utilisateurs, mémoire dominée par Postgres et Ollama.

Ce qu'on garde de l'idée :

- **Requêtes précompilées EF sans NativeAOT** (`dotnet ef dbcontext optimize --precompile-queries`, documenté), si le démarrage gêne.
- **Analyseurs AOT/trimming activés** sur `Domain` et `Application` (propriété `IsAotCompatible`, à vérifier) pour ne pas fermer la porte gratuitement.
- **Enregistrement explicite** des modules et des outils MCP, pas de scan d'assemblies.
- **ReadyToRun** et images .NET « chiseled » : pistes à mesurer, non évaluées.
- **AOT ciblé** possible pour un petit binaire .NET autonome (outil d'export, healthcheck) si le besoin apparaît.

À revoir si EF Core stabilise son support AOT **et** que l'authentification OIDC/cookie devient supportée.

## 5. Modules et propriété des données

### 5.1 Règle de base

Un module est composé de ses projets .NET (`Contracts`, `Domain`, `Application`, `Infrastructure`), de son **schéma Postgres** et de son `DbContext`. Aucun module ne lit les tables d'un autre ni ne référence son domaine ; ils communiquent par leurs `Contracts` (interfaces et DTO). Des **tests d'architecture** (NetArchTest) le vérifient en CI.

### 5.2 Modules

Le modèle de données du Core est détaillé dans `core-data-model.md`, celui d'Investments dans `investments-data-model.md`, celui de RealEstate dans `realestate/` (`README.md` : bien, location, coûts ; `loans.md` : crédits), celui de Revenus dans `income-data-model.md`.

| Module | Possède | Expose (Contracts) | Dépend de |
|---|---|---|---|
| **Core** | `Person` (`adult` lié au `sub` OIDC, ou `dependent` sans connexion), `Item` (registre), `Ownership` (part par personne), visibilité, `Valuation`, `Flow`, `Document`, `Proposal` (file de validation), provenance, `fx_rate` | `IAccessPolicy`, `IVisibleItems`, types `Money`/`Duration`/`DateRange`, `IValuations`, `IFlows`, `IProposals` | rien |
| **Subscriptions** | `Subscription`, `BillingPhase`, `Purchase`, `Grant`, caches dérivés `CoveragePeriod`/`Charge` | Contributions au tableau de bord (coût mensuel réel/amorti, échéances), charges attendues | Core |
| **Investments** | `Envelope` (PEA, assurance-vie, PER, livret…) ; valorisations et flux via Core | Gain €/%, TRI plus tard, contribution au patrimoine | Core |
| **Income** | `IncomeSource`, `IncomePhase` ; revenus exceptionnels via les flux du Core | Revenus mensuels lissés, part engagée, marge (`IIncomeContribution`, `IIncomeSummary`) | Core |
| **RealEstate** | `Property`, `Loan`, `LoanPhase`, `LoanEvent`, `Insurance` | Tableau d'amortissement, capital restant dû, valeur nette, engagements | Core |
| **Ingestion** | Pipeline de documents, table `job`, contrats de jobs, résultats d'extraction | Entrée des propositions vers Core, API interne pour les workers | Core |
| **Dashboard** | Rien (lecture seule, composition) | Agrégats patrimoine / engagements / évolution | Core + `Contracts` des autres |
| **Platform** (technique) | Notifications, audit, sessions d'import (staging), planification | `INotificationChannel` ; les modules implémentent `IImportSection` (`import-format.md` §11.7) | Tous, via contrats |

### 5.3 Décisions de conception

1. **Registre `Item` central (Core).** Chaque bien, abonnement, enveloppe ou crédit a une ligne `Item(id, module, kind, visibility)` et ses `Ownership`. Le module garde ses données et référence `item_id` (lien logique, pas de clé étrangère entre schémas). `IAccessPolicy` applique les règles de `Project.md` §5.1 (privé, lecture seule pour l'autre, totaux du foyer hors privé) pour l'API, le tableau de bord et le MCP : un seul endroit à auditer.
2. **Tableau de bord par contributions enfichables.** Chaque module implémente `INetWorthContribution`, `ICommitmentsContribution`, `IIncomeContribution`, `IUpcomingEvents`, découvertes par injection de dépendances. Un nouveau module apporte de nouveaux fournisseurs sans modifier Dashboard. La courbe d'évolution est **calculée à la demande** à partir des valorisations, flux et capitaux restants dus ; des instantanés matérialisés ne sont ajoutés que si un besoin réel apparaît.
3. **File de validation dans Core, application dans le module.** Une `Proposal` porte un payload typé, sa provenance et son document source. À la validation, Core appelle l'`IProposalHandler` du module concerné, qui écrit dans son schéma. Ingestion ne touche jamais aux données métier.
4. **`Valuation` et `Flow` génériques (Core)** pour Investments et RealEstate. Subscriptions conserve ses propres `Purchase` / `BillingPhase` : sa logique de couverture n'est pas un `Flow`.
5. **Calculs = fonctions pures dans `Domain`** (timeline, amortissement, gain, TRI) : aucune entrée/sortie, aucune IA. Les caches dérivés sont reconstruisibles à tout moment.
6. **Communication** : requêtes Synapse aux points d'entrée, interfaces `Contracts` pour les lectures inter-modules, événements Synapse in-process (avec outbox) pour les réactions (ex. abonnement modifié → recalcul du cache). Pas de bus externe. Détail au §5.4.

### 5.4 Couche application : CQRS avec Synapse

Bibliothèque : `UnambitiousFx.Synapse` et `UnambitiousFx.Synapse.Generator` (MIT ; net8, net9, net10). Médiateur in-process sans réflexion à l'exécution, `ValueTask`, retours en `Result<T>` (`UnambitiousFx.Functional`).

- **Un cas d'usage = une requête + un handler** `[RequestHandler<TRequest, TResponse>]`. Le générateur produit, par assembly, une classe `RegisterGroup` ; le host appelle `cfg.AddRegisterGroup(new …RegisterGroup())` pour chaque module. L'enregistrement est **explicite par module** : un module absent n'est pas chargé.
- **Commande ou requête** : Synapse n'expose que `IRequest` et `IRequest<T>` (vérifié, `spikes.md` §3). Le projet définit ses marqueurs `ICommand` / `IQuery`, imposés par un test d'architecture.
- **Erreurs attendues = `Result.Failure`**, jamais d'exception. **Vérifié** : le type `Failure` porte un `Code`, un message et des métadonnées, avec des variantes (`NotFoundFailure`, `UnauthorizedFailure`, `UnauthenticatedFailure`, `ValidationFailure`, `TimeoutFailure`) et des codes de la bibliothèque (`VALIDATION`, `NOT_FOUND`, `CONFLICT`, `UNAUTHORIZED`, `UNAUTHENTICATED`, `BAD_REQUEST`, `TIMEOUT`, `ERROR`). On les utilise tels quels et on ajoute des codes propres préfixés par module (`things.name_required`). Ils sont traduits à un seul endroit en Problem Details / statut HTTP et en erreur d'outil MCP ; `UnambitiousFx.Functional.AspNetCore` fournit `AddResultHttp` avec des mappeurs par type d'échec.
- **Pipelines**, dans cet ordre cible : trace / journalisation → acteur (personne, origine API / MCP / job, « agissant pour ») → portée (avec conversion en `Proposal`, D10) → accès à l'élément (`IAccessPolicy`) → `Idempotency-Key` → validation → transaction → handler → garde de sortie (`identity-and-access/authorization.md` §2). Droits privé/partagé, audit et provenance vivent dans ces comportements, pas dans les handlers. **Vérifié** (`spikes.md` §3) : l'ordre suit `IOrderedPipelineBehavior.Order` (valeur basse = extérieur), quel que soit l'ordre d'enregistrement, et un comportement peut court-circuiter avec un `Result.Failure`. **`[PipelineBehavior]` se propage vers le bas du graphe de références**, or les modules référencent `Platform` : les comportements communs sont donc déclarés **par le host** avec `[assembly: SynapseGlobalBehavior(typeof(…<,>))]`, et le `RegisterGroup` du host doit être enregistré (sans lui, rien ne s'exécute et rien n'échoue). Un test d'architecture vérifie qu'une requête de chaque module traverse toute la chaîne.
- **Contexte** : `IContextAccessor` et ses « features » transportent l'identité et l'origine de l'appel d'un comportement à l'autre.
- **Événements** : `IEmitter` et `IEventHandler<T>`. Avec `EmitMode.Outbox` et `IOutboxCommit`, l'événement n'est diffusé qu'**après** la réussite de l'écriture. **Aucun paquet EF Core n'est publié** : nous écrivons dans Platform un `IEventOutboxStorage` (11 méthodes, dont relances et lettres mortes), **dans la même transaction que l'écriture du module**. Vérifié (`spikes.md` §6) : avec des `DbContext` qui partagent connexion et transaction, le retour arrière efface l'entrée et une autre session ne la voit qu'après le commit. Le stockage **en mémoire** fourni ne doit **jamais** servir en production : l'événement d'une commande échouée reste en attente et part avec le commit d'une autre requête. `IOutboxCommit.CommitAsync` diffuse tout ce qui est en attente.
- **Frontière avec les Contracts (D14)** :
  - Synapse : points d'entrée (API REST, outils MCP, jobs Quartz, application des propositions) et événements entre modules.
  - Interfaces `Contracts` : lectures synchrones inter-modules et points d'extension à plusieurs fournisseurs (`IAccessPolicy`, `IVisibleItems`, `INetWorthContribution`…). Un fan-out n'est pas une requête unique, et `IAccessPolicy` est sur le chemin critique.
- **`Domain` ne référence pas Synapse** : fonctions pures, remplaçables sans toucher aux règles métier.
- **MCP** : les outils envoient les mêmes requêtes, donc traversent le même pipeline. La règle « une écriture MCP devient une `Proposal` sauf scope `write:direct` » (D10) peut devenir un comportement : une commande marquée `IProposable`, appelée depuis le MCP sans ce scope, crée une proposition au lieu de s'exécuter. Idée à valider au prototypage.

### 5.5 Générateurs de sources

Principe : générer à la compilation plutôt que réfléchir à l'exécution ; pas de scan d'assemblies.

| Besoin | Générateur | Vérification |
|---|---|---|
| Handlers, comportements, événements | Synapse.Generator | Documentation lue |
| JSON (API, MCP, payloads JSONB, contrats de jobs) | `System.Text.Json` source generation, un `JsonSerializerContext` par module | Npgsql : JSON dynamique non compatible AOT/trimming (vérifié). NodaTime avec source generation : **à vérifier** |
| Endpoints | Minimal APIs + Request Delegate Generator (activable hors AOT via `EnableRequestDelegateGenerator`) | Documentation Learn lue |
| EF Core | Modèle compilé + requêtes précompilées, sans NativeAOT | Documentation Learn lue. Limites : requêtes dynamiques non précompilées, syntaxe « query expression » non supportée |
| Journalisation | `[LoggerMessage]` | Fonction standard .NET, non revérifiée |
| Configuration et options | Générateur de binding de configuration, `[OptionsValidator]` | Non revérifié |
| Expressions régulières | `[GeneratedRegex]` | Non revérifié |
| Mapping de DTO | Candidat : Mapperly | Non vérifié, à évaluer |
| Identifiants typés, objets valeur (`Money`, `ItemId`) | Candidats : Vogen, StronglyTypedId | Non vérifié, à évaluer |

## 6. Conventions de données

| Sujet | Décision |
|---|---|
| Argent | `Money(decimal, Currency)`. Postgres `numeric(19,4)` + `char(3)`. Jamais de flottant. Devise d'origine stockée, conversion à l'affichage. **Postgres arrondit en silence** au-delà de 4 décimales (0,00005 devient 0,0001, vérifié) : `Money` refuse plus de 4 décimales. |
| Taux de change | Table `fx_rate` dans Core, devise pivot USD, alimentée par un job Quartz depuis le dépôt `Mistribe/SubTracker-Data`, avec saisie manuelle en secours. Conversion au taux de la date concernée. Détail dans `core-data-model.md` §8. |
| Arrondis | Précision pleine en interne, arrondi à l'affichage. Crédit : politique d'arrondi **configurable par prêt** pour coller au tableau de la banque, qui fait référence. |
| Dates | NodaTime `LocalDate` → `date`. Instants en `timestamptz` UTC. Intervalles **semi-ouverts** `[début, fin)` ; `daterange` Postgres quand utile. |
| Durées | `{count, unit}` (`day`/`week`/`month`/`year`), jamais converties en jours. Arithmétique des mois avec jour d'ancrage et écrasement en fin de mois (voir `subscription-data-model.md`). **Vérifié avec NodaTime** : l'occurrence k se calcule par `début.PlusMonths(k × n)` **depuis l'ancre**, jamais mois après mois (31/01 puis « + 1 mois » deux fois donne le 28/03 au lieu du 31/03). Tests dédiés (31 janvier, années bissextiles). |
| Identifiants | UUIDv7 générés côté application, triés **à la milliseconde** : à l'intérieur d'une même milliseconde l'ordre est aléatoire (vérifié), donc jamais une séquence ; départage par `created_at`. |
| Culture | `InvariantCulture` pour toute conversion décimale ou date en texte. L'hôte est en `fr-FR` : sans cela, `665262.12` ne s'analyse pas (vérifié). |
| Colonnes | `snake_case` par convention de nommage EF (paquet communautaire `EFCore.NamingConventions` ou convention maison) : EF produit du `PascalCase` par défaut. |
| Valorisations et flux | **Append-only** : une correction crée une nouvelle ligne (`supersedes_id`) et retire l'ancienne (`retired_at`) dans la même transaction. Historique conservé. Détail dans `core-data-model.md` §5.4. |
| Provenance | Colonnes communes sur toute donnée saisie ou importée : `source`, `created_via`, `document_id?`, `proposal_id?`, `created_by_person_id`. Valeurs dans `core-data-model.md` §5.1. |
| Audit | Table `audit_log` append-only (Platform) : qui, quand, entité, action, diff JSONB. Écrite par un interceptor EF. Enregistre aussi « agissant pour » quand l'acteur est le MCP. |
| Suppression | Suppression logique (`deleted_at`) sur les agrégats utilisateur ; purge réelle par action explicite. |
| Concurrence | Verrou optimiste via la colonne système `xmin`. Conflit → HTTP 409. |
| Documents | Stockés par contenu : `/data/documents/{sha[0:2]}/{sha256}`, dédupliqués. Métadonnées en base. Chiffrement au repos = disque de l'hôte ; sauvegarde chiffrée. |
| Secrets | Jetons et consentements bancaires chiffrés avec ASP.NET Data Protection ; clés sur volume sauvegardé. |

## 7. API

- REST JSON `/api/v1/{module}/…`, OpenAPI généré, **client TypeScript généré** pour le SPA. Erreurs en Problem Details (RFC 9457).
- Sur le fil : montants en **chaînes** (`{"amount":"12.34","currency":"EUR"}`), dates ISO 8601. Intervalles en champs explicites `startsOn` / `endsBefore` (le `ends_on` du modèle d'abonnements est ambigu et n'a été précisé « semi-ouvert » qu'après coup). **Retenu par défaut.**
- **Authentification** : le SPA passe par un **BFF** (code + PKCE côté serveur, cookie `HttpOnly`, protection CSRF). L'API accepte aussi des jetons bearer (MCP, automatisations) avec les portées `read`, `write` et `write:direct`. Détail dans `identity-and-access/`.
- Écritures idempotentes via l'en-tête `Idempotency-Key` (workers, MCP).
- Couche application en CQRS avec **Synapse** (§5.4). Chaque endpoint se limite à traduire HTTP → requête Synapse → `Result` → réponse (Problem Details en cas d'échec). L'idempotence (`Idempotency-Key`) est un comportement de pipeline.
- **API interne des workers** : groupe de routes `/internal/v1/jobs/…` sur un port / listener séparé, non routé par le proxy, avec identifiants de service par worker.

## 8. Serveur MCP

- Endpoint `/mcp`, transport Streamable HTTP, dans le même processus, avec le SDK C# officiel (`ModelContextProtocol.AspNetCore`). Mode sans session (par défaut, recommandé tant qu'il n'y a pas de requêtes serveur → client). Autorisation via les filtres d'autorisation du SDK, `MapMcp()` protégé par `RequireAuthorization()`.
- Les outils sont de **minces enveloppes** qui envoient les mêmes requêtes Synapse que REST : même pipeline, même `IAccessPolicy`, mêmes droits ; un élément privé reste privé. Outils enregistrés **explicitement** (`WithTools<T>()`), pas par scan (`WithToolsFromAssembly` est annoté `RequiresUnreferencedCode`).
- **Lectures** (`net_worth`, `list_subscriptions`, `loan_schedule`, `envelope_performance`…) : chiffres calculés par les services déterministes, avec provenance. Le modèle ne calcule rien.
- **Écritures** : par défaut, elles créent une `Proposal` dans la file de validation. Un jeton peut recevoir le scope `write:direct` quand l'utilisateur dicte lui-même la saisie. **Retenu par défaut.**
- Authentification : **jeton d'accès personnel** révocable, avec portées `read`, `write`, `write:direct` (`identity-and-access/tokens-and-credentials.md`). OAuth via Authelia écarté en v1.
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

Avant d'engager un modèle : constituer un **jeu d'évaluation** de vrais relevés avec le JSON attendu pour mesurer la précision par champ. Le choix du modèle relève de la configuration, pas de l'architecture. L'extracteur est un port (`IDocumentExtractor`). Le modèle est appelé **par la passerelle LLM** de `nid-api` (D24) : Ollama en local, ou un fournisseur cloud **sur opt-in à chaque dépôt de document**, tracé dans la provenance et dans `llm_call`. Politique, consentement, budget, masquage et clés : `ingestion/llm-providers.md`.

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

- Identifiants de service **par worker**, limités à ses types de jobs. Réseau compose segmenté : un worker d'extraction n'atteint que l'API interne (et, par elle, la passerelle LLM) ; **sans Internet, sans clés d'API**.
- Les workers de **connecteurs** (mail, banque, woob), à l'étape 4 ou 5, exigent une sortie limitée à leurs hôtes et une **revue de risque préalable** (`security/risk-register.md` §7).
- Aucun document ne quitte l'infrastructure par défaut ; tout envoi cloud exige le consentement du déposant (`ingestion/llm-providers.md` §6).
- Menaces et mesures détaillées : `security/ai-ingestion.md`.

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
- une URL publique configurable (`PUBLIC_URL`) utilisée pour les redirections OIDC, les liens des emails et le manifeste PWA ;
- **une seule origine** : le proxy route `/api`, `/auth`, `/mcp` et `/health` vers `nid-api`, et le reste vers `nid-web`, sur le même nom d'hôte (cookie de session du BFF, pas de CORS) ;
- un accès sortant HTTPS vers le dépôt `SubTracker-Data` (taux de change et catalogue de fournisseurs) et vers l'historique de la Banque centrale européenne (`www.ecb.europa.eu`, taux de change depuis 1999), URL configurable, miroir ou dossier local possibles. Sans lui, les taux se saisissent à la main et les fournisseurs sont créés à la main.

Un exemple de reverse proxy peut figurer dans `deploy/`, à titre optionnel.

### 10.3 Environnement de développement local (Aspire)

Le projet `Nid.AppHost` (Aspire) décrit en C# toute la pile de développement et la lance d'une seule commande, `aspire run` (ou `dotnet run --project src/Nid.AppHost`). Il **remplace** un compose de développement écrit à la main : dépendances, ordre de démarrage, ports et chaînes de connexion sont déclarés une fois, dans le code.

| Ressource | Nature | Rôle |
|---|---|---|
| `postgres` | conteneur (`AddPostgres`, volume de données, base `nid`) | Base ; chaîne de connexion injectée dans `nid-api` |
| `authelia` | conteneur (configuration montée en lecture seule depuis `deploy/dev/authelia`) | OIDC de développement, trois utilisateurs jetables |
| `nid-api` | projet .NET (`Nid.Host`), port fixe 5080 non proxifié | API + BFF ; attend `postgres` ; migrations au démarrage |
| `nid-web` | application Vite (`/web`, `AddViteApp`) | Rechargement à chaud du frontend |
| `caddy` | conteneur | Proxy TLS mono-origine : `/api`, `/auth`, `/mcp`, `/health` vers `nid-api`, le reste vers `nid-web` ; reproduit §10.2 |

- **Pourquoi un proxy en développement** : Authelia n'accepte que le HTTPS et refuse `localhost` comme domaine de cookie ; la pile répond donc sur `https://nid.localtest.me` et `https://auth.localtest.me` (`identity-and-access/authentication.md`). Le proxy garantit aussi la mono-origine du BFF (pas de CORS).
- **Tableau de bord Aspire** : journaux, traces et métriques OpenTelemetry de tous les services au même endroit ; il tient lieu de backend OTLP local (§12). Les projets .NET référencent `Nid.ServiceDefaults` (OpenTelemetry, health checks, résilience HTTP) ; les `/health/*` de §12 en font partie.
- **Secrets** : les secrets de développement sont des paramètres Aspire (`AddParameter`, `secret: true`, user-secrets) ou les fichiers jetables générés par `deploy/dev/setup.sh`. Rien ne part en production.
- **Tests d'intégration** : ils gardent Testcontainers (§13), indépendants de l'AppHost. `Aspire.Hosting.Testing` reste possible plus tard pour les E2E Playwright, qui démarreraient alors la même pile.
- **Prérequis** : SDK .NET (`global.json`), CLI Aspire, Docker (ou Podman), Node pour Vite.
- **Limite** : l'AppHost est un outil de développement. Le compose de référence (§10.1) reste la seule forme livrée ; `aspire publish` n'est pas utilisé.

## 11. Sauvegarde et export

- **restic**, chiffré, règle 3-2-1 : cible locale + cible distante au choix de l'hébergeur (S3, SFTP, autre disque).
- Contenu : dump Postgres (`pg_dump` cohérent, pas de copie à chaud du répertoire de données) + volume `documents` + clés Data Protection + configuration Authelia. **Sans les clés Data Protection, les jetons bancaires chiffrés sont perdus.**
- **Test de restauration** documenté et scripté (restauration dans un compose jetable).
- Alerte email si la sauvegarde échoue ou dépasse N jours.
- **Export complet** JSON/CSV à la demande, format documenté et versionné, pour ne jamais dépendre de l'application. **Assemblé par le navigateur** à partir de pages fournies par le serveur (manifeste, pages de 500 entités, `import-format.md` §10), sans fichier construit côté serveur. Respecte la visibilité : chacun exporte ce qu'il voit.
- **Import** : le même format que l'export JSON, spécifié dans `import-format.md`. Session en lots avec staging, validation complète par le serveur, écriture en une transaction, idempotence ; **orchestrée par le navigateur, sans tâche de fond**. Sert à la reprise initiale des données existantes et au transfert entre instances.

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
| **Banc anti-fuite** | Jeu de données mixte, toutes les requêtes exécutées en tant que chaque adulte, tests de propriétés sur `Readers` / `Writers`, test de la garde de sortie. Bloquant en CI (`identity-and-access/access-tests.md`). |
| **Sécurité** | SSRF du `SafeHttpClient`, injection de formules CSV, uploads, import malveillant, corpus de PDF piégés, égress des workers, politique de la passerelle LLM (offres inéligibles, consentement, budget), clés jamais exposées (`security/ai-ingestion.md` §7, `security/application.md` §9). |
| **Frontend** | Lint (dont l'interdiction d'arithmétique sur les montants), tests de composants, E2E avec un Authelia de test, accessibilité, contraste de tous les thèmes, validation de la palette de graphiques, budgets de taille (`frontend/testing.md`). |
| **Contrats** | Schémas de jobs validés avec des exemples ; chaque type de job possède un schéma d'entrée et de sortie. |
| **Architecture** | NetArchTest : aucune dépendance entre modules hors `Contracts` ; `Domain` sans référence à l'infrastructure ni à Synapse ; toute implémentation de handler porte `[RequestHandler]` (le générateur est la source de vérité : sans l'attribut, le handler n'est jamais enregistré, sans erreur). |
| **Extraction** | Jeu d'évaluation de relevés, mesuré hors CI standard (précision par champ). |
| **E2E** | Playwright sur les parcours clés (saisie mensuelle, validation d'une proposition, tableau de bord). |

## 14. Structure du dépôt (monorepo)

```
/src
  /Nid.Core/{Contracts,Domain,Application,Infrastructure}
  /Nid.Subscriptions/…   /Nid.Investments/…   /Nid.RealEstate/…   /Nid.Income/…
  /Nid.Ingestion/…       /Nid.Dashboard/…     /Nid.Platform/…
  /Nid.Host              (racine de composition : API, BFF, MCP, Quartz)
  /Nid.AppHost           (Aspire : pile de développement local, §10.3)
  /Nid.ServiceDefaults   (OpenTelemetry, health checks, résilience HTTP)
/web                     (React + TS + Vite PWA, client généré)
/workers/{extract-python,…}
/contracts/jobs/…        (schémas JSON versionnés)
/deploy/compose          (compose de référence, exemples d'environnement)
/deploy/dev              (configuration Authelia et Caddy de développement, setup.sh, smoke.sh ; lancés par Nid.AppHost)
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
- Champs d'intervalle `startsOn` / `endsBefore` dans l'API (§7).
- Frontière Synapse / interfaces `Contracts` (D14, §5.4).
- Minimal APIs plutôt que contrôleurs MVC (§5.5).

**Risque : maturité de Synapse (D12)**

Au moment de la vérification : version 2.0.1 publiée le 13/08/2026, environ 3 000 téléchargements au total, 2 étoiles GitHub, 1 issue ouverte. Adoption très faible, donc risque de maintenance. Atténuations :

- licence MIT (fork possible) ;
- usage limité à `Application` et au host ; les règles métier restent dans `Domain`, pur et sans dépendance à Synapse ;
- l'API des handlers (`IRequestHandler`, `Result<T>`) est présente dans chaque cas d'usage : un remplacement serait mécanique mais large. À garder en tête avant de multiplier les comportements de pipeline spécifiques.

**Vérifié par les spikes** (`spikes.md`)

- Modèle d'erreur de `Result` avec code : oui.
- Ordre des comportements : par `IOrderedPipelineBehavior.Order` ; comportements communs enregistrés par le host.
- Marqueurs `ICommand` / `IQuery` : à définir (Synapse n'a que `IRequest`).
- Outbox avec un `DbContext` par module : faisable avec une transaction partagée ; stockage transactionnel à écrire.
- NodaTime avec `System.Text.Json` en source generation, schéma JSON, arithmétique de mois, RFC 8785, échéancier de prêt : voir `spikes.md`.

**Reste à vérifier**

- Propriété `IsAotCompatible` et analyseurs associés sur `Domain` / `Application`.
- Migrations de plusieurs `DbContext`, Quartz sur Postgres, SDK MCP en exécution (`spikes.md` §9).

**À trancher plus tard**

- Version LTS de .NET à figer dans un ADR au démarrage du code.
- Modèle local d'extraction : dépend du jeu d'évaluation et du matériel disponible (§9.4).
- Durée de conservation des transactions bancaires brutes (étape 5). Recommandation : ne garder que les observations rapprochées, l'application n'étant pas un outil de comptabilité.
- Cible distante de la sauvegarde : choix de l'hébergeur.
- Fournisseurs LLM : SDK officiels ou appels HTTP directs ; choix des modèles après le jeu d'évaluation ; revérification des conditions des fournisseurs (`ingestion/llm-providers.md` §14).
- Revue de risque avant IMAP, email et banque (`security/README.md` §9).
- OAuth via Authelia pour le MCP : à revoir si un client utilisé refuse le jeton statique (`identity-and-access/tokens-and-credentials.md` §5).
- Flux OIDC avec Authelia **exécuté de bout en bout** (`spikes.md` §8) : `client_secret_post`, PAR désactivé et `GetClaimsFromUserInfoEndpoint = true` sont nécessaires ; `name` et `email` ne sortent que par `userinfo`.

## 17. Vérifications faites lors de la rédaction

Consultées dans la documentation à jour :

- **SDK MCP C#** : `AddMcpServer().WithHttpTransport(...)`, mode sans session par défaut, `MapMcp()` et `RequireAuthorization()`, `AddAuthorizationFilters()`.
- **Quartz.NET** : `AddQuartz` / `AddQuartzHostedService`, magasin persistant avec clustering, préfixe de tables qualifié par schéma pour Postgres.
- **Npgsql EF Core** : plugin `UseNodaTime()`, concurrence optimiste via une propriété `uint` mappée sur `xmin`.
- **Synapse** (documentation et NuGet) : `[RequestHandler<,>]` et `RegisterGroup` générés par assembly, `[PipelineBehavior]`, validation, événements avec outbox (stockage transactionnel à écrire, pas de paquet EF Core : `spikes.md` §3), contexte par « features », MIT, net8 à net10.
- **Native AOT** (Microsoft Learn, Npgsql, Quartz.NET, SDK MCP) : voir §4.3.
- **EF Core** : modèle compilé et requêtes précompilées utilisables sans NativeAOT.
- **Spikes exécutés** : Synapse, NodaTime et JSON, RFC 8785, échéancier de prêt, EF Core et PostgreSQL (`spikes.md`).
