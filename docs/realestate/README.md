# Modèle de données du module RealEstate : le bien, la location, les coûts

> Statut : brouillon issu de la discussion technique. Décrit le bien immobilier, ses charges récurrentes prévues, ses baux, et les calculs de valeur nette, de coût réel et de rendement. Les crédits sont dans `loans.md`.
> Références : `../Project.md` §8, `../core-data-model.md` §5 (valorisations et flux), `loans.md`, `../import-format.md`, `../architecture.md` §5.

---

## 1. Objectifs

- Suivre le patrimoine immobilier **en détail** : valeur, coût réel, plus-value latente, rendement locatif.
- S'appuyer sur les **faits datés du Core** (estimations, travaux, taxes, loyers) et sur les crédits liés.
- Rester **brut et économique** : aucune fiscalité, aucun conseil fiscal (`../Project.md` §11).
- Des **estimations manuelles** une à deux fois par an, avec rappel, plutôt qu'une donnée automatique.

## 2. Vue d'ensemble

```
Core                                   RealEstate (schéma realestate)
item (kind = property) ──────────────── property (1:1)
  ├──< valuation   (Core : estimations)   ├──< property_recurring_cost   (prévisions)
  └──< flow        (Core : coûts réels)   └──< property_tenancy          (baux)

item (kind = loan) ── loan.property_item_id ──> property     (voir loans.md)
```

Un bien est un `item` du Core. Un prêt est un autre `item`, lié à un bien par `loan.property_item_id` ; un bien peut avoir plusieurs prêts (principal, prêt à taux zéro, lissage).

## 3. Entité `property`

| Champ | Type | Remarques |
|---|---|---|
| `item_id` | uuid | Clé primaire |
| `property_type` | text | `primary_residence`, `secondary_residence`, `rental`, `parking`, `land`, `other` |
| `purchase_date` | date | Non future |
| `purchase_price` | numeric(19,4) | > 0, dans la devise de l'élément |
| `notary_fees`, `agency_fees`, `other_acquisition_costs` | numeric(19,4) | 0 par défaut |
| `surface_m2` | numeric? | |
| `location_label` | text? | Commune ou libellé. **Pas d'adresse complète** : une donnée personnelle inutile |
| `valuation_cadence` | text | `yearly` par défaut, péremption à 400 jours |
| `stale_after_days` | int? | Remplace le seuil dérivé de la cadence |
| `rental_starts_on` | date? | Début de mise en location, sert à la vacance ; ≥ `purchase_date` |
| `note` | text? | |

Les **estimations** sont des valorisations du Core (une à deux fois par an, avec rappel, note « estimation en ligne » ou autre). L'interface propose des liens vers des estimateurs en ligne ; aucune donnée n'y est envoyée. Les données publiques de transactions restent à explorer plus tard (`../Project.md` §13).

**Livré en 3C1** (2026-10-13, `superpowers/specs/2026-10-13-properties-server-design.md`) : table `realestate.property` (1:1 avec l’élément du Core `realestate` / `property`) et colonne `loan.property_item_id` (`loans.md` §3) ; un bien n’est ni vendu, ni supprimé, ni archivé à cette étape. Ses estimations et ses flux sont ceux du Core ; un flux de bien est `expense` ou `income` et porte une catégorie **obligatoire** de la convention du §4 (`realestate.invalid_category` sinon). API `/api/v1/realestate` :

- `GET /properties?scope=` : par bien, le type, la devise, la visibilité (depuis 3D1), la valeur du jour (dernière estimation, sa date, périmée ou non) et la valeur nette (`null` sans estimation) ;
- `GET /properties/{id}` : les champs du bien et sa `version`, les propriétaires, la visibilité, les indicateurs du jour (§7 : valeur, prix de revient, plus-value latente brute, capital restant dû des prêts liés, valeur nette, coût réel et ses composantes, coût net des loyers), les avertissements, toutes les estimations et tous les flux par date, et les prêts liés **que la personne peut lire**, avec leur capital restant dû et sa conversion dans la devise du bien ;
- `POST /properties` et `PATCH /properties/{id}` (avec la `version` lue ; propriétaires, visibilité et devise sont fixés à la création ; une version périmée répond `409 realestate.property_conflict`) ;
- `POST` et `DELETE` sur `/properties/{id}/valuations`, `POST`, `PUT` et `DELETE` sur `/properties/{id}/flows`.

Un prêt lié que la personne ne peut pas lire n’apparaît nulle part : ni son identifiant, ni son montant, ni leur nombre ; il ne compte ni dans la valeur nette ni dans le coût réel qu’elle voit. Un prêt dans une autre devise est converti au taux du jour (`loan_other_currency`) ; sans taux, il est laissé de côté (`fx_missing`). Composantes du coût réel : `acquisition`, `loan_interest`, `loan_insurance`, `loan_fees`, puis une ligne par catégorie de dépense. Avertissements : `no_valuation`, `valuation_stale` (400 jours en cadence `yearly`, jamais en `manual`, sauf `stale_after_days`), `loan_other_currency`, `fx_missing`, `visibility_mismatch`. Montants en chaînes décimales invariantes (`"290000.00"`), deux décimales au plus, sous 10¹² ; les montants des estimations et des flux sont eux aussi des chaînes décimales invariantes, dans la devise du bien (pas d’objet `{ amount, currency }`) ; chaque refus nomme son champ (`realestate.invalid_property_field`, `realestate.invalid_category`). Charges récurrentes, baux, loyer attendu, impayés, vacance et rendements sont livrés en 3C2 (§5 à §7).

## 4. Catégories de flux

Convention du module pour le champ `category` des flux du Core :

| `kind` | `category` | Effet |
|---|---|---|
| `expense` | `works` | Entre dans le **prix de revient** et dans le coût réel |
| `expense` | `property_tax`, `condo_fees`, `insurance`, `energy`, `maintenance`, `other` | Entrent dans le coût réel |
| `income` | `rent`, `other_income` | Hors coût réel ; servent au coût net et au rendement |

## 5. Charges récurrentes prévues : `property_recurring_cost`

Ce sont des **prévisions**. Elles alimentent les engagements du tableau de bord et les échéances à venir. Les flux réels restent la vérité comptable.

| Champ | Type | Remarques |
|---|---|---|
| `id`, `property_item_id` | uuid | |
| `category` | text | `property_tax`, `condo_fees`, `insurance`, `energy`, `maintenance`, `other` |
| `amount` | numeric(19,4) | > 0 |
| `every` | durée | **Mois ou année seulement** |
| `starts_on`, `ends_on?` | date | Intervalle semi-ouvert |
| `note` | text? | |

- **Équivalent mensuel exact** : `amount ÷ (count × 12 si l'unité est l'année)`, arrondi à 0,01 à l'affichage.
- **Prochaine échéance** : `starts_on` + k périodes (arithmétique de mois du Core).
- **Routine mensuelle** : pour chaque charge attendue dans le mois, l'interface propose un flux `expense` **prérempli à confirmer** (livré en 3E2, voir §6).

## 6. Baux : `property_tenancy`

| Champ | Type | Remarques |
|---|---|---|
| `id`, `property_item_id` | uuid | |
| `label` | text | Libellé libre. **Ne pas y mettre de donnée personnelle** : une colocation est un seul bail au loyer total |
| `starts_on`, `ends_on?` | date | Intervalle semi-ouvert ; **pas de chevauchement** sur un même bien ; `starts_on` ≥ `purchase_date` |
| `rent_monthly` | numeric(19,4) | > 0, loyer prévu |
| `charges_monthly` | numeric(19,4) | Provisions de charges prévues, 0 par défaut |
| `deposit` | numeric(19,4)? | Dépôt de garantie |
| `note` | text? | |

Calculs indicatifs :

- **Loyer attendu** : mois complet couvert par le bail = `rent_monthly + charges_monthly` ; mois partiel = proratisé au nombre de jours du mois, arrondi au centime (demi supérieur).
- **Loyer réel** : flux `income` de catégorie `rent` ; la routine mensuelle propose chaque mois le loyer attendu de chaque bail, à confirmer comme tel (3E2).
- **Impayés** = loyer attendu − loyer réel, affichés à titre d'information.
- **Vacance** : jours sans bail entre le début de location R et la date d, sur les 12 mois glissants : de max(R, d − 12 mois + 1 jour) à d. R est la plus ancienne date entre `rental_starts_on` et le début du premier bail ; sans l’une ni l’autre, ou si R est après d, la vacance n’est pas calculée. **Taux de vacance** = jours vacants ÷ jours de la période.

**Livré en 3C2** (2026-10-13, `superpowers/specs/2026-10-13-property-rental-design.md`) : tables `realestate.property_recurring_cost` et `realestate.property_tenancy`, chaque ligne avec sa `version`. API sous `/api/v1/realestate/properties/{id}` :

- `POST /recurring-costs`, `PUT /recurring-costs/{costId}` (avec la `version` lue ; une version périmée répond `409 realestate.recurring_cost_conflict`), `DELETE /recurring-costs/{costId}?version=` ; corps `{ category, amount, every: { count, unit }, startsOn, endsOn?, note? }`, `unit` valant `month` ou `year`, `count` de 1 à 120 ;
- `POST /tenancies`, `PUT /tenancies/{tenancyId}` (`409 realestate.tenancy_conflict`), `DELETE /tenancies/{tenancyId}?version=` ; corps `{ label, startsOn, endsOn?, rentMonthly, chargesMonthly?, deposit?, note? }` ; terminer un bail, c’est le corriger avec un `endsOn` ;
- `GET /properties/{id}` ajoute `recurringCosts` (avec `monthlyEquivalent` et `nextDueOn`, `null` une fois la charge terminée) et `tenancies` (avec `current`), par date de début, et dans `figures` : `rentedSince`, `expectedRent`, `rentReceived`, `arrears`, `vacantDays`, `vacancyRate`, `grossYield`, `netYieldBeforeTax` ; `GET /properties` ajoute à chaque ligne `grossYield` et `vacancyRate`.

Une charge récurrente ne crée aucun flux. Deux baux d’un même bien ne se chevauchent jamais (`realestate.tenancy_overlap` sur `startsOn`) : chaque écriture de bail verrouille la ligne du bien avant de vérifier. Un `PATCH` du bien refuse une date d’achat postérieure au début d’un de ses baux (`purchaseDate`). Une charge de catégorie `works` est refusée (`realestate.invalid_category`) : les travaux sont déjà dans le prix de revient. Avertissement `rental_without_tenancy` : un bien de type `rental` sans bail ni `rental_starts_on`.

**Livré en 3E2** (`superpowers/specs/2026-10-15-realestate-routine-design.md`) : `GET /api/v1/realestate/routine?on=` (par défaut le dernier jour du mois précédent, jamais après aujourd’hui ; M est le mois de `on`) propose, pour les biens visibles dans « Moi » que le lecteur peut modifier, une estimation par bien (due sans estimation au plus tard à `on`, ou quand la dernière est périmée à `on`), chaque charge prévue dont une échéance tombe dans M, et le loyer attendu de M de chaque bail qui en couvre au moins un jour (loyer + charges, proratisé comme les impayés, daté du premier jour couvert), chacun avec ce qui est déjà saisi dans M pour la même nature et la même catégorie ; lectures par lots. `POST /api/v1/realestate/routine` (`{ on, estimates: [{ property, amount }], flows: [{ property, kind, category, occurredOn, amount }] }`) enregistre tout ou rien, dans la devise du bien : une dépense d’une catégorie de charge prévue ou un revenu `rent`, daté dans M et au plus tard aujourd’hui ; une estimation déjà présente à `on` est refusée (`realestate.routine_value_exists` : la corriger depuis la fiche), un bien répété dans les estimations aussi (`realestate.routine_duplicate` ; les lignes ne sont pas dédupliquées), une routine vide aussi (`realestate.routine_empty`) ; un bien que le lecteur ne peut pas modifier répond « introuvable ». La réponse récapitule, bien par bien, les faits créés ; `POST /api/v1/realestate/routine/undo` les annule exactement (`Void`, historique conservé).

## 7. Calculs

Fonctions pures, décimal exact, à la date d (par défaut aujourd'hui).

| Métrique | Définition |
|---|---|
| **Valeur nette** | valeur(d) − Σ capital restant dû(d) des prêts liés (`loans.md` §8), avec l'indicateur de péremption de la valeur |
| **Prix de revient** (économique, brut) | prix d'achat + frais de notaire + frais d'agence + autres frais + Σ `works` jusqu'à d |
| **Plus-value latente brute** | valeur(d) − prix de revient. **Pas de fiscalité** |
| **Coût réel** | prix d'achat + frais d'acquisition + Σ intérêts payés + Σ assurance emprunteur payée + Σ frais de prêt + Σ `expense`, jusqu'à d. Le **capital remboursé n'est pas un coût** : il constitue du patrimoine. Présenté avec le **détail par composante** |
| **Coût net des loyers** | coût réel − Σ `rent` |
| **Rendement brut** | loyers des 12 derniers mois ÷ prix de revient |
| **Rendement net avant impôt** | (loyers − charges) des 12 derniers mois ÷ prix de revient |

- Les rendements sont « non significatifs » si le bien n'est pas loué depuis au moins 12 mois.
- Précisé en 3C2 : les 12 mois glissants sont ]d − 12 mois, d] (365 ou 366 jours) ; les rendements sont non significatifs (`null`) quand R est inconnu ou postérieur à d − 12 mois ; le rendement net déduit les dépenses du bien sur la période, hors `works` (déjà dans le prix de revient), jamais les coûts du prêt ; rendements et taux de vacance sont des fractions écrites avec quatre décimales (`"0.0384"`).
- Le prix de revient est une notion **économique** : il diffère de la base fiscale d'une plus-value immobilière.

## 8. Intégration au foyer

| Sujet | Règle |
|---|---|
| **Patrimoine** | Chaque `item` contribue **séparément, sans netting** : le bien pour +valeur, chaque prêt pour −capital restant dû. La répartition par personne reste juste quand les parts du bien et du prêt diffèrent (`../core-data-model.md` §4.3). La « valeur nette du bien » est une vue calculée à l'écran |
| Visibilité | Avertissement **`visibility_mismatch`** : un prêt privé lié à un bien partagé n'est pas déduit du total du foyer |
| **Engagements** | Mensualités et assurance des prêts actifs à d (`loans.md` §9), plus l'équivalent mensuel des charges récurrentes prévues |
| **À venir** | Valorisation due, fin d'une phase de crédit (**changement de taux ou fin de différé**), fin d'un crédit, prochaine échéance d'une charge récurrente, fin de bail |
| Devises | Si le bien et un prêt lié n'ont pas la même devise, le capital restant dû est converti au taux de la date (`../core-data-model.md` §8) |

**Livré en 3E1** (2026-10-14, `superpowers/specs/2026-10-14-realestate-dashboard-design.md`) : le tableau de bord compte chaque bien pour sa dernière estimation (« Sans valeur » sans estimation, « À mettre à jour » au-delà de son seuil) et chaque crédit pour − son capital restant dû, une ligne chacun dans la répartition, au prorata de ses propres parts ; un crédit soldé ou pas encore débloqué n’y figure pas ; un crédit privé n’entre que dans « Moi ». Depuis le 1er janvier, la variation d’estimation d’un bien est un gain et le capital remboursé d’un crédit un versement ; seuls comptent un bien estimé récemment au 1er janvier et un crédit débloqué à cette date. La courbe compte chaque estimation depuis sa date et chaque crédit depuis son déblocage, son capital remboursé dans les versements cumulés. Engagements : mensualité et assurance de l’échéance du mois de chaque crédit en cours (rien sans échéance ce mois-ci), équivalent mensuel des charges prévues en cours, détaillés « Abonnements · Crédits · Charges des biens ». « À venir » sur 60 jours : fin de phase d’un crédit (à la première échéance de la phase suivante), dernière échéance, estimation à refaire (le jour où elle devient périmée), prochaine échéance de chaque charge prévue avec son montant, fin de bail. Les estimations et charges prévues dans la routine viennent en 3E2.

**Ajouté avec le tableau de bord des biens, PR 1** (`superpowers/specs/2026-10-22-properties-dashboard-design.md` §3) : la route `GET /api/v1/realestate/properties/overview?scope=&span=12m|3y|all` (`3y` par défaut) : les chiffres du foyer dans la devise de référence (valeur des biens, crédits restants des crédits liés, valeur nette, prix de revient et plus-value latente, loyers reçus sur 12 mois et rendement brut sur le prix de revient des biens au rendement significatif), la répartition par type et par personne (chaque bien et chaque crédit selon ses propres parts), les biens avec les chiffres de leur fiche, la courbe (estimations depuis leur date, crédits depuis leur déblocage) et « À surveiller » (avertissements de la fiche sauf `loan_other_currency`, `rent_arrears`, et sur 60 jours `property_cost_due` et `tenancy_end`). Un crédit compte s'il est lié à un bien et visible dans la portée : dans « Foyer », un crédit privé n'est pas déduit et `visibility_mismatch` le nomme ; un crédit lié à aucun bien n'est pas sur cette page. Chaque montant est converti seul et arrondi au centime ; un taux manquant rend le chiffre `null` avec `core.fx_rate_missing`.

## 9. Propositions

| `kind` | Payload | Application |
|---|---|---|
| `realestate.record_valuation` | `{ as_of, value: { amount, currency }, note? }` sur un bien cible | `IValuations.Record` |
| `realestate.record_flow` | `{ occurred_on, kind, amount, category?, external_ref?, note? }` sur un bien cible (travaux, taxes, loyers) | `IFlows.Record` |

Les propositions liées aux crédits sont dans `loans.md` §10.

## 10. Sections d'import

Sections de `../import-format.md` définies par ce module (les `valuations` et `flows` sont celles du Core, `../import-format.md` §5.6).

**`properties`**

| Champ | Type | Remarques |
|---|---|---|
| `id`, `name` | string | |
| `property_type` | string | Voir §3 |
| `currency` | string | ISO 4217 |
| `purchase_date` | date | |
| `purchase_price` | montant | |
| `notary_fees`, `agency_fees`, `other_acquisition_costs` | montant? | |
| `surface_m2`, `location_label`, `note` | | |
| `valuation_cadence`, `stale_after_days`, `rental_starts_on` | | |
| `ownership`, `visibility` | | Comme pour les abonnements |

**`property_recurring_costs`** : `id`, `property` (référence), `category`, `amount` (montant), `every`, `starts_on`, `ends_on?`, `note?`.

**`tenancies`** : `id`, `property` (référence), `label`, `starts_on`, `ends_on?`, `rent_monthly` (montant), `charges_monthly?` (montant), `deposit?` (montant), `note?`.

**Livré en 3F1** (`superpowers/specs/2026-10-15-realestate-import-export-design.md`) : les trois sections ci-dessus. Montants `{ amount, currency }` dans la devise du bien ; `notary_fees`, `agency_fees`, `other_acquisition_costs` et `charges_monthly` valent 0 s’ils manquent, `valuation_cadence` `yearly`, `visibility` `shared` ; `surface_m2` est une chaîne décimale ; `every` vaut `{ count, unit }`, de 1 à 120 mois ou années. Une charge ou un bail nomme un bien du même fichier. Les erreurs du §11 s’appliquent à ce que le bien contient une fois l’import écrit ; un bien dont le fichier recule l’achat après un bail qu’il garde est refusé. Un flux de bien (section `flows` du Core) respecte la convention du §4 : `expense` ou `income`, avec une catégorie de sa nature (`realestate.invalid_category` sinon). L’export écrit chaque bien lisible, ses charges prévues et ses baux.

## 11. Validation

**Erreurs** : `purchase_price` ≤ 0 ; `purchase_date` future ; `rental_starts_on` < `purchase_date` ; frais < 0 ; baux qui se chevauchent ; bail avant l'achat ; unité d'une charge récurrente autre que mois ou année ; montants ≤ 0.

**Avertissements** : valeur périmée ; `visibility_mismatch` ; prêt lié dans une autre devise ; bien de type `rental` sans bail ni `rental_starts_on`.

## 12. Exemple (chiffres fictifs)

Achat de 250 000 € + notaire 19 000 € + travaux 12 000 € (`works`).

| Métrique | Résultat |
|---|---|
| Prix de revient | 250 000 + 19 000 + 12 000 = **281 000 €** |
| Estimation | 290 000 € |
| **Plus-value latente brute** | 290 000 − 281 000 = **+9 000 €** |
| Capital restant dû des prêts liés | 150 000 € |
| **Valeur nette** | 290 000 − 150 000 = **140 000 €** |
| Loyer 900 € par mois | 10 800 € par an → rendement brut 10 800 ÷ 281 000 = **3,8 %** |

## 13. Tests

| Test | Vérifie |
|---|---|
| Cas doré (§12) | Prix de revient, plus-value, valeur nette, rendement |
| Vacance | Jours vacants et taux ; mois partiel proratisé |
| Baux | Chevauchement refusé |
| Charge récurrente | Équivalent mensuel d'une charge annuelle ; prochaine échéance |
| Valeur nette | Deux prêts (principal et prêt à taux zéro) |
| Répartition par personne | Parts du bien et du prêt différentes |
| `visibility_mismatch` | Avertissement émis |
| Coût réel | Le capital remboursé n'est pas compté ; composantes présentes |

## 14. Limites

- Aucune **fiscalité** : plus-value, revenus fonciers, régimes locatifs. **Évolution envisagée** à cause de sa complexité (`../Project.md` §10) ; la plus-value fiscale n'est pas notre prix de revient économique.
- Pas d'**adresse** ni de données personnelles de locataire.
- Pas de gestion détaillée de copropriété.
- Estimations **manuelles** ; les données publiques de transactions sont à explorer plus tard.
- Pas de suivi des paiements réels d'un prêt (`loans.md` §13).

## 15. Questions ouvertes

- ~~Faut-il lier une charge récurrente à la génération automatique de flux prévus, ou seulement à la routine mensuelle ?~~ **Tranché en 3C1 (2026-10-13) : la routine seulement.** Une charge récurrente donne sa prochaine échéance et son équivalent mensuel ; la routine (3E) propose un flux prérempli à confirmer ; les flux réels restent la vérité comptable.
- ~~Rendement locatif : fenêtre de 12 mois glissants ou année civile ?~~ **Tranché en 3C1 (2026-10-13) : les 12 mois glissants** avant la date.
- ~~Représentation d’une résidence principale partiellement louée (colocation, location meublée d’une pièce).~~ **Tranché en 3C1 (2026-10-13) : un bail sur le bien, sans proratisation** des charges entre partie louée et partie occupée ; tout type de bien peut porter des baux.
