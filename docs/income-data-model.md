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
| `max_cycles` | int? | Nombre maximal d'occurrences |
| `note` | text? | |

- **Net versé** = montant reçu sur le compte, **après cotisations sociales et prélèvement à la source**, avant tout autre impôt. Aucun calcul fiscal (`Project.md` §11).
- **Une augmentation ferme la période courante et en ouvre une nouvelle** : l'historique des changements est conservé.
- **Pas de chevauchement** pour une même paire (`source_item_id`, `label`).

### 3.3 Revenus exceptionnels

Prime ponctuelle, remboursement, indemnité : un `flow` du Core avec `kind = income` et `category = one_off`, posé sur la source (`core-data-model.md` §5.3). Il compte dans les revenus du mois où il tombe (§6) et prépare l'évolution vers « attendu + réel » (§14).

## 4. Occurrences et équivalents

Fonctions pures dans `Domain`, **décimal exact**.

**Occurrences attendues** d'une phase : `starts_on` + k × `every` (arithmétique de mois du Core, jour d'ancrage écrasé en fin de mois), tant que la date est avant `ends_on` et que k < `max_cycles`. C'est la vue **trésorerie**, utilisée pour « à venir ».

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
| **RealEstate** | **Loyer prévu** des baux en cours (`rent_monthly`), **sans** les provisions de charges (`realestate/README.md` §6) |
| **Investments** | Revenus du patrimoine réellement **encaissés** sur 12 mois (flux `income`), affichés **à part**, hors équivalent mensuel |

- Chaque contribution est émise **par élément**, pour que les droits et la répartition par personne soient justes.
- Les montants d'une autre devise sont convertis au taux de la date (`core-data-model.md` §8).
- Une source dont la date de fin est passée n'apporte plus rien à d.

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
| `income.change_phase` | `{ label, effective_on, net_amount: { amount, currency }, every?, gross_amount? }` sur une source cible | **Ferme** la période courante de ce `label` à `effective_on` et **ouvre** la suivante |
| `income.record_one_off` | `{ occurred_on, amount: { amount, currency }, note? }` sur une source cible | `IFlows.Record` (`income`, `one_off`) |

Clé de dédoublonnage (`subject_key`) : `label` et `effective_on`, ou la date du flux.

## 9. Sections d'import

Sections de `import-format.md` (les `flows` sont celles du Core, `import-format.md` §5.6).

**`income_sources`**

| Champ | Type | Remarques |
|---|---|---|
| `id`, `name` | string | |
| `kind` | string | Voir §3.1 |
| `payer_label`, `note` | string? | |
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
| `max_cycles?`, `note?` | | |

## 10. Contrats (`Income.Contracts`)

| Contrat | Rôle |
|---|---|
| `IIncomeContribution` | Équivalent mensuel, occurrences sur une plage, flux exceptionnels, **par élément** (défini dans les contrats du tableau de bord du Core) |
| `IUpcomingEvents` | Fins de période, `income_gap_soon` |
| `IIncomeSummary` | `Get(scope, asOf?)` → revenus, engagements, part engagée, marge |
| `IProposalHandler<…>` | Voir §8 |
| `IImportSection` | Sections `income_sources` et `income_phases` |

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
