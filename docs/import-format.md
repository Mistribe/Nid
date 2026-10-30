# Format d'import et d'export de Nid

> Statut : v1 en vigueur pour l'import (1D1, 2026-09-23) et l'export (1D2, 2026-09-25) ; l'écran d'export du navigateur suit. Décrit le fichier JSON standard, ses règles de validation et le protocole d'échange avec le serveur, orchestré par le navigateur. Les sections Abonnements (1D1), Placements et Core (`valuations`, `flows`) (2B1b, 2026-09-30), Revenus (`income_sources`, `income_phases`) (2C1, 2026-10-02) et Immobilier (les huit sections de `realestate/README.md` §10 et `realestate/loans.md` §10) (3F1, 2026-10-15) sont définies ; `loan_bank_schedule` les complète en 3F2. Les objectifs d'épargne (`goals`) suivent (savings goals spec D16, 2026-10-23), puis les comptes bancaires et leurs opérations (`bank_accounts`, `bank_transactions`), en dernier (bank sync D24, 5B, 2026-10-29), complétés par les liens et les règles (`bank_links`, `bank_rules`, 5C, 2026-10-30).
> Références : `subscription-data-model.md` (sens des entités), `core-data-model.md` (personnes, propriété, provenance), `architecture.md` §11 (export) et §5.4 (Synapse).

---

## 1. Objet

Un **seul format** sert à trois usages :

- la **reprise initiale** de données existantes (par exemple l'ancien `subscriptions.json`, converti dans ce format ; conversion faite hors de ce dépôt et vérifiée par le protocole réel en 1D3, le 2026-09-23) ;
- l'**export complet** à la demande (`Project.md` §13), pour ne jamais dépendre de l'application ;
- le **transfert** ou la restauration logique des données d'un foyer vers une autre instance.

## 2. Principes

- Un fichier JSON UTF-8, **versionné** et auto-descriptif.
- **Même format à l'import et à l'export** : un export rejoué sur une instance vide reconstitue les données du foyer.
- **Le serveur est l'autorité de validation.** Le navigateur peut pré-valider pour le confort, jamais pour décider. Le serveur revalide tout avant d'écrire.
- **Validation complète avant toute écriture**, puis écriture tout-ou-rien en une seule transaction.
- **Le navigateur orchestre** l'import et l'export par une suite d'appels (§11). **Aucune tâche de fond** n'y participe : ni traitement asynchrone ni planificateur.
- **Aperçu** : l'étape de validation (§11) produit un rapport sans aucune écriture dans les données du foyer.
- **Idempotent** : réimporter le même fichier ne crée pas de doublons (§8).
- **Lisible et modifiable à la main**. Le fichier ne contient que des faits saisis : la couverture, les charges et les coûts sont **dérivés**, jamais écrits dans le fichier.
- **Strict sur le fond** : section ou champ inconnu = erreur explicite, pour ne jamais perdre de données sans le dire. **Tolérant sur la forme** : un montant écrit en nombre JSON est accepté (lu en décimal, jamais en flottant) ; l'export écrit des chaînes.

## 3. Enveloppe

```json
{
  "format": "nid.data",
  "version": 1,
  "exported_at": "2026-09-19T14:00:00Z",
  "persons": [],
  "providers": [],
  "subscriptions": [],
  "billing_phases": [],
  "purchases": []
}
```

| Clé | Type | Remarques |
|---|---|---|
| `format` | string | Toujours `nid.data` |
| `version` | int | Version du format. L'import accepte la version courante et la précédente |
| `exported_at` | timestamp UTC? | Renseigné par l'export, ignoré à l'import |
| `persons` | tableau | Voir §5.1 |
| `providers` | tableau? | Fournisseurs **personnalisés** seulement (§5.2) |
| `subscriptions`, `billing_phases`, `purchases` | tableaux | Module Abonnements (§5.3 à §5.5) |
| `envelopes` | tableau | Module Investments (`investments-data-model.md` §9) |
| `holdings`, `holding_snapshots`, `holding_movements` | tableaux | Module Investments, le détail par support (`investments-data-model.md` §9 et §16) |
| `properties`, `property_recurring_costs`, `tenancies` | tableaux | Module RealEstate, le bien (`realestate/README.md` §10) |
| `loans`, `loan_phases`, `loan_insurances`, `loan_fees`, `loan_events`, `loan_bank_schedule` | tableaux | Module RealEstate, les crédits (`realestate/loans.md` §10) ; `loan_bank_schedule` livrée en 3F2 (§12bis) |
| `income_sources`, `income_phases` | tableaux | Module Revenus (`income-data-model.md` §9) |
| `income_one_off_taxable` | tableau | Module Revenus : le net imposable d'un revenu ponctuel, `{ flow_id, taxable_amount }`, où `flow_id` est l'`id` d'une entité de `flows` (catégorie `one_off`) ; appliqué après `flows` |
| `valuations`, `flows` | tableaux | Core (§5.6) |
| `goals` | tableau | Module Objectifs (`superpowers/specs/2026-10-23-savings-goals-design.md` D16 et §2) : l'objectif, ses liens vers les enveloppes du fichier et ses relevés |
| `bank_accounts`, `bank_transactions`, `bank_links`, `bank_rules` | tableaux | Module Banque (`superpowers/specs/2026-10-29-bank-sync-design.md` D24) : les comptes bancaires, leurs opérations, ce que chaque opération paie et les règles de libellé de la personne (§5.7) |

Section **réservée**, pas encore définie : `documents`. Un fichier qui la contient est refusé. Une section d'un module désactivé est refusée elle aussi.

## 4. Conventions

- **Casse** : `snake_case` dans le fichier, comme `subscription-data-model.md`. Les DTO de l'API sont indépendants (`architecture.md` §7).
- **Dates** : `AAAA-MM-JJ`. **Intervalles semi-ouverts** : `ends_on` est le premier jour **non** couvert.
- **Montants** : `{ "amount": "12.99", "currency": "EUR" }`, devise ISO 4217 en majuscules. Négatif seulement sur les achats `adjustment` et `refund`.
- **Durées** : `{ "count": 1, "unit": "month" }`, unités `day`, `week`, `month`, `year`.
- **Identifiants** : `id` est une **clé stable du fichier** (`^[a-z0-9][a-z0-9-]*$`), unique dans sa section. Ce n'est pas l'UUID interne de l'application. Les références (`subscription_id`, `person`…) désignent ces clés.
- **`note`** : texte libre optionnel sur les abonnements, phases et achats. Conservé, sans effet métier (par exemple pour garder l'identifiant d'origine).

## 5. Sections

### 5.1 `persons`

| Champ | Type | Remarques |
|---|---|---|
| `ref` | string | Clé locale au fichier |
| `kind` | string | `adult` ou `dependent` |
| `display_name` | string | |
| `email` | string? | Sert à retrouver le compte d'un `adult` (§6) |

### 5.2 `providers`

Uniquement les fournisseurs absents du catalogue. Un fournisseur du catalogue n'est pas listé : `provider_key` suffit.

| Champ | Type | Remarques |
|---|---|---|
| `key` | string | Clé **locale au fichier** (`^[a-z0-9][a-z0-9-]*$`), unique dans la section ; ce n'est pas la clé stockée. Mappée à une clé générée (`<slug>-<6 [a-z0-9] aléatoires>`) au `commit`, jamais choisie |
| `name` | string | |
| `website`, `pricing_page` | url? | `http` ou `https` |
| `label_keys` | `[string]` | Au moins une |

Résolution de chaque entrée à la validation puis au `commit` (dans cet ordre) :

1. l'entrée a déjà été importée sous cette même référence (`import_ref`) par **cet importeur** : contenu identique → `unchanged`, sinon → `conflict` (rien n'est écrasé) ;
2. sinon, une entrée déjà importée sous cette même référence, de contenu identique, **visible de l'importeur** : le fournisseur existant est réutilisé, aucune ligne n'est créée ;
3. sinon, un nouveau fournisseur `custom` est créé au `commit`, avec une clé générée.

Un fournisseur ainsi créé garde `import_ref`/`import_hash` comme toute autre entité importée (§9).

### 5.3 `subscriptions`

| Champ | Type | Remarques |
|---|---|---|
| `id` | string | |
| `provider_key` | string? | Désigne une entrée de `providers` du même fichier (résolue en §5.2 : c'est **le premier candidat**), sinon un fournisseur existant **visible de l'importeur et non archivé** ; sinon `import.unresolved_reference` au chemin `subscriptions[i].provider_key`. Une entrée `providers` du fichier de même orthographe **prime toujours** sur une clé existante de même nom |
| `name` | string | |
| `ownership` | `[{ person, share_bp }]` | Références vers `persons`. Somme des `share_bp` = 10 000 |
| `visibility` | string | `shared` (défaut) ou `private` (un seul propriétaire adulte) |
| `service_users` | `[person]` | Références vers `persons`, `dependent` possibles |
| `auto_renew` | bool | |
| `cancelled_at` | date? | |
| `anniversary_on` | date? | Conditions du contrat : date d'anniversaire, à partir de laquelle la résiliation compte son préavis |
| `notice` | durée? | Conditions du contrat : préavis de résiliation. Exige `anniversary_on` ou `commitment_ends_on`, sinon `subscriptions.notice_without_date` au chemin `subscriptions[i].notice` |
| `commitment_ends_on` | date? | Conditions du contrat : fin de l'engagement |
| `cancellable_from` | date? | Conditions du contrat : première date à laquelle la résiliation est permise |
| `note` | string? | |

Les quatre membres de conditions sont facultatifs : un ancien fichier, qui n'en nomme aucun, reste valide et garde son empreinte. L'export ne les écrit que s'ils sont renseignés. Avec `replace`, un abonnement déjà importé prend les conditions du fichier, et les perd si le fichier n'en nomme aucune. Une date mal formée est refusée à son propre chemin (`subscriptions[i].anniversary_on`…).

### 5.4 `billing_phases`

| Champ | Type | Remarques |
|---|---|---|
| `id`, `subscription_id` | string | |
| `starts_on`, `ends_on?` | date | `ends_on` nul = phase en cours |
| `price` | montant | |
| `every`, `covers?` | durée | `covers` vaut `every` par défaut |
| `max_cycles?` | int | |
| `payer` | `{ type, person? }` | `type` : `person` ou `household` ; `person` est une référence, obligatoire si `type = person` |
| `note` | string? | |

### 5.5 `purchases`

| Champ | Type | Remarques |
|---|---|---|
| `id` | string | |
| `date` | date | |
| `label` | string | |
| `kind` | string | `prepaid_term`, `bundle`, `gift`, `adjustment`, `refund` |
| `price` | montant | `0` pour un cadeau reçu |
| `payer` | `{ type, person? }` | `type` : `person`, `household` ou `external` |
| `allocated_amount` | montant décimal? | `bundle` uniquement ; entre 0 et `price` |
| `grants` | `[{ subscription_id, duration, source, starts_on? }]` | `source` : `paid`, `bonus`, `included`, `trial`, `gift` ; sans `starts_on`, la période s'**empile** après la couverture en cours |
| `note` | string? | |

Le sens de chaque entité, l'algorithme de couverture et le calcul des coûts sont ceux de `subscription-data-model.md`.

### 5.6 `valuations` et `flows` (Core)

Sections communes à tous les modules qui suivent une valeur (`envelopes`, `properties`). Le champ `item` nomme exactement une entité d'une section qui crée des éléments (`envelopes`, `income_sources`, `properties`, `loans`) ; une référence vers une autre section, ou vers rien, est une erreur.

**`valuations`**

| Champ | Type | Remarques |
|---|---|---|
| `id` | string | |
| `item` | string | Référence vers une entité de fichier |
| `as_of` | date | Non future |
| `amount` | montant | Peut être négatif ; devise = celle de l'élément |
| `note` | string? | |

**`flows`**

| Champ | Type | Remarques |
|---|---|---|
| `id` | string | |
| `item` | string | Référence vers une entité de fichier |
| `occurred_on` | date | |
| `kind` | string | `contribution`, `withdrawal`, `opening`, `income`, `expense` |
| `amount` | montant | > 0 ; le sens vient du `kind` |
| `category` | string? | Convention du module (`investments-data-model.md` §4, `realestate/README.md` §4) ; imposée pour un bien (§12bis, 3F1) |
| `external_ref` | string? | Identifiant externe ; rend un réimport sans doublon |
| `note` | string? | |

Règles : devise identique à celle de l'élément ; au plus une valorisation courante par élément et par date ; au plus un flux `opening` courant par élément (`core-data-model.md` §5). La provenance est `source = import`.

- **Seuls les faits courants sont exportés** : un fait remplacé ou annulé n'apparaît pas dans le fichier.
- **Correction par `replace`** : avec `replace`, un fait dont l'`id` est déjà connu mais dont le contenu diffère corrige l'ancien par `supersedes_id` (l'ancien n'est plus courant, il reste dans l'historique).
- **Collision avec un fait courant** : un fait nouveau du fichier qui tombe sur la même date (valorisation), sur un second `opening` ou sur le même `external_ref` (flux) qu'un fait courant du même élément que le fichier ne nomme pas (saisi à la main ou importé par un autre fichier) est un `conflict` ; il ne remplace jamais rien. Avec `replace`, une correction (fait d'`id` connu) ne corrige que son propre fait.
- **Clôture** : la table `envelopes` porte `closed_on` (`investments-data-model.md` §9) ; une enveloppe clôturée est exportée et réimportée clôturée.

### 5.7 `bank_accounts`, `bank_transactions`, `bank_links` et `bank_rules` (Banque)

Les comptes que la personne lit et leurs opérations (bank sync D24). **Jamais** de connexion, de session, de source ni d'historique de soldes.

**`bank_accounts`**

| Champ | Type | Remarques |
|---|---|---|
| `ref` | string | Clé du fichier (§4) ; l'export écrit `acc-<uuid>` |
| `name` | string | |
| `iban_last4` | string? | Les quatre derniers caractères de l'IBAN, jamais plus |
| `kind` | string | `checking`, `savings`, `card`, `other` |
| `currency` | string | ISO 4217 |
| `envelope_ref` | string? | `id` d'une entité `envelopes` du même fichier |
| `card_of_ref` | string? | `ref` du compte courant du même fichier auquel la carte est rattachée |
| `auto_apply_balance` | bool | `false` par défaut |
| `ignored` | bool | `false` par défaut |
| `owner_ref` | string | `ref` d'une personne : qui possédait le compte là où il a été exporté |

**`bank_transactions`**

| Champ | Type | Remarques |
|---|---|---|
| `account_ref` | string | `ref` d'une entité `bank_accounts` du même fichier |
| `provider_ref` | string | Identifiant de la source (ou l'empreinte, quand elle n'en a pas) |
| `fingerprint` | string | Empreinte de l'opération ; **recalculée** à l'import |
| `booked_on` | date | |
| `value_on` | date? | |
| `amount` | montant décimal | Signé (débit négatif), au plus 4 décimales ; devise = celle du compte (`banking.currency_mismatch`) |
| `currency` | string | |
| `label` | string | Libellé brut |
| `counterparty`, `remittance` | string? | |
| `fetched_via` | string | `api` ou `file` |

Règles :

- **Compte retrouvé** : `ref` désigne un compte existant s'il vaut `acc-<uuid>` d'un compte de cette instance, sinon s'il a déjà été importé sous cette `ref` : par n'importe qui pour une `ref` écrite par un export (`acc-<uuid>`, si bien que deux adultes qui déménagent chacun leur export retrouvent le même compte commun), par cette personne seulement pour une `ref` écrite à la main. La personne doit pouvoir le modifier, sinon `banking.file_account_mismatch` sur `ref`, sans le nommer (comme un relevé) ; il est laissé tel quel (`unchanged`) et reçoit les opérations du fichier.
- **Compte nouveau** : créé sous la connexion `file` de **la personne qui importe** (une par personne, sans session), qui en devient propriétaire quel que soit `owner_ref` (qui doit toutefois désigner une personne du fichier). Sa clé de source est l'empreinte de sa `ref`, jamais un numéro de banque. Son enveloppe suit les règles de 5A : une enveloppe que la personne peut modifier (`import.not_permitted`), `checking`, `savings` ou `other` (`banking.envelope_kind`), dans la devise du compte (`banking.currency_mismatch`), un compte par enveloppe (`banking.envelope_taken`) ; une carte ne prend pas d'enveloppe et se rattache à un compte courant (`banking.card_parent`).
**`bank_links`** (5C) : ce qu'une opération paie

| Champ | Type | Remarques |
|---|---|---|
| `account_ref`, `provider_ref` | string | L'opération, par le `ref` de son compte (`bank_accounts` du même fichier) et sa `provider_ref` |
| `outcome` | string | `as_expected`, `price_differs`, `income`, `contribution`, `internal_transfer`, `card_settlement` |
| `target_ref` | string? | `id` d'un abonnement, d'un bien ou d'un prêt du même fichier ; obligatoire sauf pour `internal_transfer` et `card_settlement`, où il est absent |
| `target_key` | string? | La clé de l'échéance payée (1 à 200 caractères) |
| `counterpart_ref` | string? | `ref` du compte en face, pour `internal_transfer` et `card_settlement` seulement |
| `expected_amount`, `expected_currency` | montant décimal?, string? | Le montant attendu au moment du lien, avec sa devise |
| `decided_at` | date et heure | `YYYY-MM-DDThh:mm:ssZ` |

**`bank_rules`** (5C) : les règles de libellé de la personne

| Champ | Type | Remarques |
|---|---|---|
| `account_ref` | string | `ref` d'une entité `bank_accounts` du même fichier |
| `label_stem` | string | Le radical de libellé que la règle reconnaît |
| `target_ref` | string | `id` d'un abonnement, d'un bien ou d'un prêt du même fichier |
| `created_at` | date et heure | |

- **Liens** : un lien est rattaché à l'opération trouvée par `(account_ref, provider_ref)`, décidé par la personne qui importe (`person:<elle>`) ; son état passe à `linked`, ou `internal` pour un virement ou un règlement de carte. Un lien dont l'opération est absente ou déjà liée, ou dont l'élément n'est pas modifiable par la personne, est **ignoré** et compté `unchanged` (pas d'erreur) ; un `target_ref` ou un `ref` inconnus du fichier sont `import.unresolved_reference`.
- **Cible** : un `target_ref` doit désigner **une seule** entité parmi `subscriptions`, `properties` et `loans` (les `id` ne sont uniques que dans leur section) ; deux entités de même `id` donnent `import.unresolved_reference`. Une clé `flow:<id>` (loyer déjà enregistré) désigne un flux de l'instance d'origine : elle n'est pas reprise à l'import, le lien est conservé sans clé.
- **Règles** : elles appartiennent à la personne qui importe ; une règle dont l'élément n'est pas modifiable par elle est ignorée (`unchanged`), une règle identique déjà là aussi.
- **Opérations** : dédoublonnées comme celles d'un relevé, par `provider_ref` ou empreinte ; réimporter le même fichier n'ajoute rien (`unchanged`). Le rapport compte `created` et `unchanged`, jamais `conflict` ni `replaced` : une opération n'est jamais réécrite.
- **Clés non stables** (choix assumé) : un compte importé reçoit un nouvel `acc-<uuid>` dans l'instance qui l'importe ; un nouvel export de cette instance donne donc d'autres `ref` que le fichier d'origine. Le fichier d'origine, réimporté, retrouve toujours ses comptes par leur `ref`.
- **Pas d'empreinte d'IBAN** : le fichier ne porte que les quatre derniers caractères, qui ne suffisent pas à reconnaître un compte. Un compte importé n'a donc pas d'empreinte d'IBAN : un relevé ou une connexion bancaire du même compte réel crée ensuite **un compte distinct**.
- **Banque désactivée** : ces sections s'exportent et s'importent même quand la synchronisation bancaire est désactivée pour le foyer (`banking_enabled`) : quitter l'application ou restaurer ses données n'en dépend pas.
- Les quatre sections passent en dernier (ordre 110) : les enveloppes, les abonnements, les biens, les prêts et les personnes existent déjà, et les liens et règles viennent après les opérations.

## 6. Résolution des personnes

- Chaque `adult` du fichier doit être **associé à un compte existant** lors de l'import (par `email`, ou par une correspondance fournie à l'import). Un import ne crée jamais d'adulte : un adulte naît à sa première connexion (`core-data-model.md` §4.4). Un `adult` non résolu est une erreur.
- Un `dependent` est retrouvé par son nom ; s'il n'existe pas, il est créé.
- Le compte qui lance l'import doit avoir le droit d'écrire sur ce qu'il crée. Les éléments importés sont créés avec les propriétaires et la visibilité du fichier.

## 7. Validation

**Erreurs bloquantes** (rien n'est écrit) :

- version ou format non supporté, section ou champ inconnu ;
- `id` ou `ref` dupliqué dans une section ;
- fait en double dans un même fichier : deux valorisations du même élément à la même date, un second flux `opening`, ou deux flux de même `external_ref` (`import.duplicate_fact`) ;
- référence non résolue (personne, abonnement, fournisseur) ;
- somme des `share_bp` différente de 10 000, ou élément `private` avec plusieurs propriétaires ;
- phases d'un même abonnement qui se chevauchent, ou `ends_on` ≤ `starts_on` ;
- durée dont `count` ≤ 0, unité inconnue ;
- prix négatif hors `adjustment` et `refund` ; `allocated_amount` hors d'un `bundle` ou supérieur au prix ;
- devise non ISO 4217, date invalide ;
- `adult` non résolu (§6).

**Avertissements** (l'import continue) :

- période explicite (`starts_on`) qui chevauche une couverture existante : doublon possible ;
- abonnement sans phase, sans achat ni couverture ;
- durée d'une phase qui n'est pas un multiple de sa période ;
- paiement dans une devise sans taux disponible à sa date (`fx_rate_missing` prévisible).

## 8. Idempotence et conflits

- Chaque entité importée conserve son `id` de fichier comme **`import_ref`**, unique **par importeur** et par section. Un réimport ne reconnaît que les entités que l'importeur peut voir : les siennes d'abord, puis celles d'un autre adulte qui lui sont visibles. Un `id` déjà utilisé par une entité qu'il ne voit pas est importé comme nouveau, avec la même réponse qu'un `id` jamais utilisé, de sorte que l'import ne révèle rien d'un élément privé de l'autre adulte. Deux fichiers qui réutilisent les mêmes `id` sont en conflit entre entités visibles : préfixer les `id` de chaque source.
- Réimport :
  - même `id`, contenu identique → `unchanged` ;
  - même `id`, contenu différent → `conflict`, **rien n'est écrasé** ;
  - option `replace` : le contenu de l'entité est mis à jour, l'ancienne valeur restant dans l'audit.
- Une entité importée par l'importeur et qu'il ne voit plus (ses droits ont changé) est refusée (`import.not_permitted`) plutôt que dupliquée.
- **(1F3b)** `replace` vaut pour tout l'import : chaque entité en conflit est remplacée, avec le droit d'écriture sur l'élément ; le rapport la compte en `replaced`. Un fournisseur personnalisé archivé reste en conflit. Un remplacement ne retire pas un élément à l'autre adulte : il ne peut ni retirer des propriétaires un adulte autre que la personne qui importe (`import.not_permitted` sur `ownership`), ni rendre privé un élément qu'un autre adulte voit aujourd'hui (`import.not_permitted` sur `visibility`).

## 9. Provenance, audit et rapport

- Les données importées portent `source = import` et `created_via = import` ; l'auteur est l'adulte qui lance l'import.
- **Audit** : une entrée par import, avec l'identifiant de session, l'empreinte du contenu reçu (SHA-256 des empreintes de lots), la version du format et les compteurs.
- **(1F3b)** Une entrée `platform.audit_log` par import **commité** (aucune pour un commit refusé), catégorie `import`, action `commit`, entité `import_session` (clé = l'identifiant de session), acteur la personne qui importe, `via = import`, sans `item_ids`. Elle porte l'identifiant de session, l'empreinte du contenu (`content_hash`, ci-dessous), le format, la version, `replace` et les compteurs par section (`created`, `unchanged`, `replaced`). Aucune valeur du fichier n'y entre. `content_hash` est le SHA-256 des empreintes de lots, dans l'ordre du `commit` (sections de l'ordre du §11.4, puis indice de lot), chacune en hexadécimal minuscule, jointes par `\n`, sans retour à la ligne final. Cette entrée n'est lue que par l'importeur, sur la page de son import ; ni le journal de sécurité de la zone admin ni l'historique d'un élément ne la montrent.
- Le navigateur peut **conserver le fichier d'origine** : case « conserver le fichier », qui l'envoie séparément comme `document` (`core-data-model.md` §6) rattaché aux éléments créés. Le serveur ne reçoit plus le fichier d'office.
- **Rapport** (à l'écran et téléchargeable) : par section, nombre d'entités `created`, `unchanged`, `conflict` et, avec `replace`, `replaced`, plus la liste des avertissements et des erreurs avec leur chemin (`purchases[12].grants[0]`, indice global dans la section).

## 10. Export

L'export est **assemblé par le navigateur** à partir d'appels paginés ; le serveur ne construit jamais le fichier.

| Appel | Rôle |
|---|---|
| `GET /api/v1/platform/export/manifest` | `format`, `version`, `exportedAt` (heure du serveur), `revision`, `sections` : `[{ name, count }]`, toutes les sections connues, vides comprises |
| `GET /api/v1/platform/export/{section}?cursor=&limit=` | `{ section, entities, nextCursor }` : page de `limit` entités (1 à 500, 500 par défaut), curseur opaque, ordre stable ; `nextCursor` vaut `null` à la dernière page. Les entités sont écrites comme dans le fichier (`snake_case`, montants en chaînes, valeurs par défaut explicites) |

- Portée `read`. **Chacun exporte ce qu'il peut lire** : les éléments privés d'un adulte n'apparaissent que dans son export. N'apparaissent dans `persons` que les personnes référencées.
- Le navigateur assemble l'enveloppe `nid.data` (§3) et déclenche le téléchargement. Il peut aussi produire **un CSV par section**, converti côté client ; toute cellule **texte** commençant par `=`, `+`, `-`, `@`, tabulation ou retour chariot est **neutralisée** (préfixe d'une apostrophe) pour empêcher l'injection de formules dans un tableur (`security/application.md` §6). Pas encore livré : 1E4 livre l'écran d'export `nid.data` (`superpowers/specs/2026-09-26-export-screen-design.md`), le CSV viendra ensuite.
- **Cohérence** : `revision` est le SHA-256 (RFC 8785) de tout ce que ce lecteur exporterait. Elle ne bouge que si ce qu'il voit bouge, donc ne dit rien des éléments privés de l'autre adulte. Le curseur porte la `revision` : une page demandée avec le curseur d'une autre `revision` répond `409 export.revision_changed` (le navigateur relance l'export), un curseur illisible `400 export.bad_cursor`, une section inconnue `404`. À la fin, le navigateur relit le manifeste. Rien n'est stocké ni verrouillé : chaque appel reconstruit l'export du lecteur.
- **Identifiants** : l'`id` d'une entité est son `import_ref` s'il est unique dans la section exportée (en cas de doublon, la ligne importée par le lecteur le garde) ; sinon une clé dérivée de l'UUID interne (`sub-…`, `ph-…`, `pu-…`, `env-…`, `val-…`, `fl-…`, `hold-…`, `hsnap-…`, `hmov-…`). Un réimport écrit cet `id` en `import_ref`, si bien qu'un second export redonne les mêmes `id`.
- **Fournisseurs** : seuls ceux qu'une instance au même catalogue ne saurait pas résoudre (`custom`, `adopted`, et ceux du catalogue qu'il a archivés). Leur clé de fichier est leur `import_ref`, sinon leur clé stockée, sinon une version slugifiée ; jamais une clé que le fichier utilise déjà, y compris une clé du catalogue nommée directement (suffixe `-2`, `-3`…).
- **Personnes** : seules les personnes référencées sont écrites, `adult-N` puis `dependent-N`, numérotées parmi elles par nom affiché puis email (une instance neuve qui réimporte le fichier redonne les mêmes références) ; l'`email` pour un adulte.
- **Comptes bancaires** (§5.7) : ceux que le lecteur lit (bank sync D7, D22 : les siens, ceux d'une enveloppe qu'il lit, les cartes d'un compte qu'il lit), par nom puis `ref`, puis leurs opérations par compte, date et arrivée. Une enveloppe ou un compte courant absent de l'export du lecteur n'est pas nommé (`null`). Les **liens** (`bank_links`) : ceux des opérations exportées dont le lecteur lit l'élément payé (ou le compte en face, pour un virement), par compte, date et arrivée ; un lien vers un élément privé de l'autre adulte n'est pas exporté. Les **règles** (`bank_rules`) : les siennes seulement, sur un compte et un élément exportés. Un compte réimporté reçoit un nouvel `acc-<uuid>` dans l'instance qui l'importe, si bien qu'un second export ne redonne pas les mêmes `ref` (choix assumé, §5.7) ; un réimport du fichier d'origine l'y retrouve par sa `ref`.
- **Achats** : un achat ne garde que ses grants vers des abonnements exportés. Réimporté seul, il couvre donc moins d'abonnements et son coût se répartit sur les grants restants.
- **Non garanti** : réimporter un export dans l'instance d'où il vient. Une ligne créée dans l'interface n'a pas d'`import_ref` et serait recréée ; une ligne modifiée depuis son import ressortirait en `conflict`.
- `archived_at` n'est pas encore dans le format : rien n'archive un abonnement. Il arrivera avec l'archivage.
- Les contenus des documents (PDF) ne sont pas dans le fichier ; une section future les référencera par `sha256`.
- L'export ne remplace pas la sauvegarde complète (`architecture.md` §11) : il garantit de pouvoir quitter l'application.

## 11. Import : protocole et mise en œuvre

### 11.1 Session d'import

Le navigateur pilote une **session** ; le serveur valide et stocke les lots en **zone de staging**, puis écrit en une seule transaction.

| Appel | Rôle |
|---|---|
| `POST /api/v1/platform/imports` | Crée une session pour l'adulte connecté. Une seule session active par personne |
| `PUT /imports/{id}/batches/{section}/{n}` | Reçoit un lot avec son empreinte, le valide seul, le stocke. Idempotent |
| `POST /imports/{id}/validate` | Validation **complète** du staging ; renvoie le rapport. Synchrone. Paramètre `replace` (§8), faux par défaut |
| `POST /imports/{id}/commit` | **Revalide**, puis écrit en une transaction ; renvoie le rapport final. Synchrone. Paramètre `replace` (§8), faux par défaut |
| `GET /imports/{id}` | État, compteurs, rapport de la dernière validation |
| `GET /imports` | Sessions récentes de la personne |
| `DELETE /imports/{id}` | Annule la session et vide le staging |

Portée requise : `write:direct` (un import écrit directement, `identity-and-access/authorization.md` §3). Une session n'est visible que de la personne qui l'a créée : son identifiant est **aléatoire sur 128 bits** et l'appartenance est vérifiée à **chaque appel** (`404` sinon).

**États** : `open` (réception de lots) → `validated` ou `invalid` → `committed`. Un nouveau lot après `validated` ramène la session à `open`. Sorties : `cancelled`, `expired`.

**(1F3b)** `replace` se donne à `validate` **et** à `commit` : un conflit non résolu rend la session `invalid`, et seule une session `validated` peut être commitée. Le drapeau n'est jamais stocké dans la session : le `commit` revalide avec le sien propre. Une session validée avec `replace` puis commitée sans lui retrouve donc ses conflits, et le `commit` est refusé comme aujourd'hui (`422 import.not_committed`, la session repasse à `invalid`).

### 11.2 Lots

- Au plus **500 entités** et **1 Mo** par lot ; un lot appartient à une section (`persons`, `subscriptions`…) et porte un indice `n`.
- L'**empreinte** est le SHA-256 du contenu JSON canonique du lot, au sens de la **RFC 8785** (JSON Canonicalization Scheme), identique côté navigateur et côté serveur. Rejouer le même lot (`section`, `n`, empreinte) donne le même résultat ; même emplacement avec une autre empreinte : `409`.
- **Ordre libre** à l'envoi : les références entre lots sont vérifiées à la validation complète, pas à la réception.
- Le serveur valide chaque lot à la réception : structure (types du contrat), règles locales à l'entité (montants, durées, devises, unités). Les erreurs reviennent immédiatement, avec leur chemin.

### 11.3 Staging

Deux tables du schéma `platform`, **jamais lues par les modules** métier :

| Table | Champs |
|---|---|
| `import_session` | `id`, `person_id`, `state`, `created_at`, `expires_at`, `counts`, `report` |
| `import_batch` | `session_id`, `section`, `batch_index`, `content_hash`, `payload` (JSONB), `received_at` |

Le staging est vidé à `commit`, à l'annulation et à l'expiration.

### 11.4 Validation et écriture

- **`validate`** : chaque module valide l'ensemble de sa section sur le staging (références entre lots, chevauchements, invariants du Core, droits de la personne, idempotence par `import_ref`, taux de change), et Platform assemble un rapport unique. Rien n'est écrit dans les données du foyer.
- **`commit`** : le serveur **rejoue la validation complète** (l'état a pu changer depuis `validate`), puis appelle les modules dans l'ordre `persons` → `providers` → les sections qui créent des éléments (`subscriptions`, `envelopes`, `properties`, `loans`, `income_sources`) → les sections qui en dépendent (`billing_phases`, `purchases`, `valuations`, `flows`, `property_recurring_costs`, `tenancies`, `loan_phases`, `loan_insurances`, `loan_fees`, `loan_events`, `loan_bank_schedule`, `income_phases`, `income_one_off_taxable`) → les supports d'enveloppe (`holdings`, `holding_snapshots`, `holding_movements`, après `flows` qu'un mouvement peut nommer) → les objectifs (`goals`, après les enveloppes qu'ils lient) → les comptes bancaires, leurs opérations, leurs liens et leurs règles (`bank_accounts`, `bank_transactions`, `bank_links`, `bank_rules`), dans **une transaction unique**. Le staging de la session est vidé dans la même transaction. Une erreur annule tout.
- Les `DbContext` des modules partagent la connexion et la transaction de l'import (`Database.UseTransactionAsync`). **Vérifié** (`spikes.md` §6) : le retour arrière efface l'écriture de tous les modules, et l'outbox reste invisible aux autres sessions jusqu'au commit.

### 11.5 Bornes

Pour tenir dans une requête ordinaire, sans tâche de fond :

| Borne | Valeur par défaut |
|---|---|
| Entités par import | 50 000 (au-delà : refus, à découper par section) |
| Entités / taille par lot | 500 / 1 Mo |
| Durée maximale de `validate` et de `commit` | 60 s, configurable ; dépassement : annulation complète, erreur `import_too_large` |
| Durée de vie d'une session non validée | 24 h |
| Sessions actives par personne | 1 |
| Profondeur JSON, longueur d'une chaîne | 32 niveaux, valeur configurable |

Le **nettoyage** des sessions expirées se fait **à la demande** : à la création d'une session et à l'affichage de la liste. Aucun planificateur.

### 11.6 Reprise

Fermer l'onglet n'annule rien : la session reste jusqu'à son expiration. Pour reprendre, l'utilisateur **re-sélectionne le fichier** ; le navigateur recalcule les lots de façon déterministe (mêmes sections, mêmes tailles, mêmes empreintes) et **saute ceux que le serveur possède déjà**.

### 11.7 Contrat des modules

Chaque module fournit une `IImportSection` dans ses `Contracts` (il possède sa section) :

| Opération | Rôle |
|---|---|
| `ValidateBatch` | Règles locales aux entités d'un lot |
| `ValidateAll` | Validation complète sur le staging de la session |
| `Apply` | Écriture, appelée dans la transaction de `commit` |
| `ExportAsync` | Tous les enregistrements de la section que le lecteur peut exporter ; Platform les pagine (§10) |

Platform ne connaît aucune entité métier : elle orchestre l'ordre, le rapport, la transaction et l'audit.

### 11.8 Autres clients

Un script ou une commande d'administration suit le même protocole : créer une session, envoyer un lot (un seul suffit pour un petit fichier), valider, valider à nouveau à la confirmation, puis commiter. Il n'existe pas de chemin d'import simplifié : une seule voie à tester et à sécuriser.

### 11.9 Rôle du navigateur

Le navigateur orchestre (assistant décrit dans `frontend/screens.md`) : lecture et conversion locale (CSV → `nid.data`), pré-validation structurelle avec le schéma publié, découpage en lots, envoi avec progression et nouvelles tentatives, affichage des rapports, confirmation avant `commit`. Il ne décide jamais : le rapport du serveur fait foi.

## 12. Exemple minimal

```json
{
  "format": "nid.data",
  "version": 1,
  "persons": [
    { "ref": "adult-1", "kind": "adult", "display_name": "Adulte 1" },
    { "ref": "adult-2", "kind": "adult", "display_name": "Adulte 2" }
  ],
  "subscriptions": [
    {
      "id": "sub-spotify",
      "provider_key": "spotify",
      "name": "Spotify Premium Duo",
      "ownership": [
        { "person": "adult-1", "share_bp": 5000 },
        { "person": "adult-2", "share_bp": 5000 }
      ],
      "visibility": "shared",
      "service_users": ["adult-1", "adult-2"],
      "auto_renew": true
    }
  ],
  "billing_phases": [
    {
      "id": "ph-001",
      "subscription_id": "sub-spotify",
      "starts_on": "2025-06-28",
      "ends_on": null,
      "price": { "amount": "17.20", "currency": "EUR" },
      "every": { "count": 1, "unit": "month" },
      "payer": { "type": "household" }
    }
  ],
  "purchases": [
    {
      "id": "pu-001",
      "date": "2025-10-10",
      "label": "Carte cadeau 3 mois",
      "kind": "gift",
      "price": { "amount": "0.00", "currency": "EUR" },
      "payer": { "type": "external" },
      "grants": [
        { "subscription_id": "sub-spotify", "duration": { "count": 3, "unit": "month" }, "source": "gift" }
      ]
    }
  ]
}
```

## 12bis. Décisions prises en 1D1 (2026-09-23)

Détail et raisons : `superpowers/specs/2026-09-23-import-protocol-design.md`.

- **Adultes** : résolus d'abord par la correspondance donnée à la création de la session (`adults: { "<ref>": "<person id>" }`), puis par `email`. Non résolu : erreur `import.adult_unresolved`, dont le message liste les adultes existants.
- **Idempotence** : chaque ligne importée garde `import_ref` (l'`id` du fichier) et `import_hash`, le SHA-256 RFC 8785 de son enregistrement. Même `id` et même empreinte : `unchanged` ; même `id`, autre empreinte : `conflict`. Une modification faite ensuite dans l'interface ne rend pas conflictuel le réimport du même fichier : le fichier, lui, n'a pas changé.
- **Un conflit bloque le `commit`** : sans `replace`, « rien n'est écrasé » veut dire que rien ne bouge.
- **`replace` est livré en 1F3b** (§8, §9, §11.1), sur le journal d'audit livré en 1F3a dont sa promesse dépendait.
- **Nombres dans l'empreinte** : sérialisés comme ECMAScript (RFC 8785 §3.2.2.3). Un montant écrit en nombre JSON est accepté et lu en décimal ; l'empreinte ne sert qu'à identifier.
- **Lots** : numérotés à partir de 0 sans trou ; tous sauf le dernier d'une section contiennent exactement 500 entités, ce qui rend exact l'indice global `n × 500 + position`. Un lot en erreur n'est pas mis en staging : son emplacement reste libre pour le lot corrigé.
- **Sections refusées pour l'instant** (`import.section_not_supported`) : seules celles des modules à venir. `providers` n'est plus refusée depuis 1C2b (2026-09-24, voir « Décisions prises en 1C2b » ci-dessous) ; `provider_key` y est désormais vérifié.
- **Avertissements** : ceux du moteur (sauf `subscriptions.horizon_reached`, propre à la lecture) et `import.phase_not_whole_periods`. `fx_rate_missing` (code `core.fx_rate_missing`), livré en 1C2a, est un avertissement **non bloquant** : à la validation, il est levé pour chaque paiement (charge de phase ou achat) dans une devise autre que celle de référence sans cours à sa date ; il signale, avant le `commit`, que le rattrapage n'a pas encore tourné ou qu'aucune des deux sources ne couvre cette devise, sans empêcher l'import.
- **Droits sur un enregistrement existant** : une ligne réimportée `unchanged` ne demande que le droit de lecture sur l'élément qu'elle référence ; une ligne nouvelle qu'un fichier ajoute à un élément déjà importé (par exemple une phase ou un achat) demande le droit d'écriture. La règle de création (§6) ne s'applique donc qu'aux enregistrements qui créent un élément.
- **Un lot accepté n'est jamais perdu en silence.** Le retour de la session à `open` et l'insertion du lot forment une seule transaction : le verrou pris sur la ligne de la session par ce retour est tenu jusqu'à ce que le lot soit validé en base. Un `commit` qui a pris la session avant fait attendre le lot, qui trouve ensuite la session terminée et est refusé avec `import.state` sans rien écrire ; un `validate` ou un `commit` arrivé après attend que le lot soit en base, puis le voit. Un `validate` ne range son rapport que si la session n'a pas bougé pendant qu'il tournait (sa date de mise à jour, qui avance à chaque transition, sert de version) ; sinon il répond `409 import.state` (« validez de nouveau »), pour ne pas ranger un rapport qui ignore un lot arrivé entre-temps.
- **Durée de vie appliquée à l'usage** : une session active dont `expires_at` est passé est traitée comme `expired` par chaque appel, même si aucun balayage ne l'a encore marquée : `GET` la montre `expired`, un lot, `validate`, `commit` ou l'annulation répondent `409 import.state`.
- **Deux codes `400` de forme pour un lot** : `request.malformed` quand le corps ne se lit pas (pas du JSON, plus de 1 Mo, plus de 32 niveaux) ; `import.malformed_entity` quand le corps se lit mais que le lot est refusé en entier (entités qui ne sont pas un tableau, indice négatif, surrogate isolé, caractère U+0000, que `jsonb` ne sait pas stocker). Les bornes et l'empreinte ont leurs propres `400` (`import.too_many_entities`, `import.string_too_long`, `import.hash_mismatch`). Une entité refusée à l'intérieur d'un lot lisible n'est pas un `400` : elle revient dans les `issues` de la réponse, avec `accepted: false`.
- **Un utilisateur listé deux fois dans `service_users`** est refusé dès la réception (et à la validation) avec `import.duplicate_id` au chemin `subscriptions[i].service_users[j]`.
- **Pas encore fait** : le schéma JSON publié (1E).
- **Codes** : les erreurs du protocole sont préfixées `import.` ; les règles des modules gardent leurs codes (`core.share_sum`, `subscriptions.phase_overlap`…). `import_too_large` (§11.5) s'écrit `import.too_large`.
- **Décisions prises en 1C2b (2026-09-24)** (détail et raisons : `superpowers/specs/2026-09-24-provider-catalog-design.md` §5 et §10) : `providers` (§5.2) n'est plus refusée — chaque entrée est résolue, dans l'ordre, contre une entrée déjà importée sous la même référence par cet importeur (`unchanged` ou `conflict`), puis contre une entrée de même référence et même empreinte **visible de l'importeur**, puis sinon créée au `commit` avec une clé générée, en gardant `import_ref`/`import_hash` comme toute autre entité importée ; `provider_key` (§5.3) est désormais vérifié — une entrée `providers` du fichier prime toujours sur une clé existante de même orthographe, sinon une clé existante doit être visible de l'importeur et non archivée, sinon `import.unresolved_reference` au chemin `subscriptions[i].provider_key` (non répété quand l'entrée `providers` de cette clé a déjà échoué) ; la provenance d'un import (« déjà importé sous cette référence ? ») est tenue **par importeur**, index séparé pour chacun, si bien que deux personnes qui importent le même fichier reçoivent chacune leur propre fournisseur et jamais celui de l'autre si elles ne peuvent pas le voir — une clé qui n'existe que par une souscription privée d'un autre adulte répond comme une clé inconnue, au mot près.
- **Décisions prises en 2B1b (2026-09-30)** (détail et raisons : `superpowers/specs/2026-09-30-investments-import-export-design.md`) :
  - **Le Core possède `valuations` et `flows`** : le module Placements n'apporte que `envelopes` ; les deux sections de faits sont importées et exportées par le Core (`Nid.Platform.Contracts.ImportSectionKit` pour les aides communes, `ImportResolver` pour la résolution des références).
  - **Ordre des appels d'export** : les sections qui créent des éléments d'abord, puis les sections de faits, `persons` en dernier (elle ne garde que les personnes référencées par ce qui précède).
  - **« Aujourd'hui »** vient du calendrier du foyer, pas de l'horloge du serveur (date non future d'une valorisation, date de clôture).
  - **Un fait annulé libère sa référence** : un réimport le recrée.
  - **Devise d'un fait** : un écart avec la devise de l'élément est signalé au chemin `.amount.currency`.
  - **Clôture par `replace`** : une enveloppe existante dont le fichier pose `closed_on` est vérifiée contre ses valeurs et flux courants stockés autant que contre ceux du fichier (la règle de la clôture de l'API). Un fait stocké que le fichier remplace par une date plus ancienne compte encore : un fichier qui recule le dernier fait et clôt à la fois doit être scindé en deux imports.
  - **Une correction ne retire qu'un fait** : une correction dont la nouvelle date ou la nouvelle clé rencontre un autre fait courant est refusée, même avec `replace` ; deux faits qui échangent leurs dates dans un même fichier sont refusés de la même façon.
  - **La clôture est tracée** dans l'audit de l'enveloppe (`closed_on`).
- **Décisions prises en 2C1 (2026-10-02)** (détail et raisons : `superpowers/specs/2026-10-02-income-server-design.md` §5) :
  - **`ended_on`** : une source qui porte `ended_on` est terminée et archivée ; en ajouter ou en retirer un à un réimport (`replace`) termine la source (les phases en cours après cette date sont coupées, comme `close`) ou la rouvre.
  - **Chevauchement** : vérifié par (source, libellé), contre les phases du fichier et contre les phases déjà stockées de la source (`income.phase_overlap`).
  - **Phases d'une source qui reste terminée** : contrairement à l'API, l'import les écrit (vérifiées contre `ended_on` et coupées à cette date), pour que l'historique d'une source terminée fasse l'aller-retour.
  - **Avertissements** : `income.source_without_phase` (une source sans aucune phase) et `income.gap_soon` (une phase active qui s'achève dans les 60 jours sans successeur de même libellé).
  - **Revenu exceptionnel** : il passe par la section `flows` du Core (`kind: income`, `category: one_off`), sans section propre ; les sources du fichier alimentent le contexte d'import comme les enveloppes.
- **Décisions prises en 3F1 (2026-10-15)** (détail et raisons : `superpowers/specs/2026-10-15-realestate-import-export-design.md`) :
  - **Huit sections** du module Immobilier, dans cet ordre : `properties`, `property_recurring_costs`, `tenancies`, `loans`, `loan_phases`, `loan_insurances`, `loan_fees`, `loan_events` (`realestate/README.md` §10, `realestate/loans.md` §10). Une charge ou un bail nomme son bien (`property`), un prêt nomme son bien (`property`, facultatif), une phase, une assurance, un frais ou un remboursement anticipé nomme son prêt (`loan`) : toujours une entité **du même fichier**. Un montant est dans la devise de son bien ou de son prêt (`core.currency_mismatch` à `.currency` sinon) ; un taux est une chaîne décimale (un nombre JSON est accepté).
  - **Règles des écrans**, sur ce que chaque bien ou prêt contient une fois l’import écrit (ses lignes stockées que le fichier ne nomme pas comprises) : baux sans chevauchement ni début avant l’achat, unité d’une charge prévue au mois ou à l’année, montants positifs, achat au plus tard aujourd’hui ; un prêt est vérifié entier (phases numérotées 1, 2, 3… sans trou dans n’importe quel ordre, assurances, frais, remboursements) par le moteur d’échéancier, et sa première erreur est signalée sur l’entité du fichier qui l’apporte, sinon sur le prêt.
  - **`schedule_source`** : `computed` ou absent ; `bank` est accepté depuis 3F2, qui apporte `loan_bank_schedule` (voir ci-dessous).
  - **Convention des flux** : un module déclare, pour les éléments qu’il possède, les catégories permises par nature de flux (`IFactConventions`). Un flux d’un bien hors convention (nature autre que `expense` ou `income`, catégorie absente ou hors de la liste de `realestate/README.md` §4) est une erreur `realestate.invalid_category` au chemin `flows[i].kind` ou `flows[i].category`. Les autres éléments (enveloppes, sources de revenus) ne changent pas.
  - **Provenance** : chaque ligne importée garde `import_ref`, `import_hash` et `imported_by` (unique par importeur) ; une ligne remplacée garde son premier importeur. Les phases et les assurances gardent leur référence quand le contrat du prêt est réécrit. Depuis l’écran, une modification qui garde leur nombre la garde par numéro de phase et par rang d’assurance : réimporter le même fichier reste `unchanged` et la modification reste. Une modification qui ajoute ou supprime une phase ou une assurance ne la garde qu’aux lignes laissées identiques : une ligne supprimée libère sa référence et le même fichier la recrée ; une ligne changée dans la même modification (une phase renumérotée comprise) perd la sienne, et le même fichier rajoute sa version à côté, ou refuse le prêt si ses phases ne se suivent plus.
  - **Export** : chacun exporte les biens et les prêts qu’il peut lire, triés par nom puis `id`, valeurs par défaut écrites (`notary_fees`, `agency_fees`, `other_acquisition_costs`, `charges_monthly` à `0.00`, `valuation_cadence`, `visibility`, `rounding_mode`, `first_period_mode`, `schedule_source: computed`) ; `id` dérivés `prop-`, `cost-`, `ten-`, `loan-`, `lp-`, `li-`, `fee-`, `ev-`. Un prêt lié à un bien que le lecteur ne peut pas lire est exporté **sans** `property` : le bien n’est pas dans le fichier et rien ne le révèle ; réimporter ce fichier perd le lien.
  - **Pénalité d’un remboursement anticipé** : `loan_events` n’a pas de champ pour elle ; elle est exportée comme un frais `early_repayment_penalty` à la date du remboursement et réimportée comme un frais isolé (le coût reste, le lien est perdu).
  - **Aucun avertissement** propre à l’import : ceux du bien (`visibility_mismatch`, `loan_other_currency`, `rental_without_tenancy`) restent sur sa fiche.
  - **Réimporter un export dans le même foyer** reste non garanti (§10) : une ligne créée dans l’interface, exportée sous un `id` dérivé, est recréée ; pour l’immobilier, un bail créé dans l’interface sur un bien importé revient refusé, en chevauchement avec lui-même.
- **Décisions prises en 3F2 (2026-10-15)** (détail : `superpowers/specs/2026-10-15-bank-schedule-design.md`) :
  - **`loan_bank_schedule`** : `id`, `loan` (un prêt du même fichier), `due_date`, `payment`, `interest`, `principal`, `insurance?`, `crd` ; montants en chaînes décimales dans la devise du prêt (un nombre JSON est accepté), écrits avec deux décimales ; `id` dérivé `lbs-`. Les lignes d’un prêt arrivent dans n’importe quel ordre : elles sont lues par date et le tableau entier est vérifié avec le prêt (`realestate/loans.md` §11), la première erreur sur la ligne qui l’apporte.
  - **`schedule_source`** : `computed` ou `bank` ; `bank` exige que le prêt ait des lignes de banque une fois l’import écrit (celles du fichier ou des lignes enregistrées que le fichier ne nomme pas), sinon `realestate.bank_schedule_missing` à `loans[i].schedule_source`. L’export l’écrit tel qu’il est enregistré.
  - **Provenance** : chaque ligne garde sa référence d’import ; un tableau remplacé à l’écran garde celle de chaque ligne par sa date : le même fichier se réimporte `unchanged` et les lignes de l’écran restent.
  - **Un tableau saisi à l’écran** : un premier import de lignes aux mêmes dates qu’un tableau saisi à l’écran est refusé (les dates doivent être distinctes et croissantes) ; il faut d’abord remplacer le tableau à l’écran ou le supprimer.

## 13. Questions ouvertes

- ~~Association des adultes aux comptes : par `email` du fichier, ou par saisie à l'import (un fichier sans email doit rester importable).~~ Tranché en 1D1, voir §12bis.
- Schéma JSON de `nid.data` : généré depuis les types source-générés (`JsonSchemaExporter`, .NET 9+) et publié par `GET /api/v1/platform/import/schema` ; le navigateur le compile au build (`frontend/architecture.md`). **Vérifié** (`spikes.md` §4) : l'exporteur renvoie `true` (tout accepté) pour `LocalDate`, `Instant` et les décimaux à convertisseur ; un `TransformSchemaNode` leur donne un vrai type, et les champs optionnels doivent avoir une valeur par défaut, sinon ils sont `required`.
- Conversion CSV → `nid.data` : réalisée côté navigateur ; les sections `valuations` et `flows` sont définies (Investments) ; reste l'usage qu'en fera RealEstate.
- Valeurs par défaut des bornes (§11.5) : à ajuster après mesure sur des imports réels.
- Format des documents dans l'export (section `documents` référençant les `sha256`).
- Sections des autres modules : définies quand ces modules le seront.
- Politique de migration entre versions du format : l'import de la version précédente est prévu, sa conversion reste à spécifier au premier changement de version.
- **Décisions prises pour les objectifs (2026-10-23)** (détail et raisons : `superpowers/specs/2026-10-23-savings-goals-design.md` D16) :
  - **Une section `goals`** : `{ id, name, kind, target_amount?, months?, target_date?, note?, visibility, ownership, closed, funding: [{ envelope, share_bp }], entries: [{ on, amount }] }`. Les montants sont des chaînes décimales (un nombre JSON est accepté), dans la devise de référence du foyer ; `ownership` liste `{ person, share_bp }` ; `envelope` est l'`id` d'une entité `envelopes` **du même fichier**. Elle passe en dernier (ordre 100) : les enveloppes existent déjà.
  - **Règles de l'API**, `target_date` passée exceptée (un import restaure le passé) : genre, cible et mois selon le genre (`goals.unknown_kind`, `goals.invalid_target`, `goals.invalid_months`), parts de 1 à 10 000 et une enveloppe une fois par objectif (`goals.invalid_share`). Un relevé n'est pas daté du futur (`goals.progress_in_future`), a un montant positif ou nul, et une seule fois par date.
  - **Somme des parts (D10)** : elle n'est pas vérifiée à l'import, qui restaure ce que le fichier dit (un export peut porter plus de 100 % sur une enveloppe, D10 le permet) ; la lecture signale `over_allocated`. Seule la part de chaque lien est vérifiée (1 à 10 000). Un lien vers une enveloppe clôturée est accepté.
  - **`closed: true`** : l'objectif est créé puis archivé ; `replace` le ferme ou le rouvre.
  - **`replace`** remplace le nom, les détenteurs, les champs, les liens et les relevés (ceux que le fichier ne nomme plus sont supprimés) ; le genre ne change pas. Les liens vers une enveloppe que l'importeur ne voit pas sont gardés tels quels (D10c).
  - **Export** : les objectifs que le lecteur peut lire, par nom puis `id` (`goal-<uuid>` sans référence d'import). Un lien vers une enveloppe absente de l'export du lecteur (l'enveloppe privée d'un autre adulte) est omis, sans rien révéler ; réimporter ce fichier avec `replace` le garde (D10c : un lien vers une enveloppe que l'importeur ne voit pas est gardé tel quel).
