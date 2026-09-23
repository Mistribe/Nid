# Feuille de route et état d'avancement

> Dernière mise à jour : 2026-09-23. **Ce fichier est la référence de l'état du projet.** Le contenu et la valeur de chaque étape restent décrits dans `Project.md` §10 ; les plans d'exécution sont dans `superpowers/plans/`.
> Règle : le dernier commit de chaque plan ou sous-plan met à jour ce fichier (statut, date, décisions prises, écarts). Un statut périmé est un défaut.

Statuts : `à faire` · `en cours` · `terminé` (avec la date) · `bloqué` (avec la raison).

---

## 1. Vue d'ensemble

| Étape | Contenu (voir `Project.md` §10) | Conception | Plan d'exécution | Statut |
|---|---|---|---|---|
| **0. Conception** | Architecture, modèles de données, sécurité, frontend, format d'import, spikes | Terminée | sans objet | **terminé** (2026-09-19) |
| **1. Noyau + Abonnements** | Propriétaires, montants ; module Abonnements complet ; reprise du fichier existant | Terminée | `superpowers/plans/2026-09-19-stage-1-overview.md` | **en cours** (1A, 1B, 1C1 et 1D1 terminés) |
| **2. Placements et Revenus** | Enveloppes, valorisations, versements, sources de revenu, premier tableau de bord | Terminée | à écrire | à faire |
| **3. Immobilier et crédits** | Biens, crédits, phases, assurance, échéancier, valeur nette | Terminée | à écrire | à faire |
| **4. Automatisation IA** | Serveur MCP, extraction de documents, file de validation | Terminée | à écrire | à faire |
| **5. Synchronisation bancaire** | Comptes via DSP2, rapprochement avec les abonnements | **À écrire** | à écrire | à faire |
| **6. Approfondissements** | TRI, détail par ligne, simulations avancées, autres modules, fiscalité | Idées seulement | à écrire | à faire |

Chaque étape doit être utilisable seule. Une étape ne démarre que lorsque la précédente est terminée et que son plan détaillé est écrit.

## 2. Étape 0 : conception (terminée)

| Jalon | Document | Date |
|---|---|---|
| Cadrage produit | `Project.md` | avant 2026-09 |
| Architecture technique | `architecture.md` | 2026-09 |
| Modèle du Core | `core-data-model.md` | 2026-09 |
| Modèle des abonnements | `subscription-data-model.md` | avant 2026-09 |
| Format d'import et d'export | `import-format.md` | 2026-09 |
| Identité et droits | `identity-and-access/` | 2026-09 |
| Frontend | `frontend/` | 2026-09 |
| Modèle de menaces, politique LLM | `security/`, `ingestion/llm-providers.md` | 2026-09 |
| Investments, RealEstate, Income | `investments-data-model.md`, `realestate/`, `income-data-model.md` | 2026-09 |
| Spikes techniques (Synapse, NodaTime, JSON, RFC 8785, EF Core, prêts, OIDC) | `spikes.md` | 2026-09-19 |
| Plan de l'étape 1 | `superpowers/plans/2026-09-19-stage-1-*.md` | 2026-09-19 |

## 3. Étape 1 : Noyau + Abonnements

Découpage et critères de « terminé » : `superpowers/plans/2026-09-19-stage-1-overview.md`.

| Sous-plan | Livrable | Détail | Statut |
|---|---|---|---|
| 1A | Fondation et Core : solution, types valeur, règles d'accès, persistance, pipeline Synapse, session OIDC, environnement de développement Aspire (`architecture.md` §10.3) | `2026-09-19-stage-1a-foundation-and-core.md` (7 tâches, toutes exécutées) | **terminé** (2026-09-22) |
| 1B | Moteur des abonnements (fonctions pures) | `2026-09-22-stage-1b-subscription-engine.md` : validation des entrées, parcours de la timeline, allocation exacte du coût et six requêtes d'extension (couverture à une date, date d'expiration, prochaine charge, coût cash sur une fenêtre, coût amorti sur une fenêtre, jours gratuits) | **terminé** (2026-09-22) |
| 1C1 | Cœur du module Abonnements : schéma `subscriptions`, entités, migrations, moteur 1B branché, API lecture et écriture, `IVisibleItems`, garde de sortie, banc anti-fuite | conception : `specs/2026-09-22-subscriptions-module-core-design.md` ; plan : `superpowers/plans/2026-09-22-stage-1c1-subscriptions-module-core.md` (8 tâches, toutes exécutées) | **terminé** (2026-09-22) |
| 1C2 | Catalogue de fournisseurs (libellés, icônes, source, suggestions) et `fx_rate` avec conversion, tâches Quartz | à détailler | à faire |
| 1D1 | Protocole d'import `nid.data` v1 : sessions, lots, staging, RFC 8785, `IImportSection`, sections `persons`, `subscriptions`, `billing_phases`, `purchases` ; une transaction unique entre modules | conception : `specs/2026-09-23-import-protocol-design.md` ; plan : `superpowers/plans/2026-09-23-stage-1d1-import-protocol.md` (10 tâches, toutes exécutées) | **terminé** (2026-09-23) |
| 1D2 | Export `nid.data` : manifeste, pages, `revision`, aller-retour | à détailler | à faire |
| 1D3 | Conversion du fichier existant : fichier de regroupement relu, rapport d'anomalies | à détailler | à faire |
| 1E | Application web : connexion, abonnements, assistant d'import, thèmes | à détailler | à faire |
| 1F | Livraison : images, compose de référence, sauvegarde, audit | à détailler | à faire |

**Décisions de périmètre confirmées le 2026-09-22** (`superpowers/plans/2026-09-19-stage-1-overview.md`) : `Valuation`, `Flow`, `Document` et `Proposal` passent à l'étape 2 et 4 (leurs premiers utilisateurs) ; `fx_rate` arrive avec 1C ; jetons d'accès et MCP restent à l'étape 4.

**Livrable de l'étape** : le foyer importe son fichier d'abonnements, voit le coût réel mensuel et la prochaine échéance de chaque abonnement, peut en ajouter et en modifier dans l'interface, connecté via Authelia, avec les éléments privés invisibles de l'autre adulte.

## 4. Étapes 2 à 6

| Étape | Documents de conception disponibles | Décisions ou conception restantes |
|---|---|---|
| 2. Placements et Revenus | `investments-data-model.md`, `income-data-model.md`, `frontend/dataviz.md`, `frontend/screens.md` §3 (tableau de bord) | Valorisations et flux du Core à construire ; tableau de bord par contributions enfichables |
| 3. Immobilier et crédits | `realestate/README.md`, `realestate/loans.md` (échéancier vérifié par comparaison sur 3 000 prêts) | Import des tableaux de la banque (dépend de l'étape 4 pour l'extraction) |
| 4. Automatisation IA | `architecture.md` §8 et §9, `ingestion/llm-providers.md`, `security/ai-ingestion.md`, `identity-and-access/tokens-and-credentials.md` | Comportement `IProposable` de la décision D10 à valider ; jeu d'évaluation d'extraction |
| 5. Synchronisation bancaire | `Project.md` §9 seulement | **Tout est à concevoir** : agrégateur DSP2, consentements, rapprochement, modèle de sécurité |
| 6. Approfondissements | `Project.md` §10 (idée : calcul de fiscalité) | Chaque idée à cadrer séparément |

## 5. Risques non levés

Repris de `spikes.md` §9. Chaque risque est rattaché à l'étape où il sera vérifié ; le vérifier est un critère de « terminé » de cette étape.

| Risque | Vérifié pendant | Statut |
|---|---|---|
| Quartz.NET sur PostgreSQL | 1C2 (premières tâches planifiées) | à faire |
| `IEventOutboxStorage` transactionnel complet (relances, lettres mortes) | premier consommateur réel (hors étape 1, décidé le 2026-09-22) | à faire |
| Client OpenAPI généré et bibliothèques du frontend | début de 1E | à faire |
| SDK MCP C# en exécution | étape 4 | à faire |
| `session_epoch` (« déconnecter partout ») et comportement de PAR avec `client_secret_post` | première fonctionnalité qui s'en sert (hors étape 1, décidé le 2026-09-22) | à faire (1A expose `session_epoch` mais ne l'incrémente pas) |
| RFC 8785 contre les vecteurs officiels | 1D1 | **levé** (2026-09-23) : vecteurs officiels et annexe B (`tests/vectors/rfc8785/`) |
| Même empreinte RFC 8785 dans le navigateur et le serveur | début de 1E (corpus `tests/vectors/rfc8785/corpus.json`) | à faire |

## 6. Décisions ouvertes

| Décision | À trancher avant | Où |
|---|---|---|
| Questions ouvertes de l'architecture | selon l'étape concernée | `architecture.md` §16 |

**Tranchées le 2026-09-22** : `Valuation`, `Flow`, `Document` et `Proposal` restent hors de l'étape 1 (leurs
premiers consommateurs sont les étapes 2 et 4) ; les plans d'exécution restent en anglais, le reste de la
documentation en français.

## 7. Journal

| Date | Événement |
|---|---|
| 2026-09-19 | Conception terminée ; spikes exécutés (Synapse, NodaTime, RFC 8785, EF Core, prêts, OIDC avec Authelia) ; plan de l'étape 1 écrit ; issues #91 à #96 et PR #97 et #98 ouvertes sur UnambitiousFx/Synapse |
| 2026-09-22 | Sous-plan 1A exécuté de bout en bout : solution, types valeur, règles d'accès, persistance Core, pipeline Synapse, session OIDC (BFF) et pile de développement Aspire ; 35 tests unitaires et 31 d'intégration verts, test de fumée OIDC complet. La tâche 7 a été reprise de `docker compose` vers l'AppHost Aspire (trois corrections, `docs/spikes.md` §8bis). |
| 2026-09-22 | Décisions tranchées avant 1B : périmètre (`Valuation`, `Flow`, `Document`, `Proposal` hors étape 1) et langue des plans (anglais). |
| 2026-09-22 | Sous-plan 1B exécuté de bout en bout : validation des entrées, parcours de la timeline, allocation exacte du coût et six requêtes d'extension (`Nid.Subscriptions.Contracts`, `Nid.Subscriptions.Domain`) ; les trois exemples travaillés de `subscription-data-model.md` (Spotify avec changement de prix et carte cadeau, terme prépayé NordVPN avec mois bonus, bundle de jeu) reproduisent leurs résultats documentés. `IsAotCompatible` vérifié sur `Nid.Subscriptions.Domain` (0 warning, `spikes.md` §9bis). 130 tests verts (35 `Nid.Core.Tests`, 31 `Nid.Core.IntegrationTests`, 64 `Nid.Subscriptions.Tests`). |
| 2026-09-22 | 1C découpé en 1C1 (cœur du module, débloque 1D et 1E) et 1C2 (catalogue et `fx_rate`) : tel qu'écrit, 1C dépassait 1A et 1B réunis. L'outbox transactionnel et l'incrément de `session_epoch` sortent de l'étape 1, faute de consommateur. Conception de 1C1 écrite. |
| 2026-09-22 | Sous-plan 1C1 exécuté de bout en bout : schéma `subscriptions` (table `coverage_grant`, `grant` étant réservé en PostgreSQL), entités, migrations coexistant avec celles du Core, moteur 1B branché, API de lecture et d'écriture, `IVisibleItems`, garde de sortie (`OutputGuardBehavior`, dernier maillon du pipeline), banc anti-fuite bloquant en CI et découvrant les requêtes par réflexion ET en rejouant les `RegisterGroup`s générés par Synapse (l'union des deux, chacune ayant son angle mort). Risque « migrations de plusieurs `DbContext` sur des schémas distincts » levé par `SchemaTests` (`spikes.md` §9ter) et retiré du tableau des risques non levés. Limite connue épinglée plutôt que corrigée : le coût amorti du foyer compte deux fois un achat partagé alors que le coût cash ne le compte qu'une fois (`KnownLimitationTests`, `docs/superpowers/specs/2026-09-22-subscriptions-module-core-design.md` §10). 225 tests verts (81 `Nid.Subscriptions.Tests`, 35 `Nid.Core.Tests`, 37 `Nid.Core.IntegrationTests`, 72 `Nid.Subscriptions.IntegrationTests`), build à 0 avertissement. |
| 2026-09-22 | Six corrections faites après que 1C1 a été marqué terminé, avant que 1D1 ne s'appuie dessus : l'acteur est désormais vérifié contre chaque don (`grant`) touché par un achat (`0f7f479`) ; le banc anti-fuite gagne un second axe, les requêtes qui nomment deux éléments (`828eb48`) ; l'acteur est vérifié à la création d'un élément (`317e088`) ; le parcours des réponses non gardées s'arrête à la racine seulement (`3c8b22c`) ; une date ou une portée malformée est refusée plutôt que remplacée par une valeur par défaut (`37ef98b`) ; le double comptage amorti d'un achat partagé (limite connue de 1C1) est désormais signalé comme un constat du banc, pas laissé à la documentation seule (`cdf43b3`). |
| 2026-09-23 | 1D découpé en 1D1 (protocole d'import), 1D2 (export) et 1D3 (conversion du fichier existant) : l'exploration a trouvé quatre travaux que l'aperçu de l'étape 1 ne montrait pas — schéma `platform` et son `DbContext`, colonnes de provenance sur les schémas existants, `revision` d'export qui dépend d'un journal d'audit livré seulement en 1F, et un canonicalisateur navigateur sans code navigateur où vivre (`specs/2026-09-23-import-protocol-design.md` §1). Sous-plan 1D1 exécuté de bout en bout : sessions d'import (`POST/GET/DELETE /imports`), lots bornés reçus, validés et stockés en zone de staging (`PUT /imports/{id}/batches/{section}/{n}`), validation complète et commit revalidé en une transaction unique couvrant Core, Subscriptions et Platform (`IUnitOfWork`), sections `persons`, `subscriptions`, `billing_phases`, `purchases` via `IImportSection`, canonicalisation RFC 8785 vérifiée contre les vecteurs officiels et l'annexe B. Décisions clés (conception §2 et §5) : `providers` refusée tant que le catalogue de 1C2 n'existe pas ; `replace` reporté à 1F, qui a besoin de son journal d'audit ; un adulte est résolu par la correspondance donnée à la création de la session puis par `email`, jamais créé par l'import ; l'idempotence repose sur `import_hash` (SHA-256 RFC 8785 de l'enregistrement), un conflit bloquant toujours le commit sans `replace`. 399 tests verts (41 `Nid.Core.Tests`, 43 `Nid.Subscriptions.Tests`, 81 `Nid.Platform.Tests`, 39 `Nid.Core.IntegrationTests`, 195 `Nid.Subscriptions.IntegrationTests`), build à 0 avertissement. |
