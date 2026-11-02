# Modèle de données du module Investments

> Statut : brouillon issu de la discussion technique. Décrit l'enveloppe, la façon dont le module interprète les valorisations et les flux du Core, et ses calculs (gain, performance d'une période, rendement annualisé).
> Références : `Project.md` §7, `core-data-model.md` §5 (valorisations et flux), `import-format.md`, `architecture.md` §5.

---

## 1. Objectifs

- Savoir **ce qui rapporte et ce qui ne rapporte pas**, en gain et en perte, sans la complexité d'un suivi de portefeuille ligne par ligne.
- S'appuyer **sur des faits datés du Core** : une valorisation (« le PEA vaut 25 000 € au 31/12 ») et des flux (versements, retraits). Le module ne stocke aucun fait de plus, **sauf le détail par support** (§16), exception assumée : stocké dans le schéma `investments`, il décompose une enveloppe sans jamais changer ses chiffres.
- Rester **brut** : les gains s'affichent **avant fiscalité et prélèvements sociaux** (`Project.md` §13).
- Calculer de façon **déterministe** : fonctions pures en décimal exact, jamais d'IA.

## 2. Vue d'ensemble

```
Core                                      Investments (schéma investments)
item (module = investments, kind = envelope) ──── envelope (1:1)
  ├─ ownership, visibility, currency, archived_at
  ├──< valuation   (Core)                   ├──< holding            (optionnel, §16)
  └──< flow        (Core)                   └──< holding_snapshot ──< holding_snapshot_line
```

Une **enveloppe** est un `item` du Core. Son nom, ses propriétaires, sa visibilité, sa devise et son archivage viennent du Core ; les faits (valorisations, flux) aussi. Le module ajoute des attributs descriptifs et les calculs.

## 3. Entité `envelope`

| Champ | Type | Remarques |
|---|---|---|
| `item_id` | uuid | Clé primaire, référence l'`item` du Core |
| `kind` | text | `checking` (compte courant), `savings` (livret), `pea`, `life_insurance`, `per`, `brokerage` (compte-titres), `tontine` (association tontinière), `other`. Détermine la couleur fixe du graphique (`frontend/dataviz.md` §5) |
| `institution` | text? | Établissement, texte libre |
| `account_hint` | text? | **Quatre derniers caractères** au plus, pour reconnaître le compte. Jamais le numéro complet |
| `opened_on` | date? | |
| `valuation_cadence` | text | `monthly` (défaut), `quarterly`, `yearly`, `manual` |
| `stale_after_days` | int? | Remplace le seuil dérivé de la cadence |
| `note` | text? | |
| `premiums_before_2017` | numeric(19,4)? | `life_insurance` ou `tontine` seulement, `>= 0` : les primes versées avant le 27/09/2017 restant dans le contrat quand Nid l'a suivi ; sert à estimer l'impôt d'un rachat |

Colonnes de provenance et d'audit : celles du Core.

## 4. Sens des faits du Core pour une enveloppe

| Fait | Usage |
|---|---|
| `flow` `opening` | Total net des versements **antérieurs** au début du suivi, daté du jour de la première valorisation. Au plus un courant |
| `flow` `contribution` | Versement (argent apporté de l'extérieur) |
| `flow` `withdrawal` | Retrait vers l'extérieur. **Une distribution qui sort de l'enveloppe** (dividende versé sur le compte bancaire) est un retrait. Sa `category` peut le classer en revenu passif : `dividend`, `interest` ou `distribution` (C2), réservé à l'argent versé directement hors de l'enveloppe ; un retrait ordinaire d'un PEA reste sans catégorie |
| `flow` `expense` | Frais **payés hors de l'enveloppe**. Les frais prélevés dans l'enveloppe sont déjà dans la valeur |
| `flow` `income` | Revenu perçu hors de l'enveloppe. Affiché à part, **hors du gain**. Même classement possible par `category` (C2) |
| `valuation` | Valeur de l'enveloppe entière à une date |

**Virement interne** (livret vers PEA) : un `withdrawal` sur l'un et un `contribution` sur l'autre, avec `category = transfer` et le **même** `external_ref = transfer:<identifiant>` sur les deux éléments (l'unicité de `external_ref` est par élément). Le total du foyer n'est pas modifié.

**Catégorie de revenu (C2, C2b)** : `dividend`, `interest` et `distribution` ne sont permises que sur un `withdrawal` ou un `income` d'une enveloppe qui n'est pas un `savings` (livret) ; ailleurs l'API refuse avec `investments.invalid_income_category` (chemin `category`) ; l'import n'applique que la règle « sur un `withdrawal` ou un `income` » (`import-format.md`), le refus d'un livret est propre à l'API. Une catégorie sans rapport (`import`, par exemple) reste libre. Un classé ne change ni le gain, ni le TRI, ni le versé net. Une jambe de virement (`category = transfer`) ne peut pas être corrigée vers une autre catégorie : `investments.transfer_leg`.

**Clôture** : dernier retrait, valorisation à 0, puis archivage de l'élément.

## 5. Calculs

Fonctions pures dans `Domain`, montants en décimal exact, à la date d (par défaut aujourd'hui).

| Métrique | Définition |
|---|---|
| Valeur, `as_of` | Dernière valorisation courante à la date d (`IValuations.Latest`) ; `v` est sa date. **Inconnue** si aucune valorisation n'existe avant d |
| **Versements nets** à `v` | `opening` + Σ `contribution` − Σ `withdrawal`, flux courants datés au plus tard `v` (un flux daté du jour de la valorisation est inclus) |
| **Gain** à `v` | valeur(`v`) − versements nets(`v`) |
| **Gain en %** | gain ÷ total versé (`opening` + Σ `contribution` au plus tard `v`). Les retraits n'entrent pas au dénominateur : rapporté aux versements nets, le pourcentage montait à chaque rachat (issue #20). **Indéfini** (« n/a ») si rien n'a été versé. Arrondi à 0,1 % (demi s'éloignant de zéro) par le serveur |
| **Depuis la dernière valeur** | Versements et retraits datés **après** `v`, affichés à part. Un versement postérieur à la dernière valorisation ne fausse jamais le gain |
| **Performance d'une période** [a, b] | variation de valeur − versements nets de la période, entre les valorisations `va` et `vb`. **Additive** : la performance sur [a, c] est la somme des performances sur [a, b] et [b, c] |
| Frais payés hors enveloppe | Σ `expense` jusqu'à `v` |
| **Gain après frais** | gain − Σ `expense` |
| Revenus hors enveloppe | Σ `income` jusqu'à `v`, à part |

Règles :

- La date de référence d'un gain est **`v`**, pas d : on ne mélange pas une valeur ancienne et des flux récents.
- **Devise étrangère** : le gain se calcule dans la devise de l'élément ; seuls les totaux sont convertis (`core-data-model.md` §5.5).
- Avertissement **`missing_opening`** : valorisation non nulle, aucun flux `opening` et aucun versement. Le gain serait égal à la valeur, ce qui est trompeur.

**Livré en 2B1a** (2026-09-29, `superpowers/specs/2026-09-29-investments-server-design.md`) : module `Nid.Investments`, table `investments.envelope`, API `/api/v1/investments` (enveloppes, valeurs, flux, virements internes, clôture et réouverture, performance d'une période). Le gain en % est arrondi par le serveur à 0,1 (demi s'éloignant de zéro). La date de l'`opening` n'est pas imposée : seul l'avertissement `missing_opening` guide. Codes : `investments.unknown_kind`, `investments.unknown_cadence`, `investments.invalid_envelope`, `investments.envelope_closed`, `investments.envelope_open`, `investments.transfer_same_envelope`, `investments.transfer_currency_mismatch`, `investments.invalid_period`. L'import et l'export (§9) sont livrés en 2B1b.

**Livré en 2B1b** (2026-09-30) : sections `envelopes`, `valuations`, `flows`, export et aller-retour.

**Détail par support** (§16) : la valorisation de l'enveloppe reste la source de vérité de tous les chiffres ci-dessus ; une enveloppe sans support se calcule exactement comme avant.

**Ajouté en 2B2b** (2026-09-30) : la route `GET /api/v1/investments/envelopes/{id}/series` (courbe : valeur et versements nets dans le temps) et le champ `canWrite` dans chaque élément de la liste des enveloppes.

**Ajouté avec le tableau de bord des placements, PR 1** (`superpowers/specs/2026-10-22-investments-dashboard-design.md` §3) : la route `GET /api/v1/investments/overview?scope=&span=12m|3y|all` : les chiffres du foyer dans la devise de référence (valeur, versé net, versé hors virements, gain et gain en %, rendement annuel du foyer, depuis le 1er janvier), la répartition par type et par personne, les enveloppes ouvertes, la courbe par type et « À surveiller ». Un virement interne n'entre ni dans le versé du foyer ni au dénominateur de son gain en % ; le rendement annuel du foyer est le TRI de la réunion des flux de ses enveloppes, convertis au taux du jour ; chaque montant est converti seul et arrondi au centime.

**Ajouté avec le tableau de bord des placements, PR 2** (même spec, §4) : la route `GET /api/v1/investments/overview/performance?scope=&from=&to=` (`from` < `to` ≤ aujourd'hui, sinon 400 `investments.invalid_period`) et `firstAsOf` dans la vue d'ensemble. Une enveloppe compte dans la période si elle a une valeur au début, pas plus ancienne que son seuil de péremption ; les autres sont nommées (`no_start_value`, `stale_start_value`). La variation de valeur se décompose en versements, retraits, virements avec une enveloppe hors du calcul et performance ; chaque mois est une performance de période, si bien que les mois s'additionnent. Les montants sont convertis au taux de la fin de la période. La référence des rendements annuels est le TRI des livrets réunis.

**Ajouté avec le tableau de bord des placements, PR 3** (même spec, §5) : la route `GET /api/v1/investments/overview/holdings?scope=` : les supports du relevé le plus récent de chaque enveloppe ouverte, réunis. Le détaillé, le non-réparti (valeur de l'enveloppe − total de son relevé) et les enveloppes sans relevé redonnent la valeur de la vue d'ensemble. Un support détenu dans plusieurs enveloppes se reconnaît à son ISIN. Les frais sont une estimation de l'année précédente (`HoldingMath.ImplicitCost`), les supports sans rapport comptés à part (les liquidités exclues). La part en unités de compte du foyer réunit les assurances-vie et les PER. « À surveiller » : échéances à 60 jours, parts inexpliquées et `holding_value_mismatch` du dernier relevé, plancher UC. Supports, relevés et mouvements sont lus en trois requêtes pour toutes les enveloppes (celles de l'export) ; les rapports de frais, une par enveloppe détaillée.

**Ajouté avec les revenus du capital** (`superpowers/specs/2026-11-02-passive-income-design.md` §3.2) : la route `GET /api/v1/investments/overview/income?scope=` : les revenus des 12 derniers mois (`from` = le 1er du mois de la fenêtre, `to` = aujourd'hui), par nature (dividende, intérêt, distribution, loyer, autre), par mois (le courant est partiel) et par ligne (enveloppe ou bien). Trois sources : les retraits et les flux `income` classés d'une enveloppe hors livret (un `income` sans catégorie compte comme `other`) et ses mouvements d'intérêt ou de revenu ; la performance mensuelle d'un livret saisi à la main (exclu, avec son motif, s'il est relié à la banque ou sans versement) ; le loyer net des biens loués, lu par `IPropertyIncome`. Chaque montant (ligne, mois, nature) est converti seul au taux du jour : les totaux additionnent les montants affichés, et sont nuls quand un taux manque. Les enveloppes closes comptent avant leur clôture. Le rendement est le revenu divisé par la valeur moyenne en fin de mois des lignes qui ont produit un revenu ; le rendement global se calcule sur les lignes valorisées seulement. L'estimation des 12 prochains mois est la somme de la fenêtre des sources encore ouvertes aujourd'hui (enveloppe non close, bien encore loué). Outil MCP `get_passive_income` (issue #277) : la même réponse, pour le foyer ou « Moi ».

## 6. Rendement annualisé (TRI), étape 6

Sert à comparer honnêtement un livret, une assurance-vie et un PEA en tenant compte du moment des versements.

- **Flux** : `opening` et `contribution` négatifs, `withdrawal` positifs, valeur finale positive à `v`. Base **Actual/365**.
- **Résolution** : bissection sur ]−99,99 %, +10 000 %], tolérance 10⁻⁹ sur le taux, 100 itérations au plus ; arrondi par le serveur à 0,1 %. Pas de changement de signe de la VAN sur l'intervalle → non significatif.
- **Livré** avec l'issue #20 : `annual_return_pct` dans les chiffres d'une enveloppe (`annualReturnPct` sur le fil), affiché « Rendement annuel » sur sa fiche. Le solveur est `Xirr.AnnualPercent`, partagé avec les supports (§16).
- **Non significatif** si tous les flux ont le même signe ou si la période couvre moins de 12 mois.
- **Seule exception aux montants décimaux** : la puissance non entière impose un calcul en flottant. Il reste **à la frontière du calcul** : les montants restent décimaux, le taux est reconverti en décimal.

## 7. Cadence, routine et anomalies

**Cadence et péremption** (`valuation_cadence`) :

| Cadence | Seuil de péremption par défaut | Dans la routine mensuelle |
|---|---|---|
| `monthly` | 45 jours | Chaque mois |
| `quarterly` | 120 jours | Tous les trois mois |
| `yearly` | 400 jours | Une fois par an |
| `manual` | Aucun | Jamais |

`stale_after_days` remplace le seuil. La routine préremplit la valeur du mois précédent (`../frontend/screens.md` §5).

**Anomalie `large_change`** : une proposition de valorisation est signalée quand |valeur proposée − valeur précédente − flux nets| ÷ valeur précédente dépasse **30 %** (configurable). Elle s'ajoute aux contrôles de `security/ai-ingestion.md` §2.

## 8. Propositions

Le module implémente `IProposalHandler` pour :

| `kind` | Payload | Application |
|---|---|---|
| `investments.record_valuation` | `{ as_of, value: { amount, currency }, note? }` sur une enveloppe cible | `IValuations.Record` |
| `investments.record_flow` | `{ occurred_on, kind, amount: { amount, currency }, category?, external_ref?, note? }` sur une enveloppe cible | `IFlows.Record` |
| `investments.record_holdings_snapshot` | `{ asOf, sourceLabel?, lines: [{ name, isin?, kind?, units?, unitPrice?, value, costBasisPerUnit? }], movements: [{ holding, on, kind, units?, unitPrice?, amount, label?, ref? }], recordValuation? }` sur une enveloppe cible (§16) | Crée les supports inconnus, enregistre le relevé (qui remplace celui de la date) et les mouvements absents, par les commandes de l'API, dans la transaction de l'approbation |
| `investments.open_envelope` | `{ name, kind, institution?, currency, opened_on?, opening?: { occurred_on, amount }, first_valuation?: { as_of, value } }` | Crée l'enveloppe (propriétaire : celui qui a déposé le document), puis ses faits |

Clé de dédoublonnage (`subject_key`) : la date de valorisation (`record_valuation`), ou la date et le montant (`record_flow`), ou la date du relevé (`record_holdings_snapshot`).

## 9. Sections d'import

Section `envelopes` de `import-format.md` (les sections `valuations` et `flows` sont celles du Core, `import-format.md` §5.6) :

| Champ | Type | Remarques |
|---|---|---|
| `id` | string | Clé du fichier |
| `name` | string | |
| `kind` | string | Voir §3 |
| `institution`, `account_hint`, `note` | string? | |
| `opened_on` | date? | |
| `currency` | string | ISO 4217, obligatoire |
| `valuation_cadence`, `stale_after_days` | string?, int? | |
| `premiums_before_2017` | string? | Montant décimal, `life_insurance` ou `tontine` seulement, `>= 0` ; écrit à l'export seulement s'il est posé |
| `ownership` | `[{ person, share_bp }]` | Comme pour les abonnements |
| `visibility` | string | `shared` (défaut) ou `private` |
| `closed_on` | date? | La date de clôture ; une enveloppe clôturée est exportée |

Sections du détail par support (§16, issue #34 §5), clés comme `envelopes` : `id` de fichier, `item` = l'`id` de l'enveloppe dans le
fichier, un support nommé par son `id` de fichier. Les règles et les avertissements sont ceux de l'API (`HoldingRules`, `MovementRules`,
`HoldingMath`), au chemin du fichier (`holding_snapshots[0].lines[1].unit_price`) ; les avertissements portent le préfixe `investments.`
(`investments.holding_value_mismatch`…). Parts, prix et pourcentages s'écrivent en chaînes (lus aussi comme nombres JSON), à 6 décimales
pour les parts et prix.

**`holdings`**

| Champ | Type | Remarques |
|---|---|---|
| `id`, `item` | string | |
| `name` | string | 1 à 100 caractères |
| `isin` | string? | Format et clé vérifiés ; unique par enveloppe |
| `kind` | string | `euro_fund`, `fund`, `etf`, `equity`, `bond`, `structured`, `cash`, `other` |
| `asset_class`, `issuer`, `note` | string? | |
| `risk_indicator` | int? | 1 à 7 |
| `opened_on`, `matures_on`, `closed_on` | date? | |
| `entry_cost_pct` | décimal? | 0 à 100 |
| `auto_price` | bool? | Cours automatique, vrai par défaut, sans effet sans ISIN ; l'export ne l'écrit que s'il est faux (le hash d'un fichier antérieur reste le même) |

**`holding_snapshots`** : `id`, `item`, `as_of` (date non future), `source_label` (string?), `complete` (bool, vrai par défaut),
`lines` : `[{ holding, units?, unit_price?, value, cost_basis_per_unit? }]`, `value` un montant dans la devise de l'enveloppe. Un relevé
par enveloppe et par date : deux dans le fichier sont une erreur (`import.duplicate_fact`) ; un relevé stocké à cette date que le
fichier ne nomme pas est un `conflict`, remplacé seulement avec `replace` (comme l'API, spec D5). Les lignes sont réécrites avec le
relevé.

**`holding_movements`** : `id`, `holding`, `occurred_on`, `kind` (les types du §16), `units?` (signées par le type), `unit_price?`,
`amount` (> 0), `envelope_flow` (string?, l'`id` d'un flux de `flows` du fichier **sur la même enveloppe**), `switch_ref` (string?,
obligatoire pour `switch_in` et `switch_out`, partagé par toutes les jambes d'un même arbitrage, deux ou plus), `label`, `external_ref`.

- Ordre : ces sections sont appliquées **après** les faits du Core, pour qu'un mouvement retrouve son flux une fois écrit. Un flux
  corrigé depuis n'est plus courant : il n'est pas exporté, et le lien du mouvement non plus.
- Provenance par ligne (`import_ref`, `import_hash`, `imported_by` sur `holding`, `holding_snapshot`, `holding_movement`) comme
  `import-format.md` §8 : `unchanged`, `conflict`, `replace` ; une ligne reste sur son enveloppe ; écrire exige le droit d'écriture
  sur l'enveloppe. Une enveloppe clôturée reçoit son historique comme ses faits.
- Export : les supports, relevés et mouvements de chaque enveloppe exportée ; `id` dérivés `hold-…`, `hsnap-…`, `hmov-…`.
- `holding_cost_reports` : les rapports de frais (PR 3) ne sont pas encore dans le format ; à venir.

## 10. Contrats (`Investments.Contracts`)

| Contrat | Rôle |
|---|---|
| `IEnvelopePerformance` | `Get(item, asOf?)` → métriques du §5, dont le TRI (§6) ; `Period(item, a, b)` → performance |
| `INetWorthContribution` | Valeur, `as_of` et indicateur de péremption de chaque enveloppe |
| `IUpcomingEvents` | Valorisations attendues |
| `IProposalHandler<…>` | Voir §8 |
| `IImportSection` | Section `envelopes` |

Le module ne contribue **pas** aux engagements mensuels.

## 11. Validation

**Erreurs** :

- valorisation ou flux dans une devise différente de celle de l'élément ;
- valorisation datée dans le futur ;
- plus d'un `opening` courant ;
- flux de montant ≤ 0 ;
- `account_hint` de plus de 4 caractères ;
- `kind` ou `valuation_cadence` inconnus.

**Avertissements** : `missing_opening`, `large_change`, valeur périmée, virement interne dont un seul côté est présent (`transfer:` sans contrepartie).

## 12. Exemples

### 12.1 PEA (chiffres fictifs)

Enveloppe `pea`, un propriétaire à 100 %, partagée, devise `EUR`.

| Date | Fait | Montant |
|---|---|---|
| 2024-12-31 | `flow` `opening` | 20 000 € |
| 2024-12-31 | `valuation` | 21 000 € |
| 2025-06-15 | `flow` `contribution` | 2 000 € |
| 2025-12-31 | `valuation` | 25 000 € |

| Métrique au 2025-12-31 | Résultat |
|---|---|
| Versements nets | 22 000 € |
| **Gain** | **+3 000 €** |
| **Gain en %** | **+13,6 %** |
| Performance sur l'année (de 21 000 à 25 000) | +4 000 € de variation, dont 2 000 € de versements et **2 000 € de gain** |
| Rendement annualisé (TRI) | **+14,2 %** |

### 12.2 Import d'une enveloppe

```json
{
  "envelopes": [
    {
      "id": "env-pea",
      "name": "PEA",
      "kind": "pea",
      "institution": "Établissement",
      "currency": "EUR",
      "valuation_cadence": "monthly",
      "ownership": [{ "person": "adult-1", "share_bp": 10000 }],
      "visibility": "shared"
    }
  ],
  "valuations": [
    { "id": "va-001", "item": "env-pea", "as_of": "2025-12-31",
      "amount": { "amount": "25000.00", "currency": "EUR" } }
  ],
  "flows": [
    { "id": "fl-001", "item": "env-pea", "occurred_on": "2024-12-31", "kind": "opening",
      "amount": { "amount": "20000.00", "currency": "EUR" } },
    { "id": "fl-002", "item": "env-pea", "occurred_on": "2025-06-15", "kind": "contribution",
      "amount": { "amount": "2000.00", "currency": "EUR" } }
  ]
}
```

## 13. Tests

| Test | Vérifie |
|---|---|
| Cas doré (§12.1) | Gain +3 000 €, +13,6 %, performance de l'année, TRI +14,2 % |
| **Additivité** (propriété) | Performance sur [a, c] = somme sur [a, b] et [b, c] |
| Flux après la dernière valeur | Le gain reste calculé à `v` ; le flux apparaît dans « depuis la dernière valeur » |
| Flux du jour de la valorisation | Inclus dans les versements nets |
| Virement interne | Neutre pour le total du foyer ; avertissement s'il manque un côté |
| Versements nets ≤ 0 | Gain en % indéfini |
| Devise étrangère | Gain dans la devise de l'élément |
| TRI | Valeurs de référence ; « non significatif » avant 12 mois ou sans changement de signe |
| `missing_opening` | Déclenché sans versement enregistré |
| Import | Aller-retour export puis import identique |
| Supports (§16) | ISIN, poids, non-réparti, avertissements d'un relevé ; une enveloppe sans support inchangée |

## 14. Limites

- **Détail par ligne** (ETF, fonds) : en cours (§16). Les supports et relevés existent ; mouvements, performance par support et frais suivent.
- **Ni fiscalité ni conseil** : gains bruts. Le calcul de fiscalité (gain net d'impôt, régimes par enveloppe) est une **évolution envisagée** à cause de sa complexité (`Project.md` §10).
- Sans détail par support, une assurance-vie ne distingue pas fonds en euros et unités de compte.
- Pas de versements programmés : ils ne sont ni des engagements ni des prévisions en v1.
- Le TRI utilise un flottant à la frontière du calcul (§6).

## 15. Questions ouvertes

- Seuil de `large_change` (30 %) et seuils de péremption : à ajuster sur des données réelles.
- Faut-il exposer un « gain depuis N mois » glissant en plus du gain depuis l'ouverture ?
- Détail par support : forme tranchée par l'issue #34 et `superpowers/specs/2026-10-19-envelope-holdings-design.md` (§16).

## 16. Détail par support (niveau 3)

Optionnel. Conception : `superpowers/specs/2026-10-19-envelope-holdings-design.md` (issue #34, livrée en six PR).

| Table | Rôle |
|---|---|
| `holding` | Un support de l'enveloppe : fonds, ETF, action, fonds en euros, produit structuré, liquidités. ISIN vérifié et unique par enveloppe |
| `holding_snapshot` | La composition à une date (un relevé), une par date ; `complete` dit si tous les supports y sont |
| `holding_snapshot_line` | Une ligne : parts et prix unitaire à 6 décimales (aucun pour le fonds en euros et les liquidités), valeur telle qu'imprimée |
| `holding_movement` | Un mouvement daté sur un support : `allocation_in` / `allocation_out` (part d'un versement ou d'un rachat), `switch_in` / `switch_out` (arbitrage, toutes ses jambes partagent `switch_ref` : un support vers plusieurs, plusieurs vers un, n vers m, issue #45 ; `POST …/switches` prend `from` et `to` en listes, un objet seul vaut une jambe ; chaque jambe lue porte `switchLegs`, toute l'opération), `maturity`, `fee`, `interest`, `income`. Montant > 0, sens donné par le type ; parts signées |
| `holding_cost_report` | Le rapport de frais annuel d'un support (art. A.522-1), un par support et par année, saisi tel qu'imprimé : rendement brut, frais totaux, rendement net, et en option frais de contrat, frais du support, rétrocession, net annualisé sur 5 ans (en %) |

`envelope.uc_share_floor_pct` (optionnel, modifiable par le `PATCH` de l'enveloppe) : le plancher de part en unités de compte.

Lecture : le relevé le plus récent à une date, les poids (arrondis à 0,1 %) et le **non-réparti** (valorisation de l'enveloppe à la même date − somme des lignes). Avertissements `holding_value_mismatch` et `holdings_sum_mismatch`, jamais d'erreur. `holding_value_mismatch` : |parts × prix − valeur| au-delà de 0,01 plus ce que les parts imprimées laissent de côté, un pas de leur dernière décimale × prix (`1,185` « tronqué à 3 décimales » au prix de 3 924,47 € : 3,92 € de marge ; des parts entières sont un compte exact). Les parts gardent les décimales saisies (colonne `numeric` sans échelle, issue #46) ; le fil les écrit toujours à 6 décimales. Un relevé peut, sur demande, enregistrer sa somme comme valorisation de l'enveloppe.

Chiffres d'un support (`HoldingMath`, depuis ses seules données) : entrées (`allocation_in` + `switch_in`) et sorties (`allocation_out` + `switch_out` + `maturity`), gain = valeur + sorties − entrées, gain % sur les entrées, TRI (même solveur que l'enveloppe, `Xirr`), performance de période additive, frais explicites (Σ `fee`) et gain avant frais. `fee`, `interest` et `income` font partie de la performance, pas des flux. Les mouvements ne créent jamais de flux du Core : les chiffres de l'enveloppe ne changent pas. Avertissements `allocation_mismatch` (allocations d'une date ≠ versements ou rachats de cette date) et `switch_unbalanced` (jambes d'un arbitrage à plus de 1 € d'écart).

Frais et part UC : **coût implicite estimé** d'une année = frais totaux du rapport × moyenne des valeurs du support au 31 décembre précédent et au 31 décembre de l'année (au centime, null sans les deux), toujours présenté comme une estimation ; `cost_report_inconsistent` quand |brut − frais − net| > 0,01 pt. **Parts inexpliquées** entre deux relevés consécutifs qui donnent les parts d'un support : Δparts − Σ parts des mouvements → `unexplained_units` au-delà de 0,0001 + un pas de la dernière décimale des parts de chacun des deux relevés (issue #46). Un mouvement sans parts compte montant ÷ son prix, sinon ÷ la moyenne des prix des deux relevés, et la tolérance s'élargit des parts que ce montant achète entre ces deux prix ; sans aucun prix, il ne compte rien, avec, pour des parts disparues, un `fee` **proposé** (parts × prix du relevé le plus récent, jamais écrit). **Part UC** du relevé lu (lignes ni fonds en euros ni liquidités, 0,1 %) ; sous le plancher, `uc_share_below_floor`, aussi dans la routine. Un support ouvert dont `matures_on` tombe dans les 60 jours est un événement « À venir » (`holding_maturity`).

Proposition `investments.record_holdings_snapshot` (§8, spec §7) : un relevé de supports copié d'un document. Une ligne est le support de son ISIN, sinon de son nom exact, sinon un **nouveau support** proposé (son type est alors exigé). Les mouvements déjà connus (même référence, ou même support, date, type et montant) sont signalés `already_recorded` et laissés de côté ; une ligne dont la valeur bouge de plus de **30 %** depuis le relevé précédent, nette des entrées et sorties, est signalée `large_change`. Outils MCP `propose_holdings_snapshot`, `get_envelope_holdings` et `get_holding_performance`.

**Livré** : supports et relevés (PR 1) ; mouvements, arbitrages et performance par support (PR 2) ; rapports de frais, parts inexpliquées, part UC et échéances (PR 3) ; import et export des supports, relevés et mouvements (PR 4, §9) ; proposition dédiée et outils MCP (PR 5) ; écrans (PR 6) : onglet « Supports » de l'enveloppe, fiche d'un support (chiffres, courbe, mouvements, « Coûts »), saisie d'un relevé, d'un mouvement, d'un arbitrage et d'un rapport de frais, plancher de part UC dans les réglages. API `/api/v1/investments/envelopes/{id}/holdings`, `/holding-snapshots`, `/holdings/{holdingId}/movements|series|performance|cost-reports[/{year}]` et `/switches`. **À venir** : la section d'import `holding_cost_reports`, l'outil MCP `propose_cost_report`.
