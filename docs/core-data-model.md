# Modèle de données du Core

> Statut : brouillon issu de la discussion technique. Décrit les entités du module **Core** : identité, éléments, propriété, valorisations, flux, documents, propositions, taux de change.
> Références : `Project.md` §5.1 (noyau commun), `architecture.md` §5 et §6 (modules, conventions de données), `subscription-data-model.md` (module Abonnements, qui n'utilise pas `Valuation` ni `Flow`), `investments-data-model.md` et `realestate/` (modules qui interprètent les valorisations et les flux).

---

## 1. Objectifs

- Un **registre unique** des éléments suivis (abonnement, enveloppe, bien, crédit), avec leurs propriétaires et leur visibilité, pour que les droits soient appliqués en un seul endroit.
- Des **faits datés** (valorisations, flux) stockés sans interprétation : Core stocke, les modules interprètent (gain, coût réel…).
- Une **traçabilité** complète : chaque donnée saisie ou importée porte sa source, son auteur, son document d'origine.
- Une **file de validation** générique pour toute donnée issue d'une automatisation (IA, banque, MCP).
- Un **historique jamais réécrit** : une correction ajoute, elle ne modifie pas.

## 2. Conventions

Les conventions transverses sont celles d'`architecture.md` §6 ; rappel de celles qui structurent ce document :

- **Argent** : `numeric(19,4)` + `char(3)` (ISO 4217). Jamais de flottant. Conversion à l'affichage seulement.
- **Dates** : calendaires (`date`), fuseau du foyer pour déterminer « aujourd'hui ». Instants en `timestamptz` UTC.
- **Identifiants** : UUIDv7 générés par l'application.
- **Énumérations** : stockées en texte (`text` + contrainte `CHECK`), pas de types `enum` Postgres, pour faciliter les migrations.
- Toutes les tables vivent dans le schéma Postgres `core`. Aucun autre module ne les lit directement : accès par les contrats (§9).

## 3. Vue d'ensemble

```
household_settings (1 ligne)

person ──< ownership >── item ──< valuation
                          │  ├──< flow
                          │  └── (documents : module Documents, `documents-data-model.md`)
                          │
proposal ── target_item_id ┘   (document_id, job_id : références de provenance)

fx_rate (indépendante)
```

- `item` est référencé par les modules via `item_id` (lien logique, pas de clé étrangère entre schémas).
- `valuation` et `flow` sont rattachées à un `item`.
- `proposal` peut viser un `item` existant ou un futur élément d'une personne.

## 4. Identité, éléments, propriété

### 4.1 Tables

**`household_settings`** (une seule ligne)

| Champ | Type | Remarques |
|---|---|---|
| `name` | text | Nom du foyer |
| `reference_currency` | char(3) | Devise des totaux (défaut `EUR`). Modifiée par un admin |
| `max_adults` | int | Plafond d'adultes, en attente compris (défaut 2, au moins 1 et au moins le nombre d'adultes). Modifié par un admin (`identity-and-access/administration-and-audit.md` §2) |
| `timezone` | text | Fuseau IANA (défaut `Europe/Paris`) : jour courant, tâches planifiées |
| `locale` | text | Langue et formats d'affichage |
| `projection_assumptions` | jsonb? | Hypothèses de la projection du patrimoine (`superpowers/specs/2026-10-27-networth-projection-design.md` §2.1) : `ClassRates` (taux annuel nominal par classe d'actifs, −50 à 50 %), `Inflation` (−5 à 20 %, 2 % par défaut), `InflationByYear`, `ClassRatesByYear` (tables année civile → taux, de l'année en cours à +4 ; `cash` et `money_market` seulement), `IncomeGrowth` (évolution des revenus au-delà de l'inflation, −5 à 10 %, 0 par défaut ; #419, PR 3), `ClassVolatility` (volatilité annuelle par classe, 0 à 50 %, pour les fourchettes de la #9 ; absente ou classe manquante : liquidités, monétaire, fonds garantis et autres 0, obligations 6 %, mixte 10 %, immobilier 10 %, actions 16 %), `WithdrawalRate` (taux de retrait annuel du capital à l'indépendance financière, au-dessus de 0 et au plus 10 %, 4 % par défaut ; absent d'un ancien document : 4 %) et `MonthlyLivingCost` (coût de vie mensuel en euros d'aujourd'hui, devise de référence, au moins 0, 0 par défaut ; retraite, spec §3), `MonthlySavings` (`null` = automatique, sinon un montant signé), `SavingsClass`, `FreedPaymentsToSavings` (clés du JSON stocké en PascalCase ; sur l'API, les mêmes en camelCase). Absente : les valeurs par défaut. Lue et modifiée par tout adulte (`GET`, `PUT` et `PATCH /api/v1/core/projection-assumptions`, ce dernier partiel et acceptant un profil de marché `prudent` / `balanced` / `optimistic`, jamais stocké mais déduit des taux ; l'effet de chaque profil sur `GET …/profiles` ; #155), à la différence des autres réglages du foyer |

Indépendance financière (retraite, spec §3) : la projection ajoute à chaque mois une série « capital, revenus couverts, dépenses » (capital × taux de retrait ÷ 12 + pensions + loyers, face aux engagements hors crédits plus `MonthlyLivingCost`, indexés, plus les mensualités des crédits ; la vente d'un bien d'un scénario en retire le loyer et les charges). Le mois d'indépendance est le premier où les revenus couverts atteignent les dépenses, seulement si `MonthlyLivingCost` > 0 (`financialIndependence`, jalon `financial_independence`). `GET /api/v1/retirement` lit la projection de base sur 40 ans : départs et pensions par adulte, revenu aujourd'hui et à la retraite, écart et capital pour le combler (avertissement `living_cost_missing`).

**`person`**

| Champ | Type | Remarques |
|---|---|---|
| `id` | uuid | |
| `kind` | text | `adult` (peut se connecter) ou `dependent` (enfant du foyer, sans connexion) |
| `oidc_subject` | text?, unique | `sub` fourni par le fournisseur d'identité (Pocket ID : l'identifiant de la personne). Vide pour un `dependent`, et pour un `adult` **en attente** (pré-enregistré, pas encore connecté) |
| `display_name` | text | Repris des claims OIDC pour un `adult`, saisi pour un `dependent` |
| `email` | text? | Saisi au pré-enregistrement, puis repris des claims OIDC ; clé de rattachement d'un adulte en attente, unique sans tenir compte de la casse parmi les `adult` ; vide pour un `dependent` |
| `is_admin` | bool | Rôle technique (`identity-and-access/administration-and-audit.md` §2). Faux pour un `dependent` ; au moins un `adult` admin dès qu'un adulte existe |
| `birth_date` | date? | Date de naissance, facultative ; sans elle, pas de retraite projetée (`projection.no_birth_date`). Modifiée avec l'âge de retraite par `PUT /api/v1/core/persons/{id}/retirement`, que l'assistant peut proposer (`propose_set_retirement`, #284) |
| `retirement_age` | int | Âge de retraite, de 50 à 75 (64 par défaut) ; la retraite tombe le dernier jour du mois de l'anniversaire |
| `retirement_months` | int | Mois en plus de l'âge de départ, de 0 à 11 (0 par défaut) ; le mois de départ est celui où la personne atteint l'âge plus ces mois, et le basculement de la projection (pensions, déblocage du PER) a lieu le mois suivant |
| `session_epoch` | int | Incrémenté par « déconnecter partout » : les cookies d'un ancien epoch sont refusés (`identity-and-access/authentication.md` §3) |
| `preferences` | jsonb | Préférences d'interface de l'adulte : `theme`, `mode`, `chart_patterns`, `guides_seen` (identifiant de guide → version vue, remplacé en entier par le PATCH) (`frontend/design-system.md` §7.6) et `ai_cloud_default` (`ask` ou `never`, `ingestion/llm-providers.md` §6). Lues par `GET /auth/session`, modifiées par `PATCH /api/v1/core/me/preferences` (fusion des clés fournies). Vide pour un `dependent` |
| `created_at` | timestamptz | |

**`item`** : registre central

| Champ | Type | Remarques |
|---|---|---|
| `id` | uuid | |
| `module`, `kind` | text | ex. `subscriptions` / `subscription`, `investments` / `envelope`, `realestate` / `property`, `realestate` / `loan` |
| `display_name` | text | **Dénormalisé** : tenu à jour par le module via un contrat, pour que listes et tableau de bord n'interrogent pas les modules |
| `visibility` | text | `shared` (défaut) ou `private` |
| `share_level` | text | #294 : `view` (défaut, lecture seule) ou `edit` (lecture et modification) ; toujours `view` sur un élément `private` |
| `currency` | char(3)? | Devise de l'élément (voir §5.5). Vide pour les éléments qui n'utilisent pas `Valuation` ni `Flow` |
| `archived_at` | timestamptz? | Masque un élément clos ; son historique reste comptable |
| `created_at` | timestamptz | |

**`ownership`**

| Champ | Type | Remarques |
|---|---|---|
| `item_id`, `person_id` | uuid | Clé primaire composite |
| `share_bp` | int | Part en **points de base** : 10 000 = 100 %. 50/50 = 5000 + 5000. Pas de décimales, pas d'écart d'arrondi |

### 4.2 Invariants

Vérifiés à l'écriture par Core, doublés de tests d'intégration :

- Un `item` a **au moins un** propriétaire ; la somme de ses `share_bp` vaut exactement 10 000 ; chaque `share_bp` est compris entre 1 et 10 000.
- `visibility = private` ⇒ **exactement un** propriétaire, et `share_level = view` (contrainte `ck_item_share_level`).
- Ajouter un propriétaire à un élément privé le passe en `shared`.
- Un `item` terminé est clos (archivé). Il n'est supprimé physiquement que par la suppression d'élément (#23), pour un élément qui n'aurait jamais dû exister : avec ses lignes propres, ses faits, ses propriétaires, ses liens de documents et ses références bancaires (liens et règles bancaires, lien d'un compte vers une enveloppe), en une transaction, après un aperçu du serveur. Elle est refusée (`core.item_in_use`) quand quelque chose hors de l'élément en dépend (liste par module dans la doc de chaque module). Un contrat de garde dont des mois sont validés est refusé (`validated_statements`) : les rouvrir du plus récent au plus ancien. La `version` est celle de l'élément. Les chiffres passés sont recalculés. Un ré-import le recrée. Ses lignes d'audit restent, mais ne sont plus lisibles par l'élément. Les propositions en attente sur l'élément passent à `superseded` (pas de blocage).
- `module` + `kind` appartiennent au module : Core n'interprète pas ces valeurs.
- Un `adult` actif a un `oidc_subject` ou un compte local (`core.local_credential`, `identity-and-access/local-accounts.md` §13), un `adult` en attente et un `dependent` n'en ont aucun. Le nombre d'`adult` ne dépasse pas `max_adults`.
- Dès qu'un `adult` existe, au moins un `adult` est admin ; un `dependent` ne l'est jamais. Le rôle n'entre pas dans la matrice de droits (§4.3).
- Seul un `adult` en attente peut être supprimé : un adulte actif a pu posséder des éléments.
- Un `dependent` **n'agit jamais** : il ne se connecte pas et ne crée rien. Il peut être propriétaire d'un élément ou utilisateur d'un service.
- Un élément dont les seuls propriétaires sont des `dependent` ne peut pas être `private` (les deux adultes seraient exclus).

**Décision : parts actuelles partout.** Il n'existe qu'une répartition par élément, appliquée à tout l'historique. Passer un bien de 100 % à 50/50 en 2025 réécrit donc aussi la répartition affichée pour 2023. Les changements de propriétaires sont rares dans un couple et l'audit (`architecture.md` §6) en garde la trace. Si le besoin de répartitions datées apparaît, il faudra ajouter une date d'effet à `ownership` et une jointure temporelle dans les agrégats du tableau de bord.

### 4.3 Matrice de droits

Appliquée par `IAccessPolicy` (règles de `Project.md` §5.1) à l'API, au tableau de bord et au MCP. Le détail (acteur, portées, pipeline, fonctions `Readers` / `Writers`, garde de sortie) est dans `identity-and-access/authorization.md`.

| Élément | Lecture | Écriture | Total du foyer |
|---|---|---|---|
| `shared`, 1 propriétaire, niveau `view` | les deux | le propriétaire seul | oui |
| `shared`, niveau `edit` | les deux | les deux, sans que l'autre devienne propriétaire | oui, au prorata des parts (inchangées) |
| `shared`, 2 propriétaires | les deux | les deux | oui, au prorata des parts |
| `private` (1 propriétaire adulte) | le propriétaire | le propriétaire | non ; compte seulement dans son total « moi » |
| au moins un propriétaire `dependent` | les deux adultes | les deux adultes (ils gèrent les biens de l'enfant) | oui, ventilé par personne |

- **Changer la visibilité** : le propriétaire seul. `private` → `shared` est possible ; `shared` → `private` est réservé au propriétaire unique.
- **Changer le niveau de partage** (#294) : les propriétaires seuls, comme les propriétaires eux-mêmes ; un adulte qui modifie un élément au niveau `edit` n'en change ni le niveau, ni la visibilité, ni les propriétaires.
- **Répartition par personne** : somme des `share_bp` × valeur de l'élément.
- **Total « moi »** : éléments partagés au prorata de mes parts, plus mes éléments privés.
- **Personne agissante** : les contrats d'accès (`IAccessPolicy`, `IVisibleItems`) prennent toujours un `adult`. La répartition par personne (« moi », « ma femme », un enfant) couvre tous les `person`, y compris les `dependent`.

### 4.4 Amorçage des utilisateurs

- **Comptes locaux** (#215, `identity-and-access/local-accounts.md`) : en mode `local` ou `both`, le premier adulte, admin, est créé par la page de mise en place ou par `nid admin create-admin` ; les suivants sont pré-enregistrés (email facultatif en `local`) puis choisissent leur mot de passe par un lien à usage unique. Les points suivants décrivent le mode `oidc`.
- Le fournisseur d'identité est la source des identités, de l'accès (groupe `nid-users`) et du rôle admin (groupe `nid-admins`, recalculé à chaque connexion dans `is_admin`). À la première connexion d'un `sub` inconnu, Core rattache l'adulte en attente de même email ou crée la `person` de type `adult` depuis les claims (`sub`, nom, email), dans la limite du plafond du foyer (`identity-and-access/authentication.md` §5).
- Un admin peut **pré-enregistrer** un adulte (nom, email, `oidc_subject` vide : « en attente »), par exemple pour lui attribuer des éléments avant sa première connexion ; il est rattaché à sa première connexion par l'email. Sans pré-enregistrement, la première connexion crée l'adulte. Un email déjà porté par un autre `sub` est refusé (`../identity-and-access/authentication.md` §5).
- Le nombre d'adultes, en attente compris, est plafonné par `household_settings.max_adults` (2 par défaut, réglable dans la zone admin).
- Les `dependent` sont créés par un adulte dans l'interface (un nom suffit), sans connexion.

### 4.5 Jetons et identifiants de service

Deux tables du schéma `core`, détaillées dans `identity-and-access/tokens-and-credentials.md`.

**`personal_access_token`** : jeton d'un adulte pour le MCP et les automatisations.

| Champ | Type | Remarques |
|---|---|---|
| `id`, `person_id` | uuid | |
| `name` | text | |
| `prefix` | text | 8 caractères |
| `token_hash` | char(64) | SHA-256 du jeton complet |
| `scopes` | text[] | `read`, `write`, `write:direct` |
| `created_at`, `expires_at` | timestamptz | Expiration obligatoire |
| `last_used_at?`, `revoked_at?` | timestamptz | |

**`worker_credential`** : identifiant de service d'un worker d'ingestion.

| Champ | Type | Remarques |
|---|---|---|
| `id`, `name` | uuid, text | |
| `secret_hash` | char(64) | SHA-256 |
| `job_types` | text[] | Types de jobs traitables |
| `created_at`, `revoked_at?` | timestamptz | |
- La première connexion crée aussi `household_settings` avec les valeurs par défaut, modifiables ensuite.

### 4.6 Avis d'imposition (`tax_assessment`)

Un avis d'imposition sur les revenus d'une année passée, saisi à la main ou proposé par un assistant (`superpowers/specs/2026-10-27-tax-assessments-design.md`). Les montants vivent dans un document JSON ; une table de jointure porte ce qu'il faut pour les droits et l'unicité.

**`tax_assessment`**

| Champ | Type | Remarques |
|---|---|---|
| `id` | uuid | Clé primaire (`pk_tax_assessment`) |
| `year` | int | Année des revenus, de 2000 à 2100 (`ck_tax_assessment_year`) |
| `data` | jsonb | L'avis : année d'émission, parts, revenu fiscal de référence, impôts, crédits, solde, déclarants et `documentId` du PDF source |
| `xmin` | xid | Version de ligne : une correction l'envoie en retour, un écart répond en conflit |

**`tax_assessment_declarant`**

| Champ | Type | Remarques |
|---|---|---|
| `assessment_id` | uuid | Clé étrangère vers `tax_assessment`, `ON DELETE CASCADE` ; clé primaire composite avec `person_id` |
| `person_id` | uuid | Clé étrangère vers `person`, `ON DELETE RESTRICT` (`fk_tax_assessment_declarant_person_person_id`) ; indexé |
| `year` | int | Recopie de l'année de l'avis |
| `rank` | int | 1 ou 2 (`ck_tax_assessment_declarant_rank`) |

- Index unique `(year, person_id)` : une personne a au plus un avis par année, seule ou à deux ; un doublon répond 422 `tax.declarant_taken` à `declarants[i].personId`.
- Les avis appartiennent à leurs déclarants, sans élément ni propriété : seuls les déclarants les lisent (« Foyer » = les avis à deux déclarants, « Moi » = les siens). Une personne qui figure sur un avis ne peut plus être supprimée.
- Les deux tables sont auditées sans élément (`AuditRule.NoItem`) ; le PDF source est relié à chaque déclarant (`core.person`, rôle `tax_notice`, `documents-data-model.md` §5).

### 4.7 Scénarios (`scenario`)

Le document d'un scénario « et si », rattaché à un élément du registre (`item`) : le scénario d'achat immobilier (`superpowers/specs/2026-10-22-property-purchase-scenario-design.md` §5) et, depuis la tranche 2 de la #102, le scénario « Projection » (`superpowers/specs/2026-10-27-networth-projection-design.md` §6), dont les surcharges portent aussi l'inflation et les taux des liquidités et du monétaire année par année, et l'évolution des revenus (#419, PR 3).

**`scenario`**

| Champ | Type | Remarques |
|---|---|---|
| `item_id` | uuid | Clé primaire et clé étrangère vers `item`, `ON DELETE CASCADE` |
| `kind` | text | `property_purchase` ou `projection` (`ck_scenario_kind`) |
| `scope` | text | `me` ou `household` : la portée à laquelle le scénario se lit (`ck_scenario_scope`) |
| `data` | jsonb | Le document, selon `kind` |

Document `projection` (`ProjectionScenario`) :

| Champ | Contenu |
|---|---|
| `version` | 1 |
| `horizon` | `years` (1 à 40) ou `retireOf` (une personne : sa retraite), jamais les deux |
| `assumptions` | Surcharges des hypothèses du foyer : `classRates` et `classVolatility` (par classe, fusionnés clé par clé), `inflation`, `monthlySavings`, `savingsClass`, `freedPaymentsToSavings`, `withdrawalRate`, `monthlyLivingCost` ; un champ absent garde celui du foyer |
| `events` | 30 événements au plus, chacun avec `id` (uuid), `kind`, `start` (et `until` pour les changements), `label` facultatif |
| `retirements` | Facultatif (absent : vide), un départ par adulte au plus : `person`, `years` (50 à 75) et `months` (0 à 11, avec `years`) facultatifs (absents : le départ du foyer), `monthlyPension` facultatif (≥ 0, en euros d'aujourd'hui) ; une entrée fixe un âge ou une pension. Il remplace le départ du foyer dans ce scénario ; une pension de scénario s'ajoute à celle, sans titulaire, que plusieurs adultes détiennent ensemble ; une personne inconnue ou à charge est refusée à l'écriture (`projection.target_not_found`, `retirements[i].person`) |

Fourchettes (#9, `superpowers/specs/2026-11-10-projection-ranges-design.md`) : sur demande (`?ranges=true` sur `GET /api/v1/projection` et `GET /api/v1/scenarios/projection/{id}`, `ranges` de l'outil MCP `get_projection`), le serveur relance la projection 1 000 fois avec des rendements annuels aléatoires et répond `ranges` : les 10ᵉ, 50ᵉ et 90ᵉ centiles de chaque point (nominal et en euros d'aujourd'hui) et la part des tirages où l'épargne disponible tient jusqu'à l'horizon (`success`, pourcentage entier). Un seul tirage normal par année civile, commun à toutes les classes (un facteur de marché) : l'année d'une classe croît de (1 + taux) × exp(σz − σ²/2), réparti sur ses mois. L'inflation, les revenus, les prêts, la valeur des biens et les événements restent déterministes. Graine fixe : mêmes entrées, mêmes fourchettes ; volatilités nulles : chaque centile égale la projection. Budget : 40 ans, 20 lignes d'épargne, 1 000 tirages en moins d'une seconde sur 4 cœurs (un test échoue au-delà de 5 s).

Une date (`start`, `until`) est soit un jour (`on`), soit une personne avec un âge de 18 à 100 ans (`person`, `age`) : le dernier jour du mois où elle atteint cet âge. Les champs d'un événement selon son `kind` :

| `kind` | Champs |
|---|---|
| `income_change` · `savings_change` | `monthlyAmount` (signé, variation mensuelle), `until` facultatif (après `start`) |
| `early_repayment` | `amount` (> 0), `split` (`highest_rate`, `smallest_balance`, `chosen`), `loan` (si `chosen`, interdit sinon), `effect` (`reduce_term`, `reduce_payment`), `penalty` (≥ 0, saisie) |
| `property_sale` | `property`, `price` facultatif (vide : valeur projetée), `costs` (≥ 0) ; un bien se vend une fois |
| `property_purchase` | `scenario` : un scénario d'achat enregistré ; la date est sa `purchase_on` |

Les cibles (biens, prêts, scénarios d'achat, personnes) sont des identifiants logiques, sans clé étrangère : elles sont revérifiées (existence, visibilité, archivage) à chaque lecture et écriture. Un événement dont la cible n'est plus visible du lecteur est écarté et compté dans l'avertissement `hidden_targets`. Un remplacement (`PUT`) conserve les événements que l'auteur ne voit pas si leurs cibles existent encore et sont actives, et abandonne ceux dont la cible a été archivée ou la personne retirée. Le document ne contient aucun montant calculé.

## 5. Valorisations et flux

Core **stocke** des faits datés ; il ne calcule aucun gain. Investments définit le gain, RealEstate le coût réel du bien.

### 5.1 Provenance

Colonnes communes à `valuation` et `flow` :

| Colonne | Valeurs / remarques |
|---|---|
| `source` | `manual`, `bank_api`, `document`, `import` |
| `created_via` | `ui`, `api`, `mcp`, `job`, `import` |
| `created_by_person_id` | La personne au nom de qui la donnée est saisie. Pour la validation d'une proposition : celui qui valide |
| `document_id?`, `proposal_id?` | Traçabilité jusqu'à la source. `document_id` (tranche 3 de la #161, `documents-data-model.md` §11) désigne un `documents.document`, sans clé étrangère entre schémas : la validation (ou l'application directe) d'une proposition qui cite un document l'écrit sur les valeurs et flux créés, et une correction le garde. Les contrats `Valuation` et `Flow` exposent `DocumentId` ; les vues qui le transmettent au navigateur passent par `DocumentMaskBehavior` (`documents-data-model.md` §11) |
| `created_at` | timestamptz UTC |

**Livré en 2A** (2026-09-29, `superpowers/specs/2026-09-29-core-dated-facts-design.md`) : tables `core.valuation` et `core.flow`, contrats `IValuations` et `IFlows`, codes `core.item_without_currency`, `core.currency_mismatch`, `core.date_in_future`, `core.amount_not_positive`, `core.invalid_fact`, `core.fact_not_found`, `core.fact_retired`, `core.opening_exists`, `core.external_ref_taken`, `core.fact_conflict`. `proposal_id` arrive à l'étape 4 avec sa table, `document_id` avec la tranche 3 de la #161 (`documents-data-model.md` §11, livré le 2026-10-26) ; `source` vaut `manual` ou `import`. Une valeur ou un flux est écrit et lu par une personne.

**Livré en 5A** (2026-10-29, `superpowers/specs/2026-10-29-bank-sync-design.md` §4) : `FactSource.BankApi` (`source = bank_api`) pour les valorisations écrites depuis le solde d'un compte bancaire qui applique ses soldes, avec `created_via = job` ; le contrat `Valuation` expose `Source`, pour que la synchronisation reconnaisse une valeur saisie ou importée à la même date et ne la remplace pas. Une proposition déposée par la synchronisation porte aussi `created_via = job` (contrainte `ck_proposal_created_via`, §7.1). Les flux n'ont pas encore de source `bank_api` (étape 5B à 5D).

### 5.2 `valuation` : « cet élément vaut X à telle date »

| Champ | Type | Remarques |
|---|---|---|
| `id` | uuid | |
| `item_id` | uuid | |
| `as_of` | date | Pas dans le futur (fuseau du foyer) |
| `amount`, `currency` | numeric(19,4), char(3) | Peut être négatif (compte à découvert). Devise = celle de l'élément |
| `note?` | text | ex. « estimation en ligne », « relevé du 31/12 » |
| provenance | | Voir §5.1 |
| `supersedes_id?`, `retired_at?` | uuid, timestamptz | Voir §5.4 |

- Valeur de **l'élément entier** (100 %), jamais de la part d'un propriétaire : la part s'applique au calcul.
- **Valeur à une date d** = dernière valorisation courante avec `as_of ≤ d` (dernière valeur connue). Le contrat renvoie aussi l'`as_of` utilisé : le tableau de bord signale les valeurs **périmées** (seuil configurable, 45 jours proposé).
- Un élément sans valorisation antérieure à `d` n'a **pas de valeur** à `d` (résultat « inconnu », pas zéro).

### 5.3 `flow` : mouvement d'argent daté

| Champ | Type | Remarques |
|---|---|---|
| `id` | uuid | |
| `item_id` | uuid | |
| `occurred_on` | date | |
| `kind` | text | `contribution`, `withdrawal`, `opening`, `income`, `expense` |
| `amount`, `currency` | numeric(19,4) **> 0**, char(3) | Le sens vient du `kind`, jamais du signe |
| `category?` | text | Libellé défini par le module : `travaux`, `taxe_fonciere`, `loyer`, `dividende`… |
| `external_ref?` | text | Identifiant externe (transaction bancaire, référence de relevé) |
| `note?` | text | |
| provenance | | Voir §5.1 |
| `supersedes_id?`, `retired_at?` | uuid, timestamptz | Voir §5.4 |

Sens des `kind` (contrat, pas calcul) :

| `kind` | Signification |
|---|---|
| `contribution` | Argent apporté de l'extérieur (versement) |
| `withdrawal` | Argent retiré vers l'extérieur (retrait) |
| `opening` | Total net des versements **antérieurs** au début du suivi, daté du jour de la première valorisation (`Project.md` §13). Au plus **un** courant par élément |
| `income` | Revenu versé hors de l'élément (loyer, dividende non réinvesti) |
| `expense` | Coût pour détenir l'élément (travaux, taxe foncière, charges, frais) |

Interprétation par les modules (exemples) :

- **Investments** : `gain = valeur − (opening + Σ contributions − Σ withdrawals)`, arrêtés à la date voulue. `income` et `expense` sont affichés à part, hors de cette formule.
- **RealEstate** : le coût réel du bien additionne les `expense`.

### 5.4 Historique : chaîne `supersedes_id`

Aucune ligne de `valuation` ni de `flow` n'est supprimée physiquement, sauf à la suppression d'un élément (#23, §4.2) ; une ligne n'est modifiée que pour recevoir `retired_at`, ou `document_id` quand la proposition qui l'a créée est appliquée (même transaction).

- **Correction** : une nouvelle ligne est créée avec `supersedes_id` vers l'ancienne, et l'ancienne reçoit `retired_at`, **dans la même transaction**. Une valeur ressaisie à sa date est une correction. La nouvelle ligne garde le `document_id` de l'ancienne ; une proposition validée y écrit ensuite le sien.
- **Annulation** : `retired_at` est renseigné sans ligne de remplacement.
- Les lectures ne voient que `retired_at IS NULL`.
- « Qu'affichait-on le 3 mars ? » reste reconstructible avec `created_at` et `retired_at` (pour l'audit ; aucun écran prévu).

Cette chaîne remplace la règle « la plus récente gagne par `(item, as_of)` » : elle ne convient pas aux flux, qui n'ont pas de clé naturelle (plusieurs versements le même jour).

### 5.5 Devise

- Toutes les valorisations et tous les flux d'un élément sont dans **`item.currency`**, vérifié à l'écriture.
- Les **gains** se calculent dans la devise de l'élément. Seuls les **totaux** sont convertis, au taux de la date de la valorisation ou du flux (voir §8).

### 5.6 Routine mensuelle

La valeur du mois précédent est **proposée** dans le formulaire (principe 2 de `Project.md`). Rien n'est enregistré avant la validation par l'utilisateur.

## 6. Documents

Les documents ne sont plus dans Core : le module `Nid.Documents` possède le fichier, ses métadonnées et ses liens (`documents-data-model.md`, livré en tranche 1 de la #161, 2026-10-26). Les tables `document` et `document_item` décrites ici auparavant sont remplacées par `documents.document` et `documents.document_link`.

## 7. Propositions (file de validation)

### 7.1 Table `proposal`

| Champ | Type | Remarques |
|---|---|---|
| `id` | uuid | |
| `module`, `kind` | text | ex. `investments.record_valuation`, `realestate.import_amortization`, `subscriptions.observed_charge` |
| `target_item_id?` | uuid | Élément visé (sans FK : une proposition survit à sa cible) |
| `for_person_id` | uuid | Personne concernée ; pour un nouvel élément : celui qui a déposé |
| `subject_key?` | text | Clé métier définie par le module : date de valorisation, période d'un relevé… |
| `payload`, `payload_version` | jsonb, int | Type défini dans les `Contracts` du module, sérialisé en JSON source-généré |
| `edited_payload?` | jsonb | Version modifiée par l'utilisateur avant validation |
| `warnings` | jsonb | Liste `{code, severity, message, field?}` : confiance, écart de contrôle, erreur d'application |
| `status` | text | `pending`, `approved`, `rejected`, `superseded` |
| `fingerprint` | char(64) | Hash de `(kind, target_item_id, payload canonique)` |
| `superseded_by_id?` | uuid | |
| provenance | | `source`, `document_id?`, `job_id?`, `created_via`, `created_by_person_id?` (au nom de qui, pour le MCP), `created_at` |
| décision | | `decided_by_person_id?`, `decided_at?`, `decision_note?`, `applied_refs?` (identifiants des lignes créées : valorisations, flux…) |

Le payload d'origine est **toujours conservé** ; le payload appliqué est `edited_payload ?? payload`.

### 7.2 Visibilité et droits

- Avec `target_item_id` : **lecture** si l'on peut lire l'élément, **décision** si l'on peut l'écrire.
- Sans cible : `for_person_id` seul.
- Une proposition sur un élément privé n'apparaît donc que chez son propriétaire.

### 7.3 Cycle de vie

1. `pending` → validation : Core rejoue `Validate` du handler du module **contre l'état courant** (la cible a pu changer depuis la création), avec contrôle d'écriture concurrente (`xmin`).
2. `Apply` réussit : `approved`, `applied_refs` renseigné, **dans la même transaction**. `Apply` échoue : la proposition reste `pending` et un avertissement d'application est ajouté.
3. `rejected` : conservée, jamais rejouée.
4. Jamais d'auto-approbation.

### 7.4 Dédoublonnage

- Même `fingerprint` **en attente** : pas de nouvelle ligne, on renvoie l'existante (index unique partiel sur `fingerprint` pour `status = 'pending'`).
- Même `(kind, target_item_id, subject_key)` mais payload différent : la nouvelle **remplace** l'ancienne, qui passe à `superseded`.

### 7.5 Versions de payload et rétention

- Une proposition en attente dont la `payload_version` n'est plus gérée par le module reste consultable avec l'avertissement `payload_version_unsupported` et ne peut qu'être rejetée.
- Les propositions décidées sont conservées (audit, lien vers le document). Aucune expiration automatique ; les propositions en attente apparaissent dans le résumé mensuel.

### 7.6 Livré en 4C1

Table `core.proposal`, handlers de module et routes `/api/v1/core/proposals` (`architecture.md` §9.5). Deux écarts assumés par rapport au §7.1 :

- **D6** : pas de document ni de job : `document_id` et `job_id` ne sont pas créés (l’extraction viendra plus tard) ; la provenance est `created_via`, `token_id?`, `created_by_person_id` et un `source_label` facultatif. `document_id` est arrivé depuis avec la tranche 3 de la #161 (§5.1) ; `job_id` reste à venir.
- **D7** : pas de `proposal_id` sur la valorisation créée par une approbation : `applied_refs` de la proposition porte son identifiant, ce qui suffit à la retrouver.

## 8. Taux de change

### 8.1 Table `fx_rate`

| Champ | Type | Remarques |
|---|---|---|
| `on_date` | date | |
| `quote` | char(3) | Devise cotée |
| `rate` | numeric(19,10) | Cours pour **1 USD** (devise pivot, voir §8.2) |
| `source` | text | `subtracker_data`, `ecb` ou `manual` |
| `fetched_at` | timestamptz | |

Clé primaire `(on_date, quote)`, contrainte `rate > 0`, contrainte `quote <> 'USD'` (l'USD vaut 1 par définition et n'est jamais stocké).

**Priorité d'écriture** `manual` > `subtracker_data` > `ecb`. Une écriture ne remplace jamais une ligne d'une source de priorité supérieure ; une ligne de même priorité est remplacée (un fichier amont corrigé l'emporte). La BCE ne fait donc que combler les trous, SubTracker-Data remplace la BCE là où les deux existent, et rien n'écrase une saisie manuelle.

### 8.2 Règles

- **Devise pivot USD.** Un cours A → B se calcule par `rate_B / rate_A` ; `USD` vaut 1. Fonctionne quelle que soit la devise de référence du foyer.
- **Normalisation à l'écriture** : toute source dont la base n'est pas l'USD est **rebasée** avant stockage (`rate_q / rate_USD`, `rate_USD = 1 / rate_pivot_USD` pour la devise pivot de la source). La lecture reste identique quelle que soit la source.
- **Conversion à la date d** : dernier cours avec `on_date ≤ d` (week-ends, jours fériés), avec un **écart maximal de 7 jours par défaut** (configurable) : la BCE a au plus quatre jours ouvrés entre deux cours, jours fériés compris. Au-delà de l'écart : erreur `fx_rate_missing`, jamais de valeur inventée.
- **Plausibilité** : un cours collecté dont la variation relative par rapport au dernier cours stocké de la même devise dépasse un seuil (**25 % par jour par défaut**, configurable) est refusé et consigné dans `fx_sync_run.rejected` (§8.5) ; le refus ne porte que sur cette devise, le reste du fichier est écrit. La comparaison se fait avec le cours stocké le plus proche antérieur, y compris à travers un écart de plusieurs jours. Les saisies manuelles ne sont pas vérifiées (l'auteur en est l'autorité) ; l'historique BCE est écrit sans vérification (c'est la référence, et ses premiers cours n'ont rien à comparer), mais un cours SubTracker est vérifié contre un cours BCE.
- **Toutes les devises fournies sont stockées** (environ 170 lignes par jour pour SubTracker-Data, soit quelque 60 000 par an) : utiliser une nouvelle devise ne demande aucun rattrapage.
- **Arrondi** : pleine précision en interne, arrondi à l'affichage uniquement.
- Les taux sont **indicatifs** (affichage et agrégation du foyer), pas des taux comptables ou bancaires.

### 8.3 Source : SubTracker-Data

Source principale : dépôt public `Mistribe/SubTracker-Data` (licence MIT, attribution à conserver en cas de redistribution).

| Constat (documentation, fichiers lus et vérification réelle du 2026-09-24, `spikes.md` §9quater) | Détail |
|---|---|
| Fichiers | `exchange/{AAAA}/{MM}/{JJ}.json`, un fichier par jour |
| Format | `{ "from": "USD", "to": { "EUR": 0.870604, "GBP": 0.746547, … } }` : base **USD**, valeurs numériques à 5-6 décimales, environ 168 à 173 devises selon la date, **aucune date dans le fichier** (la date vient du chemin). Format stable et confirmé sur cinq jours réels, aucun rejet |
| `exchange/list.json` | Table `code → nom` de 155 devises, utilisable pour les listes de choix et pour écarter les codes inconnus |
| Mise à jour amont | Planifiée à 01:00 UTC, mais GitHub Actions la lance avec plusieurs heures de retard, et ce retard varie : vers 05:30 UTC en septembre 2026, jusqu'à **07:10 UTC** début novembre 2026 |
| Fichiers du week-end | **Présents**, avec un contenu comparable aux jours ouvrés (vérifié les 2026-09-19 et 2026-09-20) : la règle « dernier cours connu » couvre malgré tout les jours sans fichier (jour férié amont, panne) |
| Devise absente un jour donné | Non observée sur la fenêtre vérifiée (5 jours consécutifs, 173 codes identiques) ; seule une **croissance** de couverture a été vue (171 codes le 2025-07-30, 173 en 2026-09), jamais une régression |
| Couverture | Dossiers présents depuis **le 2025-07-30** (premier fichier confirmé) |
| Fournisseur amont | Non identifiable : les URL sont des secrets du dépôt |

**Collecte côté application**

- Job Quartz quotidien (`fx-daily`), planifié à **09:00 UTC** (configurable), avec une marge sur l'heure amont observée. Il lit chaque jour manquant depuis le dernier cours SubTracker jusqu'à aujourd'hui, un fichier à la fois, en séquence ; un jour manqué est rattrapé le lendemain matin. Il purge aussi les lignes de `fx_sync_run` de plus de 400 jours.
- **Rattrapage** (`fx-backfill`) au démarrage tant qu'aucun rattrapage n'a réussi (`fx_sync_run`), et à la demande : la BCE d'abord (elle ne fait que combler les trous, §8.1), puis SubTracker depuis le 2025-07-30 jusqu'à aujourd'hui, les jours déjà couverts par SubTracker étant sautés. Dans cet ordre, chaque cours SubTracker a un cours BCE contre lequel être vérifié (§8.2).
- URL de base **configurable** : le dépôt (fichiers bruts) par défaut, mais aussi un miroir ou un dossier local (ce dernier hors 1C2a, voir `superpowers/specs/2026-09-23-fx-rates-design.md` §2). Sans accès sortant, saisie manuelle possible (`source = manual`).
- L'accès sortant ne révèle aucune donnée financière : les requêtes portent sur des dates, pas sur le contenu du foyer.

### 8.4 Source : BCE (historique)

Historique de référence : `eurofxref-hist.zip` (`www.ecb.europa.eu`), une archive zip contenant un CSV `Date,USD,JPY,…`, une ligne par jour ouvré, `N/A` pour une valeur absente, base **EUR**. Vérifié en réel le 2026-09-24 (`spikes.md` §9quater) : 639 760 octets, **220 919** lignes `(date, devise, cours)`, **7099** dates distinctes du **1999-01-04** au jour ouvré le plus récent, 27 devises déjà cotées à la première date dont neuf aujourd'hui retirées de la zone euro (`CYP`, `EEK`, `LTL`, `LVL`, `MTL`, `ROL`, `SIT`, `SKK`, `TRL`) dont les colonnes et les anciens cours restent dans la série complète (un paiement ancien peut en avoir besoin). Bornée contre une bombe de décompression : au plus 5 Mo téléchargés, une seule entrée, 50 Mo décompressés ; les valeurs `N/A` sont ignorées sans erreur, jamais une erreur de format.

Un cours qui ne tient pas dans `numeric(19,10)` une fois rebasé (plus de 9 chiffres avant la virgule) est refusé comme un cours implausible (§8.2), jamais tronqué.

### 8.5 Journal des collectes (`fx_sync_run`)

Une ligne par exécution (planifiée ou à la demande) : `id`, `kind` (`daily`, `backfill`), `started_at`, `finished_at`, `status` (`running`, `succeeded`, `failed`), `files_read`, `rows_written`, `failure` (code et message, nul en cas de succès), `rejected` (jsonb : `[{on_date, quote, rate, previous, reason}]`). Rend concret le « refusé **et signalé** » de `security/application.md` §8 ; le futur écran d'administration n'a qu'à l'afficher. Les lignes de plus de 400 jours sont purgées par le job quotidien, sauf le dernier rattrapage réussi, dont dépend le démarrage (§8.3). Une exécution interrompue par l'arrêt du service est close en `failed` avec `core.fx_run_interrupted`.

### 8.6 Indice des prix (`cpi`, `cpi_run`)

Série mensuelle de l'IPC de l'INSEE (BDM 011814630, base 2025), collectée par le job `cpi-weekly` (`superpowers/specs/2026-11-06-inflation-design.md`). `core.cpi` : un mois (`2026-09`), la valeur de l'indice, la source (`insee` ou `manual`) ; une valeur de l'INSEE remplace toujours celle du même mois, une valeur manuelle est refusée sur un mois que l'INSEE a fourni (`core.cpi_from_insee`). `core.cpi_run` : le journal des collectes, gardé 400 jours. Lecture par le contrat `IInflation`.

## 9. Contrats exposés (`Core.Contracts`)

Toutes les opérations passent par `IAccessPolicy`. Côté application, les écritures sont des commandes Synapse ; audit et provenance sont ajoutés par les comportements de pipeline (`architecture.md` §5.4).

| Contrat | Opérations |
|---|---|
| `IAccessPolicy` | `Readers(item)`, `Writers(item)`, `CanRead`, `CanWrite`, `CanCreate(person, owners)`, `CanChangeOwners`, `CanChangeVisibility` ; accès en lot pour les listes (`access.can_write`) |
| `IVisibleItems` | `For(person)` : ensemble des éléments lisibles, filtre unique réutilisé par tous les modules ; variantes « foyer » (partagés seulement) et « moi » |
| `IItems` | `Create`, `Rename` (met à jour `display_name`), `SetOwners`, `SetVisibility`, `Archive` |
| `IValuations` | `Record` (crée ou remplace la valeur d'une date), `Void`, `Latest(item, on)`, `Series(item, range)` |
| `IFlows` | `Record`, `Supersede`, `Void`, `List(item, range, kinds?)`, `FindManyAsync(ids)` (flux actifs par id, sans contrôle d'accès : l'appelant décide qui voit), `CounterpartsAsync(item)` (flux actifs d'autres éléments partageant un `external_ref` avec un flux actif de l'élément, même règle : l'autre côté d'un virement, #23), `Sum(item, kinds, range)` (somme déterministe pour les modules) |
| `IDocuments` | Retiré de Core : `IDocumentTargets` et `IDocumentLinks` sont dans `Documents.Contracts` (`documents-data-model.md` §5) |
| `IProposalStore` | Livré en 4C1 : `Submit`, `Find`, `List`, `Decide`, `AddWarning`. Pas d’`Edit` séparé avant 4C2 : la modification se fait à l’approbation (`editedPayload`) |
| `IProposalHandler` | Non générique, implémenté par chaque module : `Validate`, `Preview`, `Apply` |
| `IFx` | `Convert(money, to, on)` |

## 10. Contraintes et index (récapitulatif)

```sql
-- une valorisation courante par élément et par date
CREATE UNIQUE INDEX ux_valuation_current
    ON core.valuation (item_id, as_of) WHERE retired_at IS NULL;

-- au plus un flux d'ouverture courant par élément
CREATE UNIQUE INDEX ux_flow_opening_current
    ON core.flow (item_id) WHERE kind = 'opening' AND retired_at IS NULL;

-- imports rejouables sans doublon
CREATE UNIQUE INDEX ux_flow_external_ref_current
    ON core.flow (item_id, external_ref)
    WHERE external_ref IS NOT NULL AND retired_at IS NULL;

-- une proposition identique en attente à la fois
CREATE UNIQUE INDEX ux_proposal_pending_fingerprint
    ON core.proposal (fingerprint) WHERE status = 'pending';

ALTER TABLE core.flow      ADD CONSTRAINT ck_flow_amount_positive CHECK (amount > 0);
ALTER TABLE core.ownership ADD CONSTRAINT ck_share_range CHECK (share_bp BETWEEN 1 AND 10000);
```

Les invariants qui portent sur plusieurs lignes (somme des parts = 10 000, privé ⇒ un seul propriétaire, devise de l'élément) sont vérifiés par l'application dans la transaction d'écriture et couverts par des tests d'intégration.

## 11. Exemples

### 11.1 PEA : gain calculé par Investments

Élément `investments/envelope` « PEA », un propriétaire à 100 %, partagé, devise `EUR`. Chiffres fictifs, ceux de `Project.md` §7.2.

| Date | Fait | Montant |
|---|---|---|
| 2024-12-31 | `flow` `opening` | 20 000 € (versements antérieurs) |
| 2024-12-31 | `valuation` | 21 000 € |
| 2025-06-15 | `flow` `contribution` | 2 000 € |
| 2025-12-31 | `valuation` | 25 000 € |

Au 2025-12-31 : versements nets = 20 000 + 2 000 − 0 = 22 000 €. Gain = 25 000 − 22 000 = **+3 000 € (+13,6 %)**, avant fiscalité. Ce calcul est fait par Investments ; Core fournit `IValuations.Latest` et `IFlows.Sum`.

### 11.2 Correction d'une valorisation

La valeur du 2025-12-31 avait été saisie 25 000 €, le relevé indique 24 800 €.

1. Nouvelle ligne `valuation` : `as_of 2025-12-31`, `amount 24 800`, `supersedes_id` → l'ancienne, `source document`, `document_id` renseigné.
2. Ancienne ligne : `retired_at` = maintenant.

Les deux lignes restent en base ; seule la seconde est courante.

### 11.3 Proposition issue d'un relevé

```json
{
  "module": "investments",
  "kind": "investments.record_valuation",
  "target_item_id": "…pea…",
  "subject_key": "2025-12-31",
  "payload_version": 1,
  "payload": {
    "asOf": "2025-12-31",
    "value": { "amount": "24800.00", "currency": "EUR" }
  },
  "warnings": [
    { "code": "low_confidence", "severity": "info",
      "message": "Montant lu sur une page scannée", "field": "value" }
  ],
  "source": "document",
  "document_id": "…releve-2025-12…",
  "created_via": "job",
  "status": "pending"
}
```

À la validation par un utilisateur autorisé à écrire sur le PEA : `Validate` rejoué, `IValuations.Record` appelé par le handler du module Investments, proposition `approved` avec `applied_refs` pointant sur la nouvelle valorisation.

## 12. Limites connues

- Répartition des propriétaires **non datée** (§4.2).
- Une seule devise par élément pour valorisations et flux (§5.5) : un compte en deux devises se modélise en deux éléments.
- Les gains ne sont pas convertis : seuls les totaux le sont.
- Pas de valorisation future ni de prévision dans Core.
- « Aujourd'hui » : seules les règles de 2A (`IHouseholdCalendar`) prennent le jour du foyer ; les calculs de l'étape 1 prennent encore la date UTC, si bien qu'un import fait tard le soir peut voir la veille.

## 13. Questions ouvertes

- ~~Seuil de péremption des valeurs (45 jours proposé).~~ Tranché en 2A : la péremption appartient à la cadence de chaque module (`investments-data-model.md` §7) ; le Core renvoie seulement l'`as_of`. ~~Écart maximal de cours (`fx_rate_missing`).~~ Tranché en 1C2a : 7 jours par défaut, configurable (§8.2).
- Liste blanche des types de fichiers et taille maximale.
- ~~**Historique des taux avant juillet 2025** : les abonnements remontent à 2014 et les placements à 12-24 mois, donc des paiements ou valorisations en devise étrangère peuvent tomber avant la première date disponible (`fx_rate_missing`). Piste : rattrapage plus long côté `SubTracker-Data` si sa source historique le permet ; sinon saisie manuelle.~~ Tranché en 1C2a : la BCE (`eurofxref-hist.zip`) couvre les jours ouvrés depuis 1999, avant SubTracker-Data (§8.4) ; au-delà (paiement le week-end ou jour férié sans cours BCE ni SubTracker), `fx_rate_missing` reste possible et la saisie manuelle couvre le reste.
- ~~Format et couverture réels (week-ends, devises absentes) à confirmer à l'implémentation (§8.3).~~ Vérifié en réel le 2026-09-24 (1C2a, `spikes.md` §9quater) : format stable, fichiers du week-end présents, aucune devise disparue d'un jour sur l'autre dans la fenêtre vérifiée.
- Propriétaires d'un élément **non-personne** (société, association) : hors périmètre. Les enfants du foyer sont couverts par les `person` de type `dependent`.
- Les éléments d'un `dependent` comptent dans le total du foyer, ventilés par personne (décidé le 2026-10-08, 2D1) ; pas d'option d'exclusion pour l'instant.
