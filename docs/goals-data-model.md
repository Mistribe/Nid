# Modèle de données du module Objectifs

> Statut : Livré en PR 3 de la #106 (2026-10-23). Décrit les tables, les règles de calcul et de contrôle, les routes, les outils MCP, les codes d'erreur et l'import/export du module `Nid.Goals`.
> Références : `superpowers/specs/2026-10-23-savings-goals-design.md` (décisions D1 à D16), `core-data-model.md`, `investments-data-model.md`, `income-data-model.md` (même structure), `import-format.md`, `architecture.md` §5.

---

## 1. Vue d'ensemble

```
Core                                     Goals (schéma goals)
item (module = goals, kind = savings | emergency_fund) ──── goal (1:1)
  └─ ownership, visibility, archived_at                       ├──< funding   (lien vers une enveloppe d'Investments)
                                                              └──< progress_entry (relevé daté)
```

Un objectif est un `item` du Core : nom, détenteurs, visibilité et archivage viennent du registre. Visibilité et détenteurs sont fixés à la création ; seul le nom se modifie (`IItemRegistry.ReplaceAsync`). **Clôturer = archiver** ; il n'y a pas de suppression.

Goals lit les enveloppes par `IEnvelopeFunding` (Investments.Contracts) et les engagements par `ICommitmentsContribution` (Core). Investments ne connaît pas Goals. Tous les montants sont dans la devise de référence du foyer.

## 2. Tables (schéma `goals`)

| Table | Champs | Contraintes |
|---|---|---|
| `goal` | `item_id` (PK), `kind`, `target_amount` numeric(19,4)?, `months` int?, `target_date` date?, `note` text?, colonnes d'import, `xmin` (version) | `ck_goal_shape` : `savings` a `target_amount` > 0 et pas de `months` ; `emergency_fund` a `months` de 1 à 24 et pas de `target_amount` |
| `funding` | `goal_id`, `envelope_id`, `share_bp` | PK (`goal_id`, `envelope_id`) ; `share_bp` de 1 à 10 000 ; suppression en cascade ; index sur `envelope_id` |
| `progress_entry` | `goal_id`, `on` (date), `amount` | PK (`goal_id`, `on`) : un relevé par date, le dernier remplace ; `amount` ≥ 0 ; suppression en cascade |

Un objectif est financé par des enveloppes **ou** libre (relevés) : tant qu'il a des enveloppes, il se lit sur elles et ses relevés sont gardés sans être lus. L'API refuse un relevé sur un objectif financé (`goals.mixed_funding`) ; une modification qui ajoute des enveloppes à un objectif libre garde ses relevés, et l'import accepte les deux. La part d'une enveloppe est en points de base (10 000 = toute sa valeur).

## 3. Règles

- **Cible** : `savings` = `target_amount` ; `emergency_fund` = `months` × coût mensuel amorti des engagements que le lecteur voit (abonnements, prêts, charges des biens), converti ; sans engagement, cible vide et drapeau `no_commitments`. Un coût sans taux de change est laissé de côté, sa source porte `missing_currency`.
- **Atteint** : Σ valeur × part des enveloppes visibles et ouvertes (libre : dernier relevé, 0 sans relevé). Une enveloppe clôturée compte 0 et donne `closed_envelope` ; sans valeur, `no_value` ; valeur périmée, `stale`. Le montant atteint n'est pas plafonné, la progression l'est à 100 %.
- **Rythme** (`monthly_pace`) : versements nets des 12 derniers mois × part ÷ 12 (libre : écart entre le dernier relevé et le relevé de référence ÷ mois écoulés ; moins de deux relevés, pas de rythme). Les gains de marché n'y entrent pas.
- **Statut** : `reached` (reste 0), `past_due` (date cible passée), `on_track` / `behind` (date prévue au plus tard à la date cible, ou non), `no_date` (pas de date cible). La mensualité nécessaire = reste ÷ mois restants (au moins 1).
- **Visibilité et « partiel »** : seules les enveloppes que le lecteur voit dans la portée (`household` ou `me`) comptent. Si un lien lui est caché, l'objectif porte `partial: true`, sans dire lequel ni combien ; l'enveloppe est absente de `funding` et de `ExposedItems()`. La portée `household` masque aussi les enveloppes privées du lecteur. Une enveloppe en devise étrangère sans taux est exclue et donne `missing_currency`.
- **Contrôle des parts** : la somme des parts d'une enveloppe sur les objectifs **ouverts** que l'auteur voit (portée `me`) ne dépasse pas 100 % (`goals.share_exceeded`, avec `freeBp`). La modification ne contrôle que les parts **nouvelles ou relevées**. Le contrôle se fait avant la transaction d'écriture, sans verrou : deux écritures simultanées peuvent ensemble dépasser 100 %. Un objectif privé d'un autre adulte ne bloque pas et n'est pas révélé ; un lecteur peut donc voir plus de 100 % : l'enveloppe porte alors `over_allocated`, sans erreur. À la lecture, la somme compte les objectifs ouverts que le lecteur voit dans la portée **et** ceux qu'il lit (son objectif privé lu en portée `household` garde sa part).
- **Liens invisibles à l'éditeur** : une modification remplace les liens que l'éditeur voit et garde les autres, sans les révéler. Un lien déjà stocké vers une enveloppe clôturée peut être conservé à la modification.
- **Clôture** : un objectif clôturé refuse toute écriture sauf sa réouverture (`goals.goal_closed`), ne compte plus dans la somme des parts, et se rouvre sous le contrôle des parts. Rouvrir un objectif ouvert répond `goals.goal_open`.
- **Modification concurrente** : la modification (`PUT /goals/{id}`) porte la `version` lue avec l'objectif, obligatoire (sans elle, `422` à `version`) ; une version périmée répond `goals.conflict` (jeton `xmin`).
- **Montants** : une cible ou un montant de relevé a au plus 4 décimales et 15 chiffres avant la virgule (`goals.invalid_target` au champ), à l'API comme à l'import.
- **Suppression** (#23) : réservée à un objectif créé par erreur. Partent l'objectif, ses lignes de financement et ses relevés de progression, avec les faits, propriétaires, liens de documents et références bancaires de l'élément. Rien ne bloque : aucun autre élément ne dépend d'un objectif. À la suppression d'une enveloppe, un objectif ouvert qu'elle finance la bloque (`goal_funding`) ; les lignes de financement des objectifs clôturés partent avec elle (`goal_funding_closed`).

## 4. Routes

Corps et réponses en camelCase, comme toutes les routes. Montants : `{ amount, currency }`.

| Route | Rôle |
|---|---|
| `GET /api/v1/goals?scope=household\|me&closed=false\|true` | Liste, vue calculée par objectif. `closed=true` liste les objectifs ouverts **et** clôturés |
| `GET /api/v1/goals/{id}?scope=` | Fiche : vue calculée, financement par enveloppe visible, relevés, sources de la cible de précaution |
| `POST /api/v1/goals` | Création |
| `PUT /api/v1/goals/{id}` | Remplacement du nom, de la cible, des mois, de la date, de la note et du financement (le genre ne change pas), avec la `version` lue |
| `POST /api/v1/goals/{id}/close`, `/reopen` | Clôture, réouverture |
| `PUT /api/v1/goals/{id}/progress/{on}` | Relevé daté (pas dans le futur) |
| `DELETE /api/v1/goals/{id}/progress/{on}` | Suppression d'un relevé |

Vue calculée : `target`, `reached`, `progressPct`, `remaining`, `monthlyNeeded`, `monthlyPace`, `projectedOn`, `status`, `partial`, `flags` (`partial`, `missing_currency`, `closed_envelope`, `no_commitments`, `no_value`, `stale`, `over_allocated`), et `canWrite` : le lecteur peut modifier l'objectif (il en est un des rédacteurs, comme pour tout élément ; lu en une fois pour toute la liste). Si la lecture des engagements échoue, toute la lecture échoue (pas de cible silencieusement fausse).

## 5. MCP

Lecture : `get_goals(scope, closed?)` et `get_goal(id, scope)`, même JSON que les routes. Écriture : un `propose_*` par route d'écriture (`RouteActions`) : `propose_create_goal`, `propose_edit_goal`, `propose_close_goal`, `propose_reopen_goal`, `propose_record_goal_progress`, `propose_delete_goal_progress` ; avec `apply: true` et `write:direct`, écriture directe.

## 6. Codes

| Code | HTTP |
|---|---|
| `goals.goal_closed`, `goals.goal_open` | 409 |
| `goals.share_exceeded` (`freeBp` dans la réponse) | 409 |
| `goals.conflict` | 409 |
| `goals.unknown_kind`, `goals.invalid_target`, `goals.invalid_months`, `goals.target_date_past`, `goals.invalid_share`, `goals.envelope_not_visible`, `goals.mixed_funding`, `goals.progress_in_future` | 422 |

## 7. Import et export

Section `goals` de `nid.data` (`GoalsImportSection`, après les sections d'Investments). Les membres du fichier sont en snake_case, comme tout `nid.data`. Un objectif du fichier porte `id`, `name`, `kind`, `target_amount`, `months`, `target_date`, `note`, `visibility`, `ownership` (`person`, `share_bp`), `closed`, `funding` (`envelope` = id fichier d'une enveloppe, `share_bp`) et `entries` (`on`, `amount`).

- Les écritures passent par le registre et le magasin comme l'API ; les règles de champs sont celles de `GoalCheck`, sauf qu'une date cible passée est acceptée (un import restaure le passé).
- L'import **n'applique pas** le plafond de 100 % : il restaure toute répartition, et la lecture signale `over_allocated`.
- Un objectif du fichier peut porter à la fois `funding` et `entries` : les relevés sont gardés, non lus tant que l'objectif a des enveloppes.
- Avec `replace`, un lien vers une enveloppe que l'importeur ne voit pas est gardé tel quel (D10c).
- Ré-importer le même fichier ne change rien (empreinte par `import_ref`).
