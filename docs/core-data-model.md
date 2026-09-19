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
                          │  └──< document_item >── document
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
| `reference_currency` | char(3) | Devise des totaux (défaut `EUR`) |
| `timezone` | text | Fuseau IANA (défaut `Europe/Paris`) : jour courant, tâches planifiées |
| `locale` | text | Langue et formats d'affichage |

**`person`**

| Champ | Type | Remarques |
|---|---|---|
| `id` | uuid | |
| `kind` | text | `adult` (peut se connecter) ou `dependent` (enfant du foyer, sans connexion) |
| `oidc_subject` | text?, unique | `sub` fourni par Authelia. **Obligatoire pour un `adult`, vide pour un `dependent`** |
| `display_name` | text | Repris des claims OIDC pour un `adult`, saisi pour un `dependent` |
| `email` | text? | Repris des claims OIDC ; vide pour un `dependent` |
| `session_epoch` | int | Incrémenté par « déconnecter partout » : les cookies d'un ancien epoch sont refusés (`identity-and-access/authentication.md` §3) |
| `preferences` | jsonb | Préférences d'interface de l'adulte : `theme`, `mode`, `chart_patterns` (`frontend/design-system.md` §7.6) et `ai_cloud_default` (`ask` ou `never`, `ingestion/llm-providers.md` §6). Lues par `GET /auth/session`, modifiées par `PATCH /api/v1/core/me/preferences` (fusion des clés fournies). Vide pour un `dependent` |
| `created_at` | timestamptz | |

**`item`** : registre central

| Champ | Type | Remarques |
|---|---|---|
| `id` | uuid | |
| `module`, `kind` | text | ex. `subscriptions` / `subscription`, `investments` / `envelope`, `realestate` / `property`, `realestate` / `loan` |
| `display_name` | text | **Dénormalisé** : tenu à jour par le module via un contrat, pour que listes et tableau de bord n'interrogent pas les modules |
| `visibility` | text | `shared` (défaut) ou `private` |
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
- `visibility = private` ⇒ **exactement un** propriétaire.
- Ajouter un propriétaire à un élément privé le passe en `shared`.
- Pas de suppression physique d'un `item` : on l'archive.
- `module` + `kind` appartiennent au module : Core n'interprète pas ces valeurs.
- Un `adult` a un `oidc_subject`, un `dependent` n'en a pas. Il y a au plus **deux `adult`**.
- Un `dependent` **n'agit jamais** : il ne se connecte pas et ne crée rien. Il peut être propriétaire d'un élément ou utilisateur d'un service.
- Un élément dont les seuls propriétaires sont des `dependent` ne peut pas être `private` (les deux adultes seraient exclus).

**Décision : parts actuelles partout.** Il n'existe qu'une répartition par élément, appliquée à tout l'historique. Passer un bien de 100 % à 50/50 en 2025 réécrit donc aussi la répartition affichée pour 2023. Les changements de propriétaires sont rares dans un couple et l'audit (`architecture.md` §6) en garde la trace. Si le besoin de répartitions datées apparaît, il faudra ajouter une date d'effet à `ownership` et une jointure temporelle dans les agrégats du tableau de bord.

### 4.3 Matrice de droits

Appliquée par `IAccessPolicy` (règles de `Project.md` §5.1) à l'API, au tableau de bord et au MCP. Le détail (acteur, portées, pipeline, fonctions `Readers` / `Writers`, garde de sortie) est dans `identity-and-access/authorization.md`.

| Élément | Lecture | Écriture | Total du foyer |
|---|---|---|---|
| `shared`, 1 propriétaire | les deux | le propriétaire seul | oui |
| `shared`, 2 propriétaires | les deux | les deux | oui, au prorata des parts |
| `private` (1 propriétaire adulte) | le propriétaire | le propriétaire | non ; compte seulement dans son total « moi » |
| au moins un propriétaire `dependent` | les deux adultes | les deux adultes (ils gèrent les biens de l'enfant) | oui, ventilé par personne |

- **Changer la visibilité** : le propriétaire seul. `private` → `shared` est possible ; `shared` → `private` est réservé au propriétaire unique.
- **Répartition par personne** : somme des `share_bp` × valeur de l'élément.
- **Total « moi »** : éléments partagés au prorata de mes parts, plus mes éléments privés.
- **Personne agissante** : les contrats d'accès (`IAccessPolicy`, `IVisibleItems`) prennent toujours un `adult`. La répartition par personne (« moi », « ma femme », un enfant) couvre tous les `person`, y compris les `dependent`.

### 4.4 Amorçage des deux utilisateurs

- Authelia est la source des identités. À la première connexion OIDC, Core crée la `person` de type `adult` depuis les claims (`sub`, nom, email).
- Après **deux `adult`**, toute nouvelle identité OIDC est refusée (application prévue pour deux utilisateurs). Verrou levable par configuration.
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

## 5. Valorisations et flux

Core **stocke** des faits datés ; il ne calcule aucun gain. Investments définit le gain, RealEstate le coût réel du bien.

### 5.1 Provenance

Colonnes communes à `valuation` et `flow` :

| Colonne | Valeurs / remarques |
|---|---|
| `source` | `manual`, `bank_api`, `document`, `import` |
| `created_via` | `ui`, `api`, `mcp`, `job`, `import` |
| `created_by_person_id` | La personne au nom de qui la donnée est saisie. Pour la validation d'une proposition : celui qui valide |
| `document_id?`, `proposal_id?` | Traçabilité jusqu'à la source |
| `created_at` | timestamptz UTC |

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

Aucune ligne de `valuation` ni de `flow` n'est modifiée ni supprimée physiquement.

- **Correction** : une nouvelle ligne est créée avec `supersedes_id` vers l'ancienne, et l'ancienne reçoit `retired_at`, **dans la même transaction**.
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

### 6.1 Tables

**`document`**

| Champ | Type | Remarques |
|---|---|---|
| `id` | uuid | |
| `sha256` | char(64) | Adresse du contenu |
| `size_bytes`, `media_type`, `original_filename` | | Types acceptés : liste blanche (PDF, images, `.eml`), taille maximale configurable |
| `uploaded_by_person_id`, `uploaded_at` | | |
| `archived_at?` | timestamptz | Pas de suppression physique |

**`document_item(document_id, item_id)`** : un document peut concerner plusieurs éléments (un relevé, plusieurs enveloppes).

### 6.2 Règles

- **Une ligne par dépôt, un fichier par contenu** : si les deux utilisateurs déposent le même PDF, il y a deux lignes `document` mais un seul fichier `/data/documents/{sha[0:2]}/{sha256}`. Le fichier n'est purgé que par action explicite, et seulement s'il n'est plus référencé.
- **Visibilité** : lisible par celui qui l'a déposé **et** par quiconque peut lire un élément lié. Non lié : déposant seul. Un document rattaché à un élément privé ne fuit donc jamais.
- **Accès au contenu** : par l'API via `IAccessPolicy` ; par les workers via le flux à jeton court (`architecture.md` §9.3).

## 7. Propositions (file de validation)

### 7.1 Table `proposal`

| Champ | Type | Remarques |
|---|---|---|
| `id` | uuid | |
| `module`, `kind` | text | ex. `investments.record_valuation`, `realestate.import_amortization`, `subscriptions.observed_charge` |
| `target_item_id?` | uuid | Élément visé |
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

## 8. Taux de change

### 8.1 Table `fx_rate`

| Champ | Type | Remarques |
|---|---|---|
| `on_date` | date | |
| `quote` | char(3) | Devise cotée |
| `rate` | numeric(19,10) | Cours pour **1 USD** (devise pivot, voir §8.2) |
| `source` | text | `subtracker_data` ou `manual` |
| `fetched_at` | timestamptz | |

Clé primaire `(on_date, quote)`. Le rafraîchissement automatique **n'écrase jamais** une ligne `manual`.

### 8.2 Règles

- **Devise pivot USD.** Un cours A → B se calcule par `rate_B / rate_A` ; `USD` vaut 1. Fonctionne quelle que soit la devise de référence du foyer.
- **Normalisation à l'écriture** : toute source dont la base n'est pas l'USD est **rebasée** avant stockage (`rate_q / rate_USD`). La lecture reste identique quelle que soit la source.
- **Conversion à la date d** : dernier cours avec `on_date ≤ d` (week-ends, jours fériés). Au-delà d'un écart maximal configurable : erreur `fx_rate_missing`, jamais de valeur inventée.
- **Toutes les devises fournies sont stockées** (environ 170 lignes par jour, soit quelque 60 000 par an) : utiliser une nouvelle devise ne demande aucun rattrapage.
- **Arrondi** : pleine précision en interne, arrondi à l'affichage uniquement.
- Les taux sont **indicatifs** (affichage et agrégation du foyer), pas des taux comptables ou bancaires.

### 8.3 Source : SubTracker-Data

Source principale : dépôt public `Mistribe/SubTracker-Data` (licence MIT, attribution à conserver en cas de redistribution).

| Constat (documentation et fichiers lus) | Détail |
|---|---|
| Fichiers | `exchange/{AAAA}/{MM}/{JJ}.json`, un fichier par jour |
| Format | `{ "from": "USD", "to": { "EUR": 0.870604, "GBP": 0.746547, … } }` : base **USD**, valeurs numériques à 5-6 décimales, environ 168 devises, **aucune date dans le fichier** (la date vient du chemin) |
| `exchange/list.json` | Table `code → nom` de 155 devises, utilisable pour les listes de choix et pour écarter les codes inconnus |
| Mise à jour | Action GitHub quotidienne à 01:00 UTC, et rattrapage sur 60 jours en fin de mois |
| Couverture | Dossiers présents depuis **juillet 2025** (2025/07) |
| Fournisseur amont | Non identifiable : les URL sont des secrets du dépôt |

**Collecte côté application**

- Job Quartz quotidien, planifié **après** l'action amont (03:00 UTC par défaut). Il lit le fichier du jour, vérifie la forme, rebase si besoin, écrit les lignes `subtracker_data`.
- **Rattrapage** au démarrage et à la demande : télécharge les jours manquants sur la plage nécessaire, un fichier par jour, en séquence, en ignorant les jours déjà présents.
- URL de base **configurable** : le dépôt (fichiers bruts) par défaut, mais aussi un miroir ou un dossier local. Sans accès sortant, saisie manuelle possible (`source = manual`).
- L'accès sortant ne révèle aucune donnée financière : les requêtes portent sur des dates, pas sur le contenu du foyer.

**Points à vérifier à l'implémentation** : présence des fichiers le week-end (la règle « dernier cours connu » couvre les deux cas), comportement des devises absentes un jour donné, stabilité du format.

**Historique avant juillet 2025** : non couvert par les dossiers actuels. Voir §13.

## 9. Contrats exposés (`Core.Contracts`)

Toutes les opérations passent par `IAccessPolicy`. Côté application, les écritures sont des commandes Synapse ; audit et provenance sont ajoutés par les comportements de pipeline (`architecture.md` §5.4).

| Contrat | Opérations |
|---|---|
| `IAccessPolicy` | `Readers(item)`, `Writers(item)`, `CanRead`, `CanWrite`, `CanCreate(person, owners)`, `CanChangeOwners`, `CanChangeVisibility` ; accès en lot pour les listes (`access.can_write`) |
| `IVisibleItems` | `For(person)` : ensemble des éléments lisibles, filtre unique réutilisé par tous les modules ; variantes « foyer » (partagés seulement) et « moi » |
| `IItems` | `Create`, `Rename` (met à jour `display_name`), `SetOwners`, `SetVisibility`, `Archive` |
| `IValuations` | `Record` (crée ou remplace la valeur d'une date), `Void`, `Latest(item, on)`, `Series(item, range)` |
| `IFlows` | `Record`, `Supersede`, `Void`, `List(item, range, kinds?)`, `Sum(item, kinds, range)` (somme déterministe pour les modules) |
| `IDocuments` | `Register`, `Link(item)`, `Open` (flux du contenu), `Archive` |
| `IProposals` | `Submit`, `Edit`, `Approve`, `Reject`, `ListPending(person)` |
| `IProposalHandler<TPayload>` | Implémenté par chaque module : `Validate`, `Preview`, `Apply` |
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

## 13. Questions ouvertes

- Seuil de péremption des valeurs (45 jours proposé) et écart maximal de cours (`fx_rate_missing`).
- Liste blanche des types de fichiers et taille maximale.
- **Historique des taux avant juillet 2025** : les abonnements remontent à 2014 et les placements à 12-24 mois, donc des paiements ou valorisations en devise étrangère peuvent tomber avant la première date disponible (`fx_rate_missing`). Piste : rattrapage plus long côté `SubTracker-Data` si sa source historique le permet ; sinon saisie manuelle.
- Format et couverture réels (week-ends, devises absentes) à confirmer à l'implémentation (§8.3).
- Propriétaires d'un élément **non-personne** (société, association) : hors périmètre. Les enfants du foyer sont couverts par les `person` de type `dependent`.
- Les éléments d'un `dependent` comptent-ils dans le total du foyer ? Par défaut oui, ventilés par personne ; une option d'exclusion reste à décider.
