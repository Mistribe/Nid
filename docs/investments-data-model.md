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
| `kind` | text | `checking` (compte courant), `savings` (livret), `pea`, `life_insurance`, `per`, `brokerage` (compte-titres), `other`. Détermine la couleur fixe du graphique (`frontend/dataviz.md` §5) |
| `institution` | text? | Établissement, texte libre |
| `account_hint` | text? | **Quatre derniers caractères** au plus, pour reconnaître le compte. Jamais le numéro complet |
| `opened_on` | date? | |
| `valuation_cadence` | text | `monthly` (défaut), `quarterly`, `yearly`, `manual` |
| `stale_after_days` | int? | Remplace le seuil dérivé de la cadence |
| `note` | text? | |

Colonnes de provenance et d'audit : celles du Core.

## 4. Sens des faits du Core pour une enveloppe

| Fait | Usage |
|---|---|
| `flow` `opening` | Total net des versements **antérieurs** au début du suivi, daté du jour de la première valorisation. Au plus un courant |
| `flow` `contribution` | Versement (argent apporté de l'extérieur) |
| `flow` `withdrawal` | Retrait vers l'extérieur. **Une distribution qui sort de l'enveloppe** (dividende versé sur le compte bancaire) est un retrait |
| `flow` `expense` | Frais **payés hors de l'enveloppe**. Les frais prélevés dans l'enveloppe sont déjà dans la valeur |
| `flow` `income` | Revenu perçu hors de l'enveloppe. Affiché à part, **hors du gain** |
| `valuation` | Valeur de l'enveloppe entière à une date |

**Virement interne** (livret vers PEA) : un `withdrawal` sur l'un et un `contribution` sur l'autre, avec `category = transfer` et le **même** `external_ref = transfer:<identifiant>` sur les deux éléments (l'unicité de `external_ref` est par élément). Le total du foyer n'est pas modifié.

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

## 6. Rendement annualisé (TRI), étape 6

Sert à comparer honnêtement un livret, une assurance-vie et un PEA en tenant compte du moment des versements.

- **Flux** : `opening` et `contribution` négatifs, `withdrawal` positifs, valeur finale positive à `v`. Base **Actual/365**.
- **Résolution** : bissection sur ]−99,99 %, +10 000 %], tolérance 10⁻⁹ sur le taux, 100 itérations au plus ; arrondi par le serveur à 0,1 %. Pas de changement de signe de la VAN sur l'intervalle → non significatif.
- **Livré** avec l'issue #20 : `annual_return_pct` dans les chiffres d'une enveloppe (`annualReturnPct` sur le fil), affiché « Rendement annuel » sur sa fiche.
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
| `investments.open_envelope` | `{ name, kind, institution?, currency, opened_on?, opening?: { occurred_on, amount }, first_valuation?: { as_of, value } }` | Crée l'enveloppe (propriétaire : celui qui a déposé le document), puis ses faits |

Clé de dédoublonnage (`subject_key`) : la date de valorisation (`record_valuation`), ou la date et le montant (`record_flow`).

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
| `ownership` | `[{ person, share_bp }]` | Comme pour les abonnements |
| `visibility` | string | `shared` (défaut) ou `private` |
| `closed_on` | date? | La date de clôture ; une enveloppe clôturée est exportée |

Sections `holdings`, `holding_snapshots`, `holding_movements`, `holding_cost_reports` : à venir (issue #34, PR 4).

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

Lecture : le relevé le plus récent à une date, les poids (arrondis à 0,1 %) et le **non-réparti** (valorisation de l'enveloppe à la même date − somme des lignes). Avertissements `holding_value_mismatch` et `holdings_sum_mismatch`, jamais d'erreur. Un relevé peut, sur demande, enregistrer sa somme comme valorisation de l'enveloppe.

**Livré** (PR 1) : supports et relevés, API `/api/v1/investments/envelopes/{id}/holdings` et `/holding-snapshots`. **À venir** : mouvements et performance par support, frais, part UC, échéances, import, proposition dédiée, écrans.
