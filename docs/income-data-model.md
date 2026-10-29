# Modèle de données du module Revenus

> Statut : brouillon issu de la discussion technique. Décrit les sources de revenu déclarées, leurs périodes, les calculs de revenus mensuels, de part engagée et de marge, et leur sensibilité.
> Références : `Project.md` §8b, `core-data-model.md` (éléments, flux), `subscription-data-model.md` (logique de phases), `import-format.md`, `architecture.md` §5.

---

## 1. Objectifs

- Répondre à **« que reste-t-il chaque mois après nos engagements ? »**, question qui manque au tableau de bord.
- Rester **déclaratif** : on renseigne des sources et des montants attendus, on ne suit pas chaque virement. Le module ne fait pas de la comptabilité (`Project.md` §1 et §11).
- Réutiliser ce qui existe : la logique des **phases** des abonnements et les **flux** du Core.
- Protéger des données très personnelles (§12).

## 2. Vue d'ensemble

```
Core                                       Income (schéma income)
item (module = income, kind = source) ──── income_source (1:1)
  ├─ ownership (le titulaire), visibility, currency         └──< income_phase
  └──< flow  (Core : revenus exceptionnels)
```

Une **source de revenu** est un `item` du Core : son nom, son titulaire (propriétaire), sa visibilité, sa devise et son archivage viennent du Core. Les revenus exceptionnels sont des `flow` du Core.

## 3. Entités

### 3.1 `income_source`

| Champ | Type | Remarques |
|---|---|---|
| `item_id` | uuid | Clé primaire |
| `kind` | text | `salary`, `self_employment`, `pension`, `unemployment`, `family_allowance`, `social_benefit`, `alimony`, `other` |
| `payer_label` | text? | Employeur ou organisme, texte libre. **Aucun identifiant personnel** (pas de numéro de sécurité sociale, pas d'adresse) |
| `note` | text? | |
| `ended_on` | date? | Date de fin de la source (2C1) : vide tant qu'elle est ouverte ; posée par la clôture, qui archive l'élément ; effacée par la réouverture |

### 3.2 `income_phase`

Une période de revenu, sur le modèle d'une phase de facturation d'abonnement.

| Champ | Type | Remarques |
|---|---|---|
| `id` | uuid | |
| `source_item_id` | uuid | |
| `label` | text | « Salaire de base », « Prime annuelle »… Plusieurs composantes peuvent courir en parallèle sur une même source. 60 caractères au plus |
| `starts_on` | date | Première occurrence |
| `ends_on` | date? | Intervalle semi-ouvert. Vide = en cours |
| `net_amount` | numeric(19,4) | > 0. **Net versé** (voir ci-dessous), dans la devise de l'élément |
| `every` | durée | **Mois ou année seulement** : `{1, month}`, `{3, month}`, `{1, year}` |
| `gross_amount` | numeric(19,4)? | Indicatif, sans effet sur les calculs |
| `taxable_amount` | numeric(19,4)? | Amendement 2026-10-25 (#159) : **net imposable par versement** (ligne *Net imposable* du bulletin, à ne pas confondre avec le net versé). > 0, au plus `gross_amount` quand celui-ci est renseigné (`income.invalid_taxable`, HTTP 422). L'estimation de l'impôt le lit à la place de brut × taux. Un changement de paie ne reprend jamais celui de la période précédente |
| `max_cycles` | int? | Nombre maximal d'occurrences |
| `note` | text? | |

- **Net versé** = montant reçu sur le compte, **après cotisations sociales et prélèvement à la source**, avant tout autre impôt. Aucun calcul fiscal (`Project.md` §11).
- **Une augmentation ferme la période courante et en ouvre une nouvelle** : l'historique des changements est conservé.
- **Pas de chevauchement** pour une même paire (`source_item_id`, `label`).
- **Couverture d'un mois par un bulletin** (#160, `superpowers/specs/2026-10-27-payslips-design.md` §2) : un bulletin est un document de rôle `payslip`, lié à une phase (`income.phase`) ou à la source (`core.item`, cas de l'assistant) ; sa **date de document** donne le mois qu'il couvre, quelle que soit la phase visée (corriger les dates d'une phase ne le déplace pas). Un bulletin sans date, archivé ou illisible pour l'acteur ne couvre aucun mois. Le calcul se fait dans le navigateur, sur des dates seulement. Règle **« pas de bulletin »** : un mois est signalé manquant pour une source de genre `salary` seulement, s'il compte un versement passé, n'est couvert par aucun bulletin et tombe au plus tôt le mois du plus ancien bulletin de la source (pas de mur de manquants avant Nid).

### 3.3 Revenus exceptionnels

> Documents : une phase (`income.phase`) et un revenu exceptionnel (`income.one_off`) sont des cibles de lien de document, ancrées sur leur source (`documents-data-model.md` §5). Supprimer l'un retire ses liens, corriger un revenu exceptionnel les déplace sur le nouveau flux, dans la même transaction.

Prime ponctuelle, remboursement, indemnité : un `flow` du Core avec `kind = income` et `category = one_off`, posé sur la source (`core-data-model.md` §5.3). Il compte dans les revenus du mois où il tombe (§6) et prépare l'évolution vers « attendu + réel » (§14).

Amendement 2026-10-25 (#159) : un revenu exceptionnel peut porter son **net imposable**, dans la table `income.one_off_taxable (flow_id pk, source_id → income_source on delete cascade, taxable_amount numeric(19,4) check > 0)`, propre au module (la table des flux du Core n'est pas modifiée). Elle suit le flux : écrite à l'ajout et à la correction, supprimée avec lui ou avec la source. Voir `superpowers/specs/2026-10-25-tax-taxable-income-and-per-design.md` §3.

Un revenu exceptionnel porte aussi des documents (#160) : un document lisible lié à `income.one_off`, tout rôle, s'affiche sur sa ligne (icône trombone, menu « Télécharger », « Voir le document », « Remplacer », « Retirer »). « Joindre un document », dans le menu de la ligne, dépose un document de rôle `payslip` daté de `occurredOn`, titré par la note du revenu ou « Revenu exceptionnel <date> ». Un tel lien ne couvre aucun mois de bulletin (§3.2).

Le détail d'une source liste ceux des 12 derniers mois, ou ceux d'une plage `from` / `to` (incluse, un côté absent restant ouvert) ; la fiche propose « 12 derniers mois » ou « Tout ». Une source terminée accepte encore l'ajout, la correction et la suppression d'un revenu exceptionnel daté au plus tard de sa fin (`income.source_ended` au-delà), sans la rouvrir.

## 4. Occurrences et équivalents

Fonctions pures dans `Domain`, **décimal exact**.

**Occurrences attendues** d'une phase : `starts_on` + k × `every` (arithmétique de mois du Core, jour d'ancrage écrasé en fin de mois ; une phase mensuelle ou annuelle qui commence le dernier jour d'un mois tombe le dernier jour de chaque mois, si bien qu'une phase changée le 28/02 garde la cadence de fin de mois), tant que la date est avant `ends_on` et que k < `max_cycles`. C'est la vue **trésorerie**, utilisée pour « à venir ».

**Équivalent mensuel** d'une phase = `net_amount ÷ (count × 12 si l'unité est l'année, sinon count)`. C'est la vue **lissée** : une prime annuelle de 1 800 € compte 150 € par mois. Précision pleine en interne, arrondi à 0,01 (demi supérieur) à l'affichage.

| Métrique | Définition |
|---|---|
| **Revenus attendus du mois m** (trésorerie) | Σ des occurrences tombant dans m, plus les flux exceptionnels de m |
| **Équivalent mensuel** à la date d | Σ des équivalents mensuels des phases actives à d (`starts_on` ≤ d < `ends_on`) |
| Revenus attendus de l'année | Σ des occurrences de l'année civile, plus les flux exceptionnels |
| Répartition par personne | Parts du Core : part × montant (`core-data-model.md` §4.3) |

## 5. Revenus du foyer

Le tableau de bord additionne trois origines par le contrat `IIncomeContribution`.

| Origine | Ce qu'elle apporte |
|---|---|
| **Module Revenus** | Équivalent mensuel des phases actives, plus flux exceptionnels |
| **RealEstate** | **Loyer prévu** des baux en cours (`rent_monthly`), **sans** les provisions de charges (`realestate/README.md` §6) ; compté dans « Revenus / mois » de l'accueil (un bien dont tous les baux sont terminés n'est plus une source) |
| **Investments** | Revenus du patrimoine réellement **encaissés** sur 12 mois (flux `income`), affichés **à part**, hors équivalent mensuel |

- Chaque contribution est émise **par élément**, pour que les droits et la répartition par personne soient justes.
- Les montants d'une autre devise sont convertis au taux de la date (`core-data-model.md` §8).
- Une source dont la date de fin est passée n'apporte plus rien à d.
- La **vue d'ensemble des revenus** (`GET /api/v1/income/overview`) compte aussi les loyers des baux (loyer seul, sans les charges), mois par mois, proratisé au jour pour un mois partiel ; les baux terminés comptent pour les mois où ils ont couru.

## 6. Part engagée et marge

| Métrique | Définition |
|---|---|
| **Engagements mensuels** | Σ `ICommitmentsContribution` (abonnements, crédits, assurances, charges récurrentes prévues) dans la même portée |
| **Part des revenus engagée** | engagements ÷ équivalent mensuel des revenus |
| **Marge avant dépenses courantes** | équivalent mensuel des revenus − engagements |

- La portée est celle du sélecteur **Foyer / Moi** : Foyer ne compte que les éléments partagés, Moi inclut mes éléments privés.
- **Sans aucun revenu déclaré, la marge est « inconnue »**, jamais égale à moins les engagements.
- Le nom est **« marge avant dépenses courantes »**, et non « reste à vivre » : les engagements ne couvrent que ce qui est déclaré, pas l'alimentation ni le reste.

## 7. Rappels et échéances

| Situation | Effet |
|---|---|
| Une période se termine dans les 60 jours **sans période qui la suive** (même source et même `label`) | Avertissement **`income_gap_soon`**, rappel « Renseigner la suite de … » dans « À faire » |
| Chaque janvier | Rappel « Revoir vos revenus » (désactivable) |
| Fin d'une période | Apparaît dans « À venir » (fin de CDD, fin de droits) |

## 8. Propositions

Le module implémente `IProposalHandler` pour :

| `kind` | Payload | Application |
|---|---|---|
| `income.change_phase` | `{ label, effective_on, net_amount: { amount, currency }, every?, gross_amount?, taxable_amount? }` sur une source cible | **Ferme** la période courante de ce `label` à `effective_on` et **ouvre** la suivante |
| `income.record_one_off` | `{ occurred_on, amount: { amount, currency }, note? }` sur une source cible | `IFlows.Record` (`income`, `one_off`) |

Clé de dédoublonnage (`subject_key`) : `label` et `effective_on`, ou la date du flux.

**Livré en 4D1** : un bulletin de paie corrige le revenu attendu (`income.change_pay`, `{ paidOn, phaseLabel, net, gross?, taxable?, effectiveOn }`, clé `<phaseLabel>:<yyyy-MM de paidOn>`). La comparaison vient en premier : un net à 2 % près du net attendu (réglage `Income:PayslipTolerance`, validé au démarrage : 0 ≤ x < 1) et un brut égal au centime, quand les deux sont connus, sont « conformes » et ne créent rien ; un net imposable (`taxable`) que le bulletin imprime et que la période n’a pas, ou a autrement, rend le bulletin non conforme (égalité au centime, sans tolérance) : il change le revenu comme un brut différent, et la nouvelle période reçoit le net imposable du bulletin. Un bulletin sans net imposable ne compare rien de ce côté-là. Sinon la période change à la date de paiement attendue du mois (calculée comme `GetIncomeExpectedQuery`), avec la cadence et la note de la période remplacée ; un montant différent dans le premier mois de la période ou dans un mois sans paiement est refusé à `paidOn`. Un bulletin sans brut garde le brut de la période (absent signifie non imprimé, pas zéro) ; le net imposable, lui, n’est jamais repris de la période remplacée. Aucun montant « reçu » n’est enregistré.

## 9. Sections d'import

Sections de `import-format.md` (les `flows` sont celles du Core, `import-format.md` §5.6).

**`income_sources`**

| Champ | Type | Remarques |
|---|---|---|
| `id`, `name` | string | |
| `kind` | string | Voir §3.1 |
| `payer_label`, `note` | string? | |
| `ended_on` | date? | Source terminée (2C1) ; les phases en cours après cette date sont coupées à cette date |
| `currency` | string | ISO 4217 |
| `ownership` | `[{ person, share_bp }]` | Le titulaire |
| `visibility` | string | `shared` (défaut) ou `private` |

**`income_phases`**

| Champ | Type | Remarques |
|---|---|---|
| `id` | string | |
| `source` | string | Référence vers `income_sources` |
| `label` | string | |
| `starts_on`, `ends_on?` | date | |
| `net_amount` | montant | > 0 |
| `every` | durée | Mois ou année |
| `gross_amount?` | montant | |
| `taxable_amount?` | montant | Net imposable par versement (2026-10-25, #159) ; > 0, au plus le brut |
| `max_cycles?`, `note?` | | |

**`income_one_off_taxable`** (2026-10-25, #159)

| Champ | Type | Remarques |
|---|---|---|
| `flow_id` | string | `id` d'une entité de `flows` (Core, catégorie `one_off`), résolu comme le fait le Core |
| `taxable_amount` | montant | > 0 |

Appliquée après les `flows` du Core (ordre 92). Les lignes obsolètes des sources du fichier sont balayées. Un `flow_id` inconnu donne `import.unresolved_reference`, un flux qui n'est pas un revenu `import.invalid_value`, une source en lecture seule `import.not_permitted`. Un fichier sans cette section se lit comme avant.

## 10. Contrats (`Income.Contracts`)

| Contrat | Rôle |
|---|---|
| `IIncomeContribution` | Équivalent mensuel, occurrences sur une plage, flux exceptionnels, **par élément** (défini dans les contrats du tableau de bord du Core) |
| `IUpcomingEvents` | Fins de période, `income_gap_soon` |
| `IIncomeSummary` | `Get(scope, asOf?)` → revenus, engagements, part engagée, marge |
| `IProposalHandler<…>` | Voir §8 |
| `IImportSection` | Sections `income_sources`, `income_phases` et `income_one_off_taxable` |

## 11. Validation

**Erreurs** :

- `net_amount` ≤ 0 ;
- `every` autre que mois ou année ;
- `ends_on` ≤ `starts_on` ;
- chevauchement pour une même paire (source, `label`) ;
- devise d'une phase différente de celle de la source ;
- `label` de plus de 60 caractères ;
- `kind` inconnu.

**Avertissements** : `income_gap_soon` ; source sans phase ; phase antérieure à la création de la source (donnée historique).

## 12. Sensibilité et sécurité

Ce sont des données très personnelles : un revenu est plus sensible qu'un abonnement.

| Sujet | Règle |
|---|---|
| **Visibilité** | À la création d'une source, l'interface demande explicitement « Ce revenu est-il visible de l'autre adulte ? ». Défaut : partagé, comme le reste ; privé possible. Le **banc anti-fuite** couvre ces éléments (`identity-and-access/access-tests.md`) |
| **Bulletins de paie** | Un bulletin contient nom, employeur et numéro de sécurité sociale. Le type de document `payslip` est **inéligible au cloud** en v1, sans contournement, **même avec le consentement par dépôt** : traitement local uniquement (`ingestion/llm-providers.md` §5.2) |
| Emails | Sans montants (`security/application.md` §7) |
| MCP et export | Mêmes droits que le reste : un revenu privé n'apparaît pas chez l'autre adulte |
| Minimisation | Aucun identifiant personnel dans `payer_label` ni `note` |

## 13. Exemple

### 13.1 Chiffres (fictifs)

| Source | Composante | Net versé | Équivalent mensuel |
|---|---|---|---|
| Salaire, adulte 1 | Salaire de base | 2 300 € par mois | 2 300 € |
| Salaire, adulte 1 | Prime annuelle | 1 800 € par an | 150 € |
| Salaire, adulte 2 | Salaire de base | 1 900 € par mois | 1 900 € |
| Allocations | Allocations | 130 € par mois | 130 € |
| **Total** | | | **4 480 €** |

Engagements mensuels de 1 250 € :

- part engagée : 1 250 ÷ 4 480 = **27,9 %** ;
- marge avant dépenses courantes : 4 480 − 1 250 = **3 230 €**.

### 13.2 Import

```json
{
  "income_sources": [
    { "id": "inc-salary-1", "name": "Salaire", "kind": "salary",
      "payer_label": "Employeur", "currency": "EUR",
      "ownership": [ { "person": "adult-1", "share_bp": 10000 } ],
      "visibility": "shared" }
  ],
  "income_phases": [
    { "id": "ip-1", "source": "inc-salary-1", "label": "Salaire de base",
      "starts_on": "2024-09-01", "ends_on": "2025-09-01",
      "net_amount": { "amount": "2200.00", "currency": "EUR" },
      "every": { "count": 1, "unit": "month" } },
    { "id": "ip-2", "source": "inc-salary-1", "label": "Salaire de base",
      "starts_on": "2025-09-01",
      "net_amount": { "amount": "2300.00", "currency": "EUR" },
      "every": { "count": 1, "unit": "month" } },
    { "id": "ip-3", "source": "inc-salary-1", "label": "Prime annuelle",
      "starts_on": "2024-12-15",
      "net_amount": { "amount": "1800.00", "currency": "EUR" },
      "every": { "count": 1, "unit": "year" } }
  ]
}
```

## 14. Tests

| Test | Vérifie |
|---|---|
| **Cas doré** (§13.1) | Équivalent mensuel 4 480 €, part engagée 27,9 %, marge 3 230 € |
| Équivalent mensuel | Mensuel, trimestriel (÷ 3), annuel (÷ 12) ; précision pleine, arrondi à l'affichage |
| Occurrences | Ancrage et fin de mois ; `max_cycles` ; date de fin exclue |
| **Changement de période** | Une augmentation ferme et ouvre ; l'équivalent mensuel change à la bonne date |
| Non-chevauchement | Refusé pour (source, `label`) ; composantes parallèles acceptées |
| `income_gap_soon` | Déclenché sans période suivante, absent avec successeur |
| Flux exceptionnel | Compté dans le mois, hors équivalent mensuel |
| **Sans revenu** | Marge « inconnue » |
| Portées | Foyer sans les revenus privés ; Moi avec |
| Répartition par personne | Source à parts différentes |
| Devise | Revenu dans une autre devise converti au taux de la date |
| Import | Aller-retour export puis import identique |

**Livré en 2C1** (2026-10-02, `superpowers/specs/2026-10-02-income-server-design.md`) : module `Nid.Income` (schéma `income`, tables `income_source` et `income_phase`), API `/api/v1/income` (§4 de la conception) : sources (liste, détail, création, modification), phases, changement de phase (`phase-changes`, refusé hors d'une date de paiement de la phase en cours : `income.invalid_phase` sur `effectiveOn`, le message donne les paiements précédent et suivant, car un changement ailleurs déplacerait l'ancrage et compterait un paiement de trop), revenu attendu (`expected?from&to`), revenus exceptionnels (`one-offs`), `close` et `reopen` ; quatre opérations propres au module (créer une source avec ses phases, changer de phase, clôturer, rouvrir) ; codes `income.*` (`unknown_kind`, `unknown_cadence`, `invalid_source`, `invalid_phase`, `phase_overlap`, `source_ended`, `source_open`, `no_running_phase`, `invalid_period`, `phase_conflict`) et `core.*` pour les montants, dates et devises. Les phases sont **modifiables** (ce ne sont pas des faits du Core, contrairement aux flux exceptionnels) ; les routes des phases et des revenus exceptionnels sont **imbriquées sous la source** : le contrôle d'accès est celui de l'élément. Décisions prises à l'exécution :

- **Une augmentation garde la fin de la phase remplacée** : une augmentation pendant un CDD ne le prolonge pas ; à l'intérieur d'une phase plafonnée, la nouvelle phase se termine à la fin du plafond.
- **Une phase avec `max_cycles` et sans `ends_on` se termine après sa dernière occurrence** (sa fin effective) pour l'activité, l'équivalent mensuel, les trous, le chevauchement et la clôture ; `max_cycles` va de 1 à 1200.
- **L'équivalent mensuel est un décimal en pleine précision**, arrondi à 0,01 sur le fil seulement.
- **Une correction ou une suppression de phase exige son `version`** : 422 sans lui, 409 `income.phase_conflict` s'il est périmé.
- **L'import écrit les phases d'une source qui reste terminée** (vérifiées contre `ended_on` et coupées à cette date), à la différence de l'API, pour que l'historique d'une source terminée fasse l'aller-retour ; le revenu exceptionnel s'importe par la section `flows` du Core.

## 15. Limites

- **Net versé seulement** : aucun calcul de fiscalité, de cotisations ni de brut vers net. **Évolution envisagée** à cause de sa complexité (`Project.md` §10).
- **Attendu, pas réel** : rien ne compare ce qui a été reçu à ce qui est prévu (§16).
- Pas de prévision d'augmentations futures.
- Pas de détail de bulletin de paie.
- Pas de budget : la marge est un indicateur, pas une enveloppe à dépenser.
- Une source à paiement hebdomadaire ou bimensuel n'est pas exprimable (mois et année seulement).

## 16. Questions ouvertes

- **Évolution « attendu + réel »** : saisie optionnelle du reçu à la routine, comparée à l'attendu, pour un revenu variable (indépendant, primes). Les flux `income` du Core la permettent déjà.
- Mensualisation d'un revenu irrégulier : moyenne glissante sur 12 mois plutôt que phases ?
- Faut-il un objectif de marge (taux d'épargne cible) avec alerte ?
