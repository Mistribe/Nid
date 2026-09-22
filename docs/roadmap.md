# Feuille de route et état d'avancement

> Dernière mise à jour : 2026-09-22. **Ce fichier est la référence de l'état du projet.** Le contenu et la valeur de chaque étape restent décrits dans `Project.md` §10 ; les plans d'exécution sont dans `superpowers/plans/`.
> Règle : le dernier commit de chaque plan ou sous-plan met à jour ce fichier (statut, date, décisions prises, écarts). Un statut périmé est un défaut.

Statuts : `à faire` · `en cours` · `terminé` (avec la date) · `bloqué` (avec la raison).

---

## 1. Vue d'ensemble

| Étape | Contenu (voir `Project.md` §10) | Conception | Plan d'exécution | Statut |
|---|---|---|---|---|
| **0. Conception** | Architecture, modèles de données, sécurité, frontend, format d'import, spikes | Terminée | sans objet | **terminé** (2026-09-19) |
| **1. Noyau + Abonnements** | Propriétaires, montants ; module Abonnements complet ; reprise du fichier existant | Terminée | `superpowers/plans/2026-09-19-stage-1-overview.md` | **en cours** (1A terminé le 2026-09-22) |
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
| 1B | Moteur des abonnements (fonctions pures) | à détailler | à faire |
| 1C | Module Abonnements : schéma, API, catalogue, `fx_rate`, garde de sortie, banc anti-fuite | à détailler | à faire |
| 1D | Import et export `nid.data` v1, conversion du fichier existant | à détailler | à faire |
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
| Migrations de plusieurs `DbContext` sur des schémas distincts | 1C (deuxième module) | à faire |
| Quartz.NET sur PostgreSQL | 1C (premières tâches planifiées) | à faire |
| `IEventOutboxStorage` transactionnel complet (relances, lettres mortes) | 1C (premier événement à ne pas perdre) | à faire |
| Client OpenAPI généré et bibliothèques du frontend | début de 1E | à faire |
| `IsAotCompatible` sur les projets `Domain` | 1B | à faire |
| SDK MCP C# en exécution | étape 4 | à faire |
| `session_epoch` (« déconnecter partout ») et comportement de PAR avec `client_secret_post` | 1A/1C | à faire (reporté en 1C : 1A expose `session_epoch` dans la session mais ne l'incrémente pas) |
| RFC 8785 contre les vecteurs officiels | 1D | à faire |

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
