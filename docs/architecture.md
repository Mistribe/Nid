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
| D6 | Authentification par **OIDC externe, Pocket ID** : l'accès à Nid et le rôle admin viennent des **groupes** du fournisseur (`nid-users`, `nid-admins`), appliqués à chaque connexion. Choisi à la place d'Authelia, retenu d'abord : un foyer qui accumule des services veut un endroit où voir les utilisateurs, leurs groupes et ce que chaque groupe atteint, sur un standard (claim OIDC `groups`) que les autres services reprennent ; Pocket ID le donne dans un conteneur léger (SQLite, passkeys, groupes, accès par client). Revue : `spikes.md` §9septies | Décidé |
| D7 | Extraction de documents : **port pluggable** ; local (Ollama) par défaut, fournisseurs cloud sur opt-in (D23, D24) | Décidé |
| D8 | Jobs domaine en **Quartz.NET in-process** ; jobs d'ingestion en **workers conteneurisés** via table de jobs | Décidé (frontière) ; Quartz **retenu par défaut** |
| D9 | Registre **`Item` central** dans Core pour propriété et visibilité | **Retenu par défaut** |
| D10 | Écritures MCP : **proposition par défaut**, écriture directe via la portée `write:direct`. **Propositions explicites** (décidé 2026-10-16) : une commande « soumettre une proposition » exige `write` ; toute commande directe exige `write:direct` ; le comportement `IProposable` est abandonné. **Outils d’écriture MCP** (4D1) : un par usage, `ApplyDirectCommand` (`IDirectWrite`, `write:direct`) pour l’écriture directe, « conforme » sans écriture | Décidé |
| D11 | Accès externe : **hors scope**, choix de l'hébergeur | Décidé |
| D12 | CQRS avec **Synapse** (`UnambitiousFx.Synapse` + générateur), résultats en `Result<T>` (`UnambitiousFx.Functional`) | Décidé |
| D13 | **Générateurs de sources en priorité** (JSON, Synapse, endpoints, EF, logs…) ; **Native AOT non visé** (§4.3) | Décidé |
| D14 | Frontière : requêtes Synapse aux points d'entrée et événements entre modules ; interfaces `Contracts` pour les lectures inter-modules et points d'extension | **Retenu par défaut** |
| D15 | Visibilité respectée par **filtrage applicatif** (pipeline, garde de sortie) et banc anti-fuite en CI ; pas de Row-Level Security | Décidé |
| D16 | Authentification MCP et automatisations par **jetons d'accès personnels** en v1 ; OAuth via le fournisseur d'identité écarté (pas d'enregistrement dynamique de clients) | Décidé |
| D17 | Import et export **orchestrés par le navigateur**, sessions d'import en lots avec staging côté serveur, **aucune tâche de fond** (`import-format.md` §11) | Décidé |
| D18 | **Thèmes** : quatre jeux de couleurs curés, préférence par personne côté serveur (`frontend/design-system.md` §7) | Décidé |
| D19 | Sources d'ingestion : **dépôt manuel seulement en v1** ; emails et banque à l'étape 4 ou 5, avec revue de risque préalable (`security/`) | Décidé |
| D20 | Extraction **sans outils** ; workers d'extraction **sans Internet ni clés d'API** | Décidé |
| D21 | **Client HTTP sortant unique** (`SafeHttpClient`) avec garde SSRF et liste d'hôtes (`security/application.md` §3) | Décidé |
| D22 | Emails **sans montants** par défaut | Décidé |
| D23 | Fournisseurs LLM multiples : offres gratuites éligibles, puis local, puis payant **avec accord** ; local par défaut ; consentement à chaque dépôt ; offres qui entraînent sur les données inéligibles (`ingestion/llm-providers.md`) | Décidé |
| D24 | **Passerelle LLM** dans `nid` : seule à contacter les fournisseurs, détient les clés, applique la politique | Décidé |
| D25 | **Aspire** (`Nid.AppHost`) orchestre la pile de **développement local** : API, frontend Vite, PostgreSQL, Pocket ID, proxy TLS. Le déploiement reste Docker Compose (D4) ; Aspire n'est ni déployé ni requis en production (§10.3) | Décidé |

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
| Authentification | Pocket ID (OIDC), l'application est cliente OIDC | SPA via BFF, cookie de session ; accès et rôle admin par les groupes |
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
        └──> nid       (.NET : SPA statique + API + BFF + MCP + Quartz, un seul port)
                │  ▲
   OIDC ◄───────┘  │ API interne (port séparé, non routé par le proxy)
   Pocket ID       │
                   ├── nid-worker-* (conteneurs d'ingestion)
                   ├── postgres
                   ├── volume documents
                   └── ollama (optionnel)

backup (restic, chiffré) ──> cible locale + cible distante
```

Un seul processus `nid` porte l'API, le BFF, l'endpoint MCP et les tâches planifiées. On ne le scinde que si un besoin réel apparaît.

### 4.3 Native AOT : évalué et écarté

Les générateurs de sources sont la règle (D13), mais **`nid` n'est pas publié en Native AOT**. État constaté dans la documentation :

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

Un module est composé de ses projets .NET (`Contracts`, `Domain`, `Application`, `Infrastructure`), de son **schéma Postgres** et de son `DbContext`. Aucun module ne lit les tables d'un autre ni ne référence son domaine ; ils communiquent par leurs `Contracts` (interfaces et DTO). Un **test d'architecture** (`tests/Nid.Platform.Tests/ModuleBoundaryTests.cs`) le vérifie sur chaque pull request, à partir des `ProjectReference` et `PackageReference` de `src/*/*.csproj` : un module ne référence que ses propres projets, `Core.Contracts` et `Platform` (`Platform`, `Platform.Contracts`, `Platform.Infrastructure`), et son `Application` peut en plus référencer le `Contracts` d'un autre module, sans cycle entre modules ; un `Domain` ne référence que son `Contracts` et aucun paquet ; un `Contracts` ne référence aucun `Domain`, `Application` ni `Infrastructure` ; seuls `Nid.Host` et `Nid.AppHost` référencent les projets d'un module depuis l'extérieur, en dehors de ces `Contracts`.

**Où va un contrat.** `Core.Contracts` n'accueille que le noyau partagé (`Money`, `ItemId`, registre, visibilité, faits, propositions…) et les points d'extension que Core agrège (contributions au tableau de bord, `ITaxContribution`, `IFactConventions`). Un contrat qu'un module lit chez un seul autre va dans le `Contracts` du module qui le fournit, et le lecteur le référence : `IRentIncome` est dans `RealEstate.Contracts`, et Income le lit. Sans cette règle, Core finirait par connaître le vocabulaire de chaque module.

### 5.2 Modules

Le modèle de données du Core est détaillé dans `core-data-model.md`, celui d'Investments dans `investments-data-model.md`, celui de RealEstate dans `realestate/` (`README.md` : bien, location, coûts ; `loans.md` : crédits), celui de Revenus dans `income-data-model.md`, celui d'Objectifs dans `goals-data-model.md`, celui de Documents dans `documents-data-model.md`.

| Module | Possède | Expose (Contracts) | Dépend de |
|---|---|---|---|
| **Core** | `Person` (`adult` lié au `sub` OIDC, ou `dependent` sans connexion), `Item` (registre), `Ownership` (part par personne), visibilité, `Valuation`, `Flow`, `Proposal` (file de validation), provenance, `fx_rate` | `IAccessPolicy`, `IVisibleItems`, types `Money`/`Duration`/`DateRange`, `IValuations`, `IFlows`, `IProposalStore` ; points d'extension agrégés (tableau de bord, `ITaxContribution`, `IFactConventions`) | rien |
| **Subscriptions** | `Subscription`, `BillingPhase`, `Purchase`, `Grant`, `Label`, catalogue des fournisseurs (`Provider`) ; `CoveragePeriod` et `Charge` dérivés, calculés à la lecture (`Timeline`), sans cache stocké | Contributions au tableau de bord (coût mensuel réel/amorti, échéances), charges attendues | Core |
| **Investments** | `Envelope` (PEA, assurance-vie, PER, livret…) ; valorisations et flux via Core | Gain €/%, TRI plus tard, contribution au patrimoine | Core |
| **Income** | `IncomeSource`, `IncomePhase` ; revenus exceptionnels via les flux du Core | Revenus mensuels lissés, part engagée, marge (`IIncomeContribution`, `IIncomeSummary`) | Core, `RealEstate.Contracts` (loyers) |
| **RealEstate** | `Property`, `Loan`, `LoanPhase`, `LoanEvent`, `Insurance` | Tableau d'amortissement, capital restant dû, valeur nette, engagements ; loyers pour Income (`IRentIncome`) | Core |
| **Documents** | `Document` (fichier adressé par son sha256, métadonnées) et ses liens génériques vers des cibles (`target_kind`, `target_id`, rôle) ; stockage fichiers ou S3 | `IDocumentTargets` (cibles que chaque module résout), `IDocumentLinks` | Core |
| **Goals** | `Goal` (objectif d'épargne ou de précaution), ses liens aux enveloppes (`share_bp`) et ses relevés | Rien (lit `IEnvelopeFunding` d'Investments et `ICommitmentsContribution`) | Core, `Investments.Contracts` |
| **Banking** (étape 5, livré en 5A) | Connexions Enable Banking (session chiffrée), comptes bancaires et leurs sources, soldes, journal des synchronisations (schéma `banking`) | Rien en 5A ; ses soldes deviennent des valorisations par `IValuations` ou une proposition (`IProposalSubmitter`) | Core, `Investments.Contracts` |
| **Ingestion** | Pipeline de documents, table `job`, contrats de jobs, résultats d'extraction | Entrée des propositions vers Core, API interne pour les workers | Core |
| **Dashboard** (hébergé dans Core : `Core.Application/Dashboard`) | Rien (lecture seule, composition) | Agrégats patrimoine / engagements / évolution | Core + contributions des modules |
| **Tax** (hébergé dans Core : `Core.Application/Tax`, `Core.Domain/Tax`) | Profil fiscal du foyer par année (`core.tax_profile`) | Estimation d'impôt ; les modules fournissent leurs lignes (`ITaxContribution`) | Core + contributions des modules |
| **Platform** (technique) | Notifications, audit, sessions d'import (staging), planification | `INotificationChannel` ; les modules implémentent `IImportSection` (`import-format.md` §11.7) | Tous, via contrats |

Dashboard et Tax ne possèdent pas de module à eux : ils composent les contributions des modules et vivent dans Core. Dashboard n'a aucune donnée et y reste. Tax est un vrai domaine (calculateur, règles, profil stocké) sans lien avec le noyau : il sort dans un module `Nid.Tax`, avec son schéma, à sa prochaine évolution importante (spec `superpowers/specs/2026-10-19-tax-estimate-design.md`).

### 5.3 Décisions de conception

1. **Registre `Item` central (Core).** Chaque bien, abonnement, enveloppe ou crédit a une ligne `Item(id, module, kind, visibility)` et ses `Ownership`. Le module garde ses données et référence `item_id` (lien logique, pas de clé étrangère entre schémas). `IAccessPolicy` applique les règles de `Project.md` §5.1 (privé, lecture seule pour l'autre, totaux du foyer hors privé) pour l'API, le tableau de bord et le MCP : un seul endroit à auditer.
2. **Tableau de bord par contributions enfichables.** Chaque module implémente `INetWorthContribution`, `ICommitmentsContribution`, `IIncomeContribution`, `IUpcomingEvents`, découvertes par injection de dépendances. Un nouveau module apporte de nouveaux fournisseurs sans modifier Dashboard. La courbe d'évolution est **calculée à la demande** à partir des valorisations, flux et capitaux restants dus ; des instantanés matérialisés ne sont ajoutés que si un besoin réel apparaît. Les contributions du tableau de bord, les listes des biens et des crédits et la routine de l’immobilier lisent **par lots** : aucune lecture du registre, de la politique d’accès ou des faits élément par élément (`IItemRegistry.FindManyAsync`, `IAccessPolicy.CheckManyAsync`, `IValuations.SeriesManyAsync`, `IFlows.ListManyAsync`, chacune en un nombre fixe de requêtes quel que soit le nombre d’éléments) ; les autres listes (enveloppes, sources de revenu) lisent encore élément par élément ; si un élément devient privé pendant une requête, le bloc entier est refusé et tous ses éléments se lisent « sans valeur » pour cette requête (transitoire, comme un bloc refusé) ; un test compte les commandes SQL du tableau de bord, de sa courbe, des listes et de la routine de l’immobilier pour N et 2N éléments (`CommandCountTests`).
3. **File de validation dans Core, application dans le module.** Une `Proposal` porte un payload typé, sa provenance et son document source. À la validation, Core appelle l'`IProposalHandler` du module concerné, qui écrit dans son schéma. Ingestion ne touche jamais aux données métier.
4. **`Valuation` et `Flow` génériques (Core)** pour Investments et RealEstate. Subscriptions conserve ses propres `Purchase` / `BillingPhase` : sa logique de couverture n'est pas un `Flow`.
5. **Calculs = fonctions pures dans `Domain`** (timeline, amortissement, gain, TRI) : aucune entrée/sortie, aucune IA. Les caches dérivés sont reconstruisibles à tout moment.
6. **Communication** : requêtes Synapse aux points d'entrée, interfaces `Contracts` pour les lectures inter-modules. **Aucun événement entre modules aujourd'hui** : les valeurs dérivées (la couverture et les charges d'un abonnement, par exemple) se calculent à la lecture, sans cache à tenir à jour. Les événements Synapse in-process avec outbox (§5.4) arriveront avec le premier module qui doit réagir à l'écriture d'un autre. Pas de bus externe.

### 5.4 Couche application : CQRS avec Synapse

Bibliothèque : `UnambitiousFx.Synapse` et `UnambitiousFx.Synapse.Generator` (MIT ; net8, net9, net10). Médiateur in-process sans réflexion à l'exécution, `ValueTask`, retours en `Result<T>` (`UnambitiousFx.Functional`).

- **Un cas d'usage = une requête + un handler** `[RequestHandler<TRequest, TResponse>]`. Le générateur produit, par assembly, une classe `RegisterGroup` ; le host appelle `cfg.AddRegisterGroup(new …RegisterGroup())` pour chaque module. L'enregistrement est **explicite par module** : un module absent n'est pas chargé.
- **Commande ou requête** : Synapse n'expose que `IRequest` et `IRequest<T>` (vérifié, `spikes.md` §3). Le projet définit ses marqueurs `ICommand` / `IQuery`, imposés par un test d'architecture.
- **Erreurs attendues = `Result.Failure`**, jamais d'exception. **Vérifié** : le type `Failure` porte un `Code`, un message et des métadonnées, avec des variantes (`NotFoundFailure`, `UnauthorizedFailure`, `UnauthenticatedFailure`, `ValidationFailure`, `TimeoutFailure`) et des codes de la bibliothèque (`VALIDATION`, `NOT_FOUND`, `CONFLICT`, `UNAUTHORIZED`, `UNAUTHENTICATED`, `BAD_REQUEST`, `TIMEOUT`, `ERROR`). On les utilise tels quels et on ajoute des codes propres préfixés par module (`things.name_required`). Ils sont traduits à un seul endroit en Problem Details / statut HTTP et en erreur d'outil MCP ; `UnambitiousFx.Functional.AspNetCore` fournit `AddResultHttp` avec des mappeurs par type d'échec.
- **Pipelines**, dans cet ordre cible : trace / journalisation → acteur (personne, origine API / MCP / job, « agissant pour ») → portée (`write:direct` pour une commande directe, `write` pour soumettre une proposition, D10) → accès à l'élément (`IAccessPolicy`) → `Idempotency-Key` → validation → transaction → handler → masquage des identifiants de documents (`DocumentMask`, ordre 85, `documents-data-model.md` §11) → garde de sortie (ordre 90, `identity-and-access/authorization.md` §2). Droits privé/partagé, audit et provenance vivent dans ces comportements, pas dans les handlers. **Vérifié** (`spikes.md` §3) : l'ordre suit `IOrderedPipelineBehavior.Order` (valeur basse = extérieur), quel que soit l'ordre d'enregistrement, et un comportement peut court-circuiter avec un `Result.Failure`. **`[PipelineBehavior]` se propage vers le bas du graphe de références**, or les modules référencent `Platform` : les comportements communs sont donc déclarés **par le host** avec `[assembly: SynapseGlobalBehavior(typeof(…<,>))]`, et le `RegisterGroup` du host doit être enregistré (sans lui, rien ne s'exécute et rien n'échoue). Un test d'architecture vérifie qu'une requête de chaque module traverse toute la chaîne.
- **Contexte** : `IContextAccessor` et ses « features » transportent l'identité et l'origine de l'appel d'un comportement à l'autre.
- **Événements** (conception retenue, **pas encore utilisée** : §5.3.6) : `IEmitter` et `IEventHandler<T>`. Avec `EmitMode.Outbox` et `IOutboxCommit`, l'événement n'est diffusé qu'**après** la réussite de l'écriture. **Aucun paquet EF Core n'est publié** : nous écrivons dans Platform un `IEventOutboxStorage` (11 méthodes, dont relances et lettres mortes), **dans la même transaction que l'écriture du module**. Vérifié (`spikes.md` §6) : avec des `DbContext` qui partagent connexion et transaction, le retour arrière efface l'entrée et une autre session ne la voit qu'après le commit. Le stockage **en mémoire** fourni ne doit **jamais** servir en production : l'événement d'une commande échouée reste en attente et part avec le commit d'une autre requête. `IOutboxCommit.CommitAsync` diffuse tout ce qui est en attente.
- **Frontière avec les Contracts (D14)** :
  - Synapse : points d'entrée (API REST, outils MCP, jobs Quartz, application des propositions) et, quand il y en aura, événements entre modules.
  - Interfaces `Contracts` : lectures synchrones inter-modules et points d'extension à plusieurs fournisseurs (`IAccessPolicy`, `IVisibleItems`, `INetWorthContribution`…). Un fan-out n'est pas une requête unique, et `IAccessPolicy` est sur le chemin critique.
- **`Domain` ne référence pas Synapse** : fonctions pures, remplaçables sans toucher aux règles métier.
- **MCP** : les outils envoient les mêmes requêtes, donc traversent le même pipeline. Un jeton « Lecture + propositions » (`write`) ne peut pas exécuter une commande directe (`write:direct`) : il soumet une proposition explicite (`SubmitProposalCommand`), validée ensuite par une personne depuis le navigateur. Le comportement `IProposable` (conversion automatique d’une commande en proposition) est abandonné (D10). Les outils d’écriture (`propose_statement`, `propose_payslip`, `propose_invoice`, 4D1) sont un par usage : sans `apply` ils soumettent une proposition, avec `apply: true` (`write:direct`) ils passent par `ApplyDirectCommand` (`IDirectWrite`), jamais transformés en proposition. Un document déjà « conforme » à l’état actuel ne crée rien. Une unité de travail ouverte dans une autre la rejoint par un point de sauvegarde (`IUnitOfWork`) : seule la validation de la plus externe rend les écritures durables.

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
| Audit | Table `platform.audit_log`, append-only par déclencheur (trigger) base de données. Écrite par un `SaveChangesInterceptor` dans la transaction propre de l'écriture (même *change tracker*, même commit) : aucune écriture sans acteur (1F3a). Les instructions en masse (`ExecuteSql`, `ExecuteUpdate`, `ExecuteDelete`) contournent l'interceptor, donc ne sont permises que là où c'est décidé (U17, `superpowers/specs/2026-09-26-audit-log-design.md`). Les événements de sécurité s'écrivent sur leur propre connexion, hors de la transaction de la requête. Enregistre aussi « agissant pour » quand l'acteur est le MCP (étape 4). |
| Suppression | Suppression logique (`deleted_at`) sur les agrégats utilisateur ; purge réelle par action explicite. |
| Concurrence | Verrou optimiste via la colonne système `xmin`. Conflit → HTTP 409. |
| Documents | Module Documents (`documents-data-model.md`). Stockés par contenu : `{sha[0:2]}/{sha256}` dans le volume `documents` (`/var/lib/nid/documents`) ou un bucket S3, dédupliqués. Métadonnées en base. Chiffrement au repos = disque de l'hôte ou chiffrement du bucket ; sauvegarde chiffrée du volume (pas du bucket S3). |
| Secrets | Jetons et consentements bancaires chiffrés avec ASP.NET Data Protection ; clés sur volume sauvegardé. |

## 7. API

- REST JSON `/api/v1/{module}/…`, OpenAPI généré, **client TypeScript généré** pour le SPA. Erreurs en Problem Details (RFC 9457).
- Sur le fil : montants en **chaînes** (`{"amount":"12.34","currency":"EUR"}`), dates ISO 8601. Intervalles en champs explicites `startsOn` / `endsBefore` (le `ends_on` du modèle d'abonnements est ambigu et n'a été précisé « semi-ouvert » qu'après coup). **Retenu par défaut.**
- **Authentification** : le SPA passe par un **BFF** (code + PKCE côté serveur, cookie `HttpOnly`, protection CSRF). L'API accepte aussi des jetons bearer (MCP, automatisations) avec les portées `read`, `write` et `write:direct`. Détail dans `identity-and-access/`.
- Écritures idempotentes via l'en-tête `Idempotency-Key` (workers, MCP).
- Couche application en CQRS avec **Synapse** (§5.4). Chaque endpoint se limite à traduire HTTP → requête Synapse → `Result` → réponse (Problem Details en cas d'échec). L'idempotence (`Idempotency-Key`) est un comportement de pipeline.
- **API interne des workers** : groupe de routes `/internal/v1/jobs/…` sur un port / listener séparé, non routé par le proxy, avec identifiants de service par worker.

## 8. Serveur MCP

- Endpoint `/mcp`, transport Streamable HTTP, dans le même processus, avec le SDK C# officiel (`ModelContextProtocol.AspNetCore`). Mode sans session (par défaut, recommandé tant qu'il n'y a pas de requêtes serveur → client). Autorisation par une politique d’autorisation de l’hôte liée au schéma `nid-pat` (`/mcp` n’accepte qu’un jeton), appliquée à `MapMcp()` via `RequireAuthorization()`.
- Les outils sont de **minces enveloppes** qui envoient les mêmes requêtes Synapse que REST : même pipeline, même `IAccessPolicy`, mêmes droits ; un élément privé reste privé. Outils enregistrés **explicitement** (`WithTools<T>()`), pas par scan (`WithToolsFromAssembly` est annoté `RequiresUnreferencedCode`).
- **Lectures** (`net_worth`, `list_subscriptions`, `loan_schedule`, `envelope_performance`…) : chiffres calculés par les services déterministes, avec provenance. Le modèle ne calcule rien.
- **Écritures** : par défaut, elles créent une `Proposal` dans la file de validation (commande de soumission, portée `write`). Un jeton peut recevoir le scope `write:direct` quand l'utilisateur dicte lui-même la saisie : seul ce scope exécute une commande directe. **Retenu par défaut.**
- Authentification : **jeton d'accès personnel** révocable, avec portées `read`, `write`, `write:direct` (`identity-and-access/tokens-and-credentials.md`). OAuth via le fournisseur d'identité écarté en v1.
- **Livré en 4B** : SDK `ModelContextProtocol.AspNetCore` 2.2.0 (révision 2026-07-28, compatible `initialize`), sans session, `/mcp` réservé aux jetons, 18 outils de lecture qui rendent le JSON des routes REST, un filtre d’appel unique (acteur `via: mcp`, événement `mcp_tool_called`), résultat borné à 100 ko.
- Le MCP **client** (messagerie, stockage de documents) vit dans les workers d'ingestion, pas dans le cœur.

## 9. Ingestion, jobs et file de validation

### 9.1 Flux de bout en bout

```
Source (dépôt UI / mail / banque)
  → Document (module Documents, SHA-256)
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
| Synchronisation bancaire (Enable Banking, étape 5) | **Quartz.NET in-process** (`bank-sync-nightly`), par `SafeHttpClient` | Un appel REST signé (JWT RS256), comme la collecte des taux : aucun code tiers ni page web à isoler (spec de la synchronisation bancaire, D13). Le bouton « Synchroniser » s'exécute dans la requête, pour transmettre à la banque l'adresse et l'agent du navigateur |

Le planificateur n'a pas accès à `docker.sock` : lancer des conteneurs depuis l'application donnerait de fait un accès root à la machine, et déléguer à un cron d'hôte lierait l'application à l'orchestrateur de l'hébergeur.

Le magasin persistant de Quartz est en Postgres, avec un préfixe de tables qualifié par schéma (`platform.QRTZ_`).

### 9.3 Jobs

- Table `job` (schéma Ingestion) : `type`, `schema_version`, `payload`, `status` (`queued` / `running` / `succeeded` / `failed` / `dead`), `attempts`, `run_after`, `lease_until`, `result`, `error`, `created_by`.
- Les workers **n'accèdent pas à la base**. Ils appellent l'API interne : `claim` (long-poll, `FOR UPDATE SKIP LOCKED` côté API), `heartbeat`, `complete`, `fail`. Bail expiré → remise en file. Retry avec délai croissant ; état `dead` après N essais, avec alerte email.
- **Idempotence** : `(type, hash de l'entrée)` évite de retraiter le même document.
- **Contrats** dans le dépôt : `contracts/jobs/{type}/v{n}/{input,output}.schema.json`. L'API valide la sortie du worker avant toute suite ; version inconnue = rejet. Ajouter une techno = respecter le contrat.
- Types initiaux : `extract-statement` (relevés de placements), `extract-amortization`, `extract-invoice`. Plus tard : `fetch-mail`, `scrape-woob`. La banque n'est pas un job : elle tourne dans `nid` (§9.2, livré en 5A).
- Les workers récupèrent le document par l'API (flux à jeton court), sans volume partagé : ils restent sans état.
- Traçage OpenTelemetry API ↔ worker (`traceparent` dans le job). Page « jobs » dans l'interface.

### 9.4 Extraction (worker Python + Ollama par défaut)

1. PDF → texte (PyMuPDF, pdfplumber) ; si scanné, OCR ou modèle de vision.
2. LLM avec **sortie JSON contrainte** par le schéma.
3. Contrôles déterministes : totaux, dates cohérentes, devise connue.
4. Sortie : valeurs **et** avertissements de confiance. Jamais de calcul par le modèle.

Pour `extract-amortization`, RealEstate **recalcule** le tableau depuis les conditions du prêt et le compare à celui de la banque (`Project.md` §8.5) ; l'écart figure dans la proposition. Une extraction douteuse devient visible.

Avant d'engager un modèle : constituer un **jeu d'évaluation** de vrais relevés avec le JSON attendu pour mesurer la précision par champ. Le choix du modèle relève de la configuration, pas de l'architecture. L'extracteur est un port (`IDocumentExtractor`). Le modèle est appelé **par la passerelle LLM** de `nid` (D24) : Ollama en local, ou un fournisseur cloud **sur opt-in à chaque dépôt de document**, tracé dans la provenance et dans `llm_call`. Politique, consentement, budget, masquage et clés : `ingestion/llm-providers.md`.

### 9.5 Proposal (Core)

- Champs : `kind`, `target_item_id?`, `payload` + version, `status` (`pending` / `approved` / `rejected` / `superseded`), `source`, `document_id`, `job_id`, `warnings`, `decided_by/at`, payload **original et édité** conservés.
- **Visibilité héritée** de l'élément cible ; pour un nouvel élément, le propriétaire est celui qui a déposé le document. Une proposition sur un élément privé n'apparaît que chez son propriétaire.
- **Dédoublonnage** par empreinte `(kind, cible, hash du payload)`. Une nouvelle proposition remplace (`superseded`) une ancienne en attente sur la même cible et la même période.
- Livré en 4C1 : ni document ni job, et le contrat de handler n’est pas générique (`core-data-model.md` §7.6).
- **Contrat de handler** `IProposalHandler` : `Validate` (problèmes), `Preview` (diff contre l'état actuel), `Apply` (écriture idempotente dans **une transaction** avec le changement de statut et l'audit).
- **Jamais d'auto-approbation.** Les propositions en attente sont rappelées dans le résumé mensuel, sans expiration silencieuse. Seule exception à la file, qui n'approuve rien : un compte bancaire dont « Appliquer les soldes automatiquement » est coché écrit sa valorisation directement (`source = bank_api`, `created_via = job`), sans proposition (`Project.md` §9.4, étape 5).
- Interface : boîte de réception avec le PDF à côté, valeurs extraites, diff et avertissements.
- **Livré en 4C1** (serveur, sans écran) :
  - table `core.proposal` ; soumission par `SubmitProposalCommand` (portée `write`), empreinte = SHA-256 de la forme RFC 8785 de `{kind, target, payload}` ;
  - dédoublonnage : même empreinte en attente renvoyée (`200`), même `(kind, cible, subject_key)` avec un autre payload : l’ancienne passe à `superseded` (`201`) ;
  - routes `/api/v1/core/proposals` (soumettre, lister, détail avec aperçu et `canDecide`, approuver, rejeter) ; décider est réservé au navigateur et exige l’écriture sur la cible ;
  - l’approbation exécute `Validate`, `Apply` et la décision dans **une seule transaction** `IUnitOfWork`, avec contrôle `xmin` ; en cas d’échec, retour arrière puis avertissement `apply_failed` ajouté à la ligne encore en attente (aussi lorsqu’un handler lève une exception, qui est alors relancée), et l’erreur est renvoyée ;
  - deux approbations simultanées n’appliquent qu’une fois : la perdante reçoit un 409 (`core.proposal_decided`, `core.proposal_conflict` ou `core.fact_conflict` selon le moment) ;
  - premier type : `investments.record_valuation` (aperçu `current`, `previous`, `proposed` ; une valeur à la même date est remplacée).

### 9.6 Déclencheurs

| Déclencheur | Effet |
|---|---|
| Dépôt manuel d'un PDF | Job d'extraction selon le type |
| Routine mensuelle (Quartz) | Rappels par email ; formulaire **pré-rempli** depuis le mois précédent pour chaque enveloppe |
| Mail (étape 4 et après) | `fetch-mail` → factures → `extract-invoice` |
| Banque (étape 5) | Tâche Quartz `bank-sync-nightly` (04:30) et bouton « Synchroniser », dans `nid` → soldes des comptes associés → proposition `investments.record_valuation` par fin de mois écoulé (et aujourd'hui sur un clic), ou valorisation écrite si le compte l'a demandé (**livré en 5A**). Ensuite (5B à 5D) : opérations, rapprochement avec les charges attendues → propositions « hausse de prix », « prélèvement récurrent inconnu », versements. Lecture seule ; session chiffrée ; alerte avant expiration. Spec : `superpowers/specs/2026-10-29-bank-sync-design.md` |

### 9.7 Sécurité de l'ingestion

- Identifiants de service **par worker**, limités à ses types de jobs. Réseau compose segmenté : un worker d'extraction n'atteint que l'API interne (et, par elle, la passerelle LLM) ; **sans Internet, sans clés d'API**.
- Les workers de **connecteurs** (mail, woob) exigent une sortie limitée à leurs hôtes et une **revue de risque préalable** (`security/risk-register.md` §7). La banque n'est pas un worker : elle tourne dans `nid` (§9.2), vers le seul `api.enablebanking.com`, et sa revue est `security/bank-sync-review.md`.
- Aucun document ne quitte l'infrastructure par défaut ; tout envoi cloud exige le consentement du déposant (`ingestion/llm-providers.md` §6).
- Menaces et mesures détaillées : `security/ai-ingestion.md`.

## 10. Déploiement

### 10.1 Docker Compose de référence

| Service | Rôle | Obligatoire |
|---|---|---|
| `nid` | SPA statique + API + BFF + MCP + Quartz, une seule image et un seul port ; l'hôte sert lui-même le SPA (fichiers hachés en cache immuable, repli sur `index.html` pour les routes du client, en-têtes de sécurité) : origine unique | oui |
| `postgres` | Base | oui |
| `pocket-id` | OIDC | oui |
| `nid-worker-*` | Workers d'ingestion | à la carte |
| `ollama` | LLM local | optionnel (avec les workers d'extraction) |
| `backup` | restic chiffré ; dépôt local `deploy/backups/`, dépôt distant optionnel (1F2) | oui |

- Configuration par variables d'environnement et secrets Docker (fichiers). Aucun secret dans l'image ni dans le dépôt.
- Images multi-architecture (amd64/arm64), utilisateur non-root, système de fichiers en lecture seule sauf volumes, healthchecks.
- **Migrations** EF Core par module, appliquées au démarrage de `nid` sous verrou advisory Postgres. Migrations rétro-compatibles (expand/contract) pour permettre un retour arrière d'image.
- Volumes : `pgdata`, `pocket-id` (sa base SQLite et ses images), `dataprotection-keys` (clés Data Protection de `nid`), `assets` (icônes des fournisseurs, 1C2c), puis `documents` à l'étape 4. Tous couverts par la sauvegarde (1F2), avec `deploy/secrets/` (dont la clé de chiffrement de Pocket ID) et `deploy/.env`. S'y ajoute `logs` (journaux de `nid`), hors sauvegarde.
- Livré en 1F1 : `deploy/compose.yaml`, `deploy/setup.sh` (secrets et `.env`, puis le secret du client OIDC une fois créé dans Pocket ID), `deploy/README.md` pour l'hébergeur ; images construites depuis le dépôt (`src/Nid.Host/Dockerfile`, `web/Dockerfile`), sans publication sur un registre.
- Mises à jour : tags d'image épinglés, procédure documentée (sauvegarde → pull → up). Pas de mise à jour automatique.

### 10.2 Hypothèses envers l'hébergeur

Le reverse proxy, le TLS et l'accès externe sont fournis par l'hébergeur, hors du compose applicatif. L'application suppose seulement :

- HTTPS terminé en amont ;
- en-têtes `X-Forwarded-*` de confiance : par défaut des plages privées et locales, où se trouve le proxy de l'hébergeur ; une liste de réseaux (`Auth__TrustedNetworks`) la remplace ;
- une URL publique configurable (`PUBLIC_URL`) utilisée pour les redirections OIDC, les liens des emails et le manifeste PWA ;
- **une seule origine** : le proxy de l'hébergeur envoie tout `nid.<domaine>` à `nid`, qui sert l'interface et l'API sur le même port ; `auth.<domaine>` va à `pocket-id`. L'hébergeur a donc **deux cibles** et aucun routage par préfixe. Même nom d'hôte pour l'interface et l'API (cookie de session du BFF, pas de CORS) ;
- un accès sortant HTTPS vers le dépôt `SubTracker-Data` (taux de change et catalogue de fournisseurs) et vers l'historique de la Banque centrale européenne (`www.ecb.europa.eu`, taux de change depuis 1999), URL configurable, miroir ou dossier local possibles. Sans lui, les taux se saisissent à la main et les fournisseurs sont créés à la main.

Un exemple de reverse proxy peut figurer dans `deploy/`, à titre optionnel.

### 10.3 Environnement de développement local (Aspire)

Le projet `Nid.AppHost` (Aspire) décrit en C# toute la pile de développement et la lance d'une seule commande, `aspire run` (ou `dotnet run --project src/Nid.AppHost`). Il **remplace** un compose de développement écrit à la main : dépendances, ordre de démarrage, ports et chaînes de connexion sont déclarés une fois, dans le code.

| Ressource | Nature | Rôle |
|---|---|---|
| `postgres` | conteneur (`AddPostgres`, volume de données, base `nid`) | Base ; chaîne de connexion injectée dans `nid-api` |
| `pocket-id` | conteneur (`v2.18.0`, volume `nid-dev-pocket-id`, clé d'API jetable) | OIDC de développement |
| `pocket-id-bootstrap` | processus jetable (`deploy/local/bootstrap-pocket-id.mjs`) | Crée les groupes, trois utilisateurs jetables (`alice` admin, `bob`, `carol` sans accès) et le client de Nid ; écrit son secret dans `.data/secrets`, que `nid-api` lit comme secret « un fichier par clé » |
| `nid-api` | projet .NET (`Nid.Host`), port fixe 5080 non proxifié | API + BFF ; attend `postgres` ; migrations au démarrage |
| `nid-web` | application Vite (`/web`, `AddViteApp`) | Rechargement à chaud du frontend |
| `caddy` | conteneur | Proxy TLS mono-origine : `/api`, `/auth`, `/mcp`, `/health` vers `nid-api`, le reste vers `nid-web` ; reproduit §10.2 |

- **Pourquoi un proxy en développement** : Pocket ID veut le HTTPS et un nom d'hôte propre pour ses passkeys (`APP_URL`) ; la pile répond donc sur `https://nid.localtest.me` et `https://auth.localtest.me` (`identity-and-access/authentication.md`). Le proxy garantit aussi la mono-origine du BFF (pas de CORS).
- **Tableau de bord Aspire** : journaux, traces et métriques OpenTelemetry de tous les services au même endroit ; il tient lieu de backend OTLP local (§12). Les projets .NET référencent `Nid.ServiceDefaults` (OpenTelemetry, health checks, résilience HTTP) ; les `/health/*` de §12 en font partie.
- **Secrets** : les secrets de développement sont des paramètres Aspire (`AddParameter`, `secret: true`, user-secrets) ou les fichiers jetables générés par `deploy/dev/setup.sh`. Rien ne part en production.
- **Tests d'intégration** : ils gardent Testcontainers (§13), indépendants de l'AppHost. `Aspire.Hosting.Testing` reste possible plus tard pour les E2E Playwright, qui démarreraient alors la même pile.
- **Prérequis** : SDK .NET (`global.json`), CLI Aspire, Docker (ou Podman), Node pour Vite.
- **Limite** : l'AppHost est un outil de développement. Le compose de référence (§10.1) reste la seule forme livrée ; `aspire publish` n'est pas utilisé.

## 11. Sauvegarde et export

- **restic**, chiffré, règle 3-2-1 : cible locale + cible distante au choix de l'hébergeur (S3, SFTP, autre disque).
- Contenu : dump Postgres (`pg_dump` cohérent, pas de copie à chaud du répertoire de données) + volume `documents` + clés Data Protection + base de Pocket ID (copie à chaud) + `deploy/secrets/`. **Sans les clés Data Protection, les jetons bancaires chiffrés sont perdus.**
- **Test de restauration** documenté et scripté (restauration dans un compose jetable).
- Alerte si la sauvegarde échoue ou dépasse N jours : **livrée** (notifications, PR 4) par `backup_failed`, aux admins. `nid-backup` écrit `status.json` dans le volume `backup-state`, que `nid` lit en lecture seule (`Nid:Backup:StatusPath`) ; le service `backup` devient aussi `unhealthy` (1F2).
- Livré en 1F2 (`superpowers/specs/2026-09-26-backup-design.md`) : service `backup` (chaque nuit à 03:30, rétention 7 quotidiennes, 4 hebdomadaires, 12 mensuelles, `restic check` le dimanche), un instantané par sauvegarde avec un manifeste (lignes par table, migrations, clés), dépôt local `deploy/backups/`, dépôt distant par la surcouche `compose.backup-remote.yaml`, y compris en ajout seulement ; `deploy/restore.sh`, réel (refuse une installation qui a des données) ou `--verify` dans un projet jetable. Le mot de passe restic reste hors de la sauvegarde.
- **Export complet** JSON/CSV à la demande, format documenté et versionné, pour ne jamais dépendre de l'application. **Assemblé par le navigateur** à partir de pages fournies par le serveur (manifeste, pages de 500 entités, `import-format.md` §10), sans fichier construit côté serveur. Respecte la visibilité : chacun exporte ce qu'il voit.
- **Import** : le même format que l'export JSON, spécifié dans `import-format.md`. Session en lots avec staging, validation complète par le serveur, écriture en une transaction, idempotence ; **orchestrée par le navigateur, sans tâche de fond**. Sert à la reprise initiale des données existantes et au transfert entre instances.

## 12. Observabilité

- Logs structurés JSON, **sans données financières** (montants et libellés masqués hors niveau debug).
- OpenTelemetry : traces API ↔ workers ; métriques (durée des jobs, propositions en attente, âge de la dernière sauvegarde, âge des consentements bancaires). Export OTLP optionnel vers ce que l'hébergeur fournit ; sans backend, logs des conteneurs et page d'état dans l'interface.
- `/health/live` (le processus) et `/health/ready` (le processus et la base), dans tous les environnements, réponse `Healthy` ou `Unhealthy` sans détail (1F1). La joignabilité de Pocket ID est visible dans Administration › Système, pas dans `/health/ready` : une panne du fournisseur d'identité ne doit pas faire redémarrer l'API.

## 13. Stratégie de tests

| Niveau | Cible |
|---|---|
| **Unitaires purs** | `Domain` : timeline d'abonnement, coût amorti, amortissement de crédit, gain, TRI. Sans E/S. |
| **Propriétés** (FsCheck) | Invariants : phases sans chevauchement, couverture contiguë, somme des coûts amortis = base de coût, capital restant dû final = 0, capital + intérêts = somme des mensualités. |
| **Cas dorés** | Tableaux d'amortissement réels de la banque, exemples de `subscription-data-model.md`, reprise du JSON existant avec ses anomalies connues. |
| **Intégration** | Testcontainers Postgres : handlers, migrations, `IAccessPolicy` (matrice privé / partagé / multi-propriétaires × lecture / écriture). Un projet `tests/Nid.<Module>.IntegrationTests` par module, contre l'hôte réel (`NidApp` et les helpers d'API partagés, dans `Nid.TestSupport`) ; `Nid.Host.IntegrationTests` garde ce qui traverse les modules : import et export, MCP, administration, audit, banc anti-fuite. Chaque projet démarre son propre conteneur Postgres : la suite complète se lance un projet à la fois (`dotnet test --solution Nid.slnx --max-parallel-test-modules 1`), en parallèle les conteneurs saturent Docker et les requêtes expirent. |
| **Banc anti-fuite** | Jeu de données mixte, toutes les requêtes exécutées en tant que chaque adulte, tests de propriétés sur `Readers` / `Writers`, test de la garde de sortie. Lancé avec les tests d'intégration à chaque pull request et chaque lundi sur `main` (`ci.yml`, `identity-and-access/access-tests.md`). |
| **Sécurité** | SSRF du `SafeHttpClient`, injection de formules CSV, uploads, import malveillant, corpus de PDF piégés, égress des workers, politique de la passerelle LLM (offres inéligibles, consentement, budget), clés jamais exposées (`security/ai-ingestion.md` §7, `security/application.md` §9). |
| **Frontend** | Lint (dont l'interdiction d'arithmétique sur les montants), tests de composants, E2E avec un Pocket ID de test (sessions ouvertes par son API, `frontend/testing.md`), accessibilité, contraste de tous les thèmes, validation de la palette de graphiques, budgets de taille (`frontend/testing.md`). |
| **Contrats** | Schémas de jobs validés avec des exemples ; chaque type de job possède un schéma d'entrée et de sortie. |
| **Architecture** | `ModuleBoundaryTests` lit les références des `csproj` (pas de NetArchTest) : aucune dépendance entre modules hors le `Contracts` d'un autre lu par une `Application`, sans cycle, `Domain` sans paquet (ni Synapse ni EF Core), règles du §5.1. `HandlerAttributeTests` (`tests/Nid.Host.IntegrationTests/LeakBench/`) vérifie par réflexion que toute implémentation de handler Synapse (`IRequestHandler`, `IStreamRequestHandler`, `IEventHandler`) des assemblies `Nid.*` porte l'attribut correspondant, `[RequestHandler<TRequest, TResponse>]` par exemple (le générateur est la source de vérité : sans l'attribut, le handler n'est jamais enregistré, sans erreur). Les deux sont lancés en CI sur chaque pull request. |
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
/deploy/dev              (configuration Caddy de développement ; lancé par Nid.AppHost avec Pocket ID)
/docs                    (Project.md, subscription-data-model.md, architecture.md, adr/)
/tests/…
```

- CI (`ci.yml`, runners GitHub standards, gratuits pour un dépôt public) : à chaque pull request, selon les fichiers touchés, build, contrôle du client OpenAPI, tests unitaires, tests d'intégration (un job par projet), typecheck, lint, vitest, budget web, tous les parcours Playwright sur le compose de référence, images analysées par Trivy et revue des dépendances ; `ci-ok` est le contrôle requis. CodeQL (`codeql.yml`) sur chaque pull request. Chaque lundi, le tout sur `main`. Dependabot propose les mises à jour chaque semaine.
- **ADR** dans `docs/adr/` : une décision structurante par fichier (stack, monolithe modulaire, OIDC, workers par table de jobs, registre `Item` central…), pour rendre les décisions de ce document traçables.

## 15. PWA et notifications

- **PWA** : manifeste et service worker avec cache des assets et du dernier tableau de bord consulté (lecture hors ligne). Saisie mobile en ligne uniquement.
- **Notifications** : `INotificationChannel`, email SMTP en premier. Types : critique (consentement bancaire, validation en attente), échéance (renouvellement, hausse de prix), résumé mensuel. Préférences par utilisateur.
- **Centre de notifications (livré, PR 1 de `superpowers/specs/2026-10-24-notifications-design.md`)** : le **centre seul**, rien n'est encore envoyé hors de l'application (email : PR 2, Discord : PR 3, sécurité, actions et santé : PR 4). Tables `platform.notification` et `platform.notification_run` (non auditées) ; contrat `INotificationSource` et `NotificationCatalog` (scoped) ; les sources Abonnements (`subscription_renewal`, `subscription_expiry`, `subscriptions.ends_without_renewal`) et Immobilier (`tenancy_end`, `property_cost_due`, `loan_phase_end`, `loan_end`, `rent_arrears`, `revision_due`, `revision_lost`, `irl_missing`, `valuation_stale`) réutilisent les calculs de leur module, sans règle d'échéance dans `Nid.Platform`. Le job Quartz `notifications-evaluate`, toutes les heures, évalue chaque adulte une fois par jour du foyer après 06:00, en lecture seule (`Actor.Notifier`), avec isolation par (personne, source) ; les lignes résolues ou écartées sont purgées après 90 jours. `GET /api/v1/platform/notifications/count` demande aussi un rafraîchissement, au plus toutes les `Notifications:RefreshThrottleSeconds` secondes (900 par défaut), seulement quand les jobs sont activés (`Jobs:Enabled`) ; la pile de développement (Aspire) et celle du smoke (`deploy/compose.local.yaml`) la fixent à 5 pour que les parcours voient leurs données évaluées, la production garde la valeur par défaut. Le titre, le texte et le lien sont rendus par le serveur, en français, **sans aucun montant**. Écrans : `frontend/screens.md` §2.
- **Envoi par email et préférences (livré, PR 2)** : **préférences** par personne (`GET` et `PUT /api/v1/platform/notifications/preferences`, versionnées et auditées) : email (désactivé par défaut, refusé sans SMTP configuré ou sans adresse), niveau action (immédiat ou résumé), niveau information (résumé ou centre seul), résumé quotidien ou hebdomadaire (avec son jour), et délai d'avance par type daté (0 à 365 jours), qui re-date les lignes en attente. **Boîte d'envoi** `platform.notification_delivery` (une ligne par notification, canal et tour, ou par résumé et canal), remplie dans la transaction qui crée ou change la notification. Le job `notifications-dispatch`, chaque minute, revérifie l'état et la visibilité, envoie, retente à 1 min, 5 min, 30 min, 2 h puis 12 h, puis échoue ; un plantage compte comme une tentative. Le job `notifications-digest`, chaque heure, produit un résumé par personne et par période (clé du jour, clé de semaine ISO) au premier passage après 08:00 heure du foyer, groupé par niveau ; une ligne réveillée ou rouverte peut figurer dans un résumé suivant. **Canal email** (MailKit) : sujet `Nid : <titre>` sans le nom de l'élément, corps avec le nom, la date et un lien absolu construit sur `Auth:PublicUrl`, **sans montant**. Variables (le canal n'existe que si `Host` et `From` sont renseignés) :

  | Variable | Rôle |
  |---|---|
  | `Nid__Smtp__Host` | serveur SMTP |
  | `Nid__Smtp__Port` | port (587 par défaut) |
  | `Nid__Smtp__User`, `Nid__Smtp__Password` | identifiants, si le serveur en demande |
  | `Nid__Smtp__From` | expéditeur (`nid@exemple.fr` ou `Nid <nid@exemple.fr>`) |
  | `Nid__Smtp__Security` | `StartTls` (défaut), `SslOnConnect` ou `None` (relais d'un réseau de confiance) |

  Nouvelles sources : Placements (échéance d'un support, valeur périmée, plancher des unités de compte), Revenus (fin de phase, écart de revenu) et socle (taux de change manquant, routine mensuelle). Email de test : `POST /notifications/test` (pour soi) et `POST /admin/notifications/test` ; vue d'administration de l'état du SMTP et des envois échoués (30 jours). La pile de développement (Aspire) et celle du smoke (`deploy/compose.local.yaml`) lancent **Mailpit** (`axllent/mailpit`, SMTP 1025, interface et API 8025), jamais sur un vrai déploiement ; le parcours `web/e2e/notifications-email.spec.ts` lit son API (`MAILPIT_URL`, `http://localhost:8025` par défaut). Limites assumées : une seule instance de l'API (la prise d'un envoi est une relecture, sans verrou) ; le premier résumé après l'activation de l'email peut lister des lignes déjà ouvertes ; un résumé hebdomadaire dont le jour passe sans exécution est sauté cette semaine-là. Écrans : `frontend/screens.md` §9 et §12.

- **Canaux Discord (livré, PR 3)** : webhook personnel (`discord_person`, au rythme de la personne comme l'email) et webhook du foyer (`discord_household`), dans `platform.notification_webhook` (une ligne par personne, `person_id` nul pour le foyer ; `url` chiffrée par Data Protection, objectif `Nid.Notifications.Webhook`, jamais renvoyée, l'API n'en montre que les 4 derniers caractères ; auditée, sans version). Seules les URL `https://discord.com|discordapp.com/api/webhooks/…` sont acceptées (`422 notifications.invalid_webhook`). Un 401, 403 ou 404 de Discord passe le webhook en `broken` (remplacé en le collant à nouveau, comme un webhook illisible après perte de la clé) ; 429, 5xx et réseau sont réessayés. Le foyer ne reçoit que les lignes critiques ou d'action avec au moins un élément que tous les adultes actifs voient (vérifié à la mise en file et à l'envoi), **une fois par clé, pour toujours** (index unique partiel sur `household_key`, que la purge de 90 jours conserve), sans nom d'élément ni montant ; `allowed_mentions.parse` vide, aucun ping. Hôtes sortants : `discord.com` et `discordapp.com` sont dans les valeurs par défaut de `Outbound:AllowedHosts` ; une liste configurée **remplace** celle par défaut et doit les contenir. `SafeHttpClient.PostJsonAsync` refuse les redirections. Routes : `PUT` et `DELETE /api/v1/platform/notifications/discord` et `/api/v1/admin/notifications/discord`, tests `POST /test` (`email` | `discord_person`) et `POST /admin/notifications/test` (`email` | `discord_household`), erreur `notifications.discord_unavailable`. Écrans : `frontend/screens.md` §9 et §12.
- **Sécurité, actions et santé (livré, PR 4)** : `INotifier` (`PublishAsync`, `ResolveAsync`) publie sur sa propre connexion, sans jamais lever ni dépendre de l'annulation de l'appelant ; `access_violation` (critique, tous les adultes actifs, sans élément), `proposal_pending` (ceux qui peuvent décider, résolue par la décision) et `job_failed` (une clé par job, résolue au prochain passage sain) sont publiés ; `token_expiring` (titulaire seul) et `backup_failed` / `sync_failed` (admins, `SystemHealthNotificationSource`, adaptateurs `ISyncHealth`) viennent de sources. Le Discord du foyer prend aussi les lignes critiques et d'action sans élément de la sécurité et de la santé (pas `token_expiring`) ; une notification de santé publiée y repart quand l'incident rouvre. Pas de notification d'import. Détail : `superpowers/specs/2026-10-24-notifications-design.md` §3.2, §5.3 et §11.

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
- OAuth via le fournisseur d'identité pour le MCP : à revoir si un client utilisé refuse le jeton statique (`identity-and-access/tokens-and-credentials.md` §5).
- Flux OIDC avec Pocket ID **exécuté de bout en bout** (`spikes.md` §9septies) : le handler ASP.NET fonctionne avec ses réglages par défaut ; `groups`, `name` et `email` sont dans l'ID token. Non vérifiés : la déconnexion fédérée (`end_session_endpoint`) et la déconnexion par canal arrière.

## 17. Vérifications faites lors de la rédaction

Consultées dans la documentation à jour :

- **SDK MCP C#** : `AddMcpServer().WithHttpTransport(...)`, mode sans session par défaut, `MapMcp()` et `RequireAuthorization()`, `AddAuthorizationFilters()`.
- **Quartz.NET** : `AddQuartz` / `AddQuartzHostedService`, magasin persistant avec clustering, préfixe de tables qualifié par schéma pour Postgres.
- **Npgsql EF Core** : plugin `UseNodaTime()`, concurrence optimiste via une propriété `uint` mappée sur `xmin`.
- **Synapse** (documentation et NuGet) : `[RequestHandler<,>]` et `RegisterGroup` générés par assembly, `[PipelineBehavior]`, validation, événements avec outbox (stockage transactionnel à écrire, pas de paquet EF Core : `spikes.md` §3), contexte par « features », MIT, net8 à net10.
- **Native AOT** (Microsoft Learn, Npgsql, Quartz.NET, SDK MCP) : voir §4.3.
- **EF Core** : modèle compilé et requêtes précompilées utilisables sans NativeAOT.
- **Spikes exécutés** : Synapse, NodaTime et JSON, RFC 8785, échéancier de prêt, EF Core et PostgreSQL (`spikes.md`).
