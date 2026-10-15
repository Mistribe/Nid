# Modèle de données du module RealEstate : les crédits

> Statut : brouillon issu de la discussion technique. Décrit le crédit, ses phases, son assurance, ses frais, ses remboursements anticipés, l'échéancier calculé, la comparaison avec le tableau de la banque et les simulations.
> Références : `../Project.md` §8, `README.md` (le bien), `../core-data-model.md`, `../import-format.md`, `../architecture.md` §5.

---

## 1. Objectifs

- **Comprendre chaque mensualité** : part d'intérêts, part de capital, capital restant dû, coût total.
- Couvrir les cas réels : prêt à taux fixe, prêt à taux zéro ou avec différé, taux variable ou à paliers, renégociation, remboursement anticipé, plusieurs prêts sur un bien.
- **Le tableau de la banque fait référence** : le calcul sert à vérifier la saisie du contrat et à projeter, pas à contredire la banque (`../Project.md` §8.5).
- Calculs **déterministes**, en décimal exact, sans flottant, sans IA.

## 2. Entités

Un prêt est un `item` du Core (`module = realestate`, `kind = loan`). Ses **emprunteurs** sont ses propriétaires (`../core-data-model.md` §4). Le module ajoute les tables suivantes (schéma `realestate`). Les montants sont dans la devise de l'élément.

### 2.1 `loan`

| Champ | Type | Remarques |
|---|---|---|
| `item_id` | uuid | Clé primaire |
| `lender` | text? | Établissement prêteur |
| `principal` | numeric(19,4) | > 0, montant débloqué |
| `disbursed_on` | date | Début du décompte des intérêts |
| `first_payment_date` | date | Les échéances suivantes : + k mois, jour d'ancrage écrasé en fin de mois (`Duration` du Core) |
| `total_payments` | int | Durée contractuelle en échéances, de 1 à 600 |
| `property_item_id` | uuid? | Bien financé. Un bien peut avoir plusieurs prêts |
| `schedule_source` | text | `computed` (défaut) ou `bank` |
| `rounding_mode` | text | `half_up` (défaut) ou `truncate` : arrondi des intérêts à chaque échéance |
| `first_period_mode` | text | `full` (défaut) ou `prorata_actual_365` |
| `note` | text? | |

### 2.2 `loan_phase`

Une phase par régime. Les phases sont **contiguës par construction** : elles s'enchaînent dans l'ordre de `seq`, sans dates propres, donc sans chevauchement possible.

| Champ | Type | Remarques |
|---|---|---|
| `id`, `loan_item_id` | uuid | |
| `seq` | int | 1, 2, 3… sans trou |
| `mode` | text | `annuity` (mensualité constante), `deferred_partial` (intérêts seuls), `deferred_total` (rien payé, intérêts capitalisés) |
| `annual_rate` | numeric(9,6) | Fraction décimale : `0.035000` pour 3,5 %. 0 permis (prêt à taux zéro). Strictement inférieur à 1 |
| `rate_type` | text | `fixed` ou `variable` |
| `payments` | int? | Nombre d'échéances de la phase. Vide pour la dernière phase : elle court jusqu'au solde |
| `payment_override` | numeric? | Mensualité contractuelle imposée : paliers, modulation, arrondi de la banque |
| `opening_balance_override` | numeric? | Recale le capital à l'ouverture de la phase (renégociation, cohérence avec un document) |
| `note` | text? | |

**Chaque changement de taux, de mensualité ou de régime ouvre une phase.**

### 2.3 `loan_insurance`

| Champ | Type | Remarques |
|---|---|---|
| `id`, `loan_item_id` | uuid | |
| `mode` | text | `fixed_monthly` ; `rate_on_initial` (taux annuel × capital initial ÷ 12, montant fixe) ; `rate_on_remaining` (taux annuel × capital restant dû ÷ 12, dégressive) |
| `monthly_amount` | numeric? | Pour `fixed_monthly` |
| `annual_rate` | numeric(9,6)? | Pour les deux autres modes |
| `starts_on`, `ends_on?` | date | Intervalle semi-ouvert |
| `note` | text? | |

### 2.4 `loan_fee`

| Champ | Type | Remarques |
|---|---|---|
| `id`, `loan_item_id` | uuid | |
| `kind` | text | `file`, `guarantee`, `early_repayment_penalty`, `renegotiation`, `other` |
| `amount` | numeric(19,4) | > 0 |
| `on_date` | date | |
| `note` | text? | |

### 2.5 `loan_event`

| Champ | Type | Remarques |
|---|---|---|
| `id`, `loan_item_id` | uuid | |
| `on_date` | date | |
| `amount` | numeric(19,4) | > 0, au plus le capital restant dû à cette date |
| `effect` | text | `reduce_term` (mensualité inchangée, fin anticipée) ou `reduce_payment` (mensualité recalculée) |
| `fee_id` | uuid? | Pénalité éventuelle (`loan_fee`) |
| `note` | text? | |

### 2.6 `loan_bank_schedule_row`

Lignes d'un tableau d'amortissement importé de la banque (§6).

| Champ | Type | Remarques |
|---|---|---|
| `id`, `loan_item_id`, `due_date` | uuid, uuid, date | Clé primaire `id` ; (`loan_item_id`, `due_date`) unique |
| `payment`, `interest`, `principal` | numeric(19,4) | |
| `insurance` | numeric(19,4)? | Si le tableau la détaille |
| `crd` | numeric(19,4) | Capital restant dû juste après l’échéance, avant un remboursement anticipé daté de ce jour ou plus tard |
| `import_ref`, `import_hash`, `imported_by` | text?, char(64)?, uuid? | Provenance d’un import, comme les autres tables (3F2) ; l’extraction depuis un document viendra à l’étape 4 |

## 3. Échéancier

Fonction pure `BuildSchedule(loan, phases, insurances, fees, events)` : **décimal exact, aucun flottant**, calculée à la demande (600 lignes au plus, non stockée).

### 3.1 Conventions

- **Dates d'échéance** : `first_payment_date` + k mois, jour d'ancrage écrasé en fin de mois.
- **Taux périodique** = taux annuel ÷ 12 (convention 30/360 de `../Project.md` §8.2), **sans jamais le précalculer**.
- **Intérêts** d'une échéance = `arrondi(capital restant avant échéance × taux annuel ÷ 12)`, **en multipliant d'abord**, arrondi selon `rounding_mode` (demi supérieur, ou troncature) au centime. **Vérifié** (`../spikes.md` §7) : précalculer `taux ÷ 12` fait basculer l'arrondi d'un centime quand le produit tombe exactement sur un demi-centime (12 écarts sur 3 000 prêts) ; en multipliant d'abord, 0 écart. Toute conversion texte utilise `InvariantCulture`.
- **Première période** : `full` = un mois complet ; `prorata_actual_365` = intérêts `round(principal × taux annuel × jours ÷ 365)` avec jours = `first_payment_date − disbursed_on`. En annuité, la première échéance porte la différence : elle vaut la mensualité + (intérêts au prorata − intérêts d’un mois), et rembourse le capital d’un mois ordinaire.

### 3.2 Construction

Pour chaque échéance k, dans l'ordre des phases :

1. **Mensualité de la phase**
   - `annuity` : `payment_override` s'il existe ; sinon `C × r ÷ (1 − (1+r)^(−n))`, arrondie au centime (demi supérieur), avec C le capital restant à l'ouverture de la phase (ou `opening_balance_override`), r le taux périodique et n le **nombre d'échéances restantes du prêt** (`total_payments` − échéances déjà passées). `r = 0` : `C ÷ n`. La puissance est un entier : exponentiation par carrés **en décimal**.
   - `deferred_partial` : mensualité = intérêts.
   - `deferred_total` : mensualité 0 ; les intérêts sont **ajoutés** au capital restant dû.
2. **Échéance** : `capital = mensualité − intérêts` ; nouveau capital restant dû = ancien − capital.
3. **Plafond et absorption** : avant l'échéance `total_payments`, la mensualité est plafonnée à `capital restant + intérêts`. **L'échéance `total_payments` absorbe le résidu d'arrondi** : elle vaut `capital restant + intérêts`, au-dessus ou en dessous de la mensualité. Sans cette règle, un remboursement à mensualité réduite produit une échéance de trop.
4. **Assurance** ajoutée à chaque échéance selon son mode (`rate_on_remaining` utilise le capital restant **avant** l'échéance). Elle est hors mensualité de prêt.
5. **Remboursement anticipé** : appliqué **juste après l'échéance due à la date de l'événement ou avant** (avertissement `event_off_payment_date` si la date n'est pas une échéance). Le capital restant baisse du montant.
   - `reduce_term` : mensualité inchangée, le prêt se termine plus tôt.
   - `reduce_payment` : mensualité recalculée sur les échéances restantes prévues (`total_payments` − k). Une `payment_override` de la phase courante est **ignorée** à partir de là (avertissement).
   - Pendant un différé, seul le capital baisse ; l'effet s'applique à la première phase d'annuité qui suit.
6. **Fin** : le prêt s'arrête quand le capital restant dû est nul. Si une mensualité imposée est trop basse, il se prolonge au-delà de `total_payments` (avertissement `exceeds_total_payments`).
7. **Phases non terminées** : au-delà de la dernière phase connue d'un prêt à taux variable, le futur est **projeté au dernier taux** (avertissement `projection_uses_last_rate`).

**Règles précisées en 3A1** (`superpowers/specs/2026-10-09-loan-schedule-engine-design.md` §4) : l’arrondi `truncate` ne concerne que les intérêts (une mensualité calculée est toujours arrondie au demi supérieur) ; un remboursement anticipé daté avant la première échéance s’applique avant elle, sur le capital débloqué ; quand les phases s’épuisent avant la fin, le calcul continue en annuité au dernier taux connu sur les échéances restantes, avec `projection_uses_last_rate` ; une phase d’annuité qui commence en cours de prêt sans mensualité imposée la calcule sur son capital d’ouverture et les échéances restantes du prêt ; une mensualité imposée qui ne couvre pas les intérêts est refusée (`payment_below_interest`), une mensualité trop basse mais suffisante prolonge le prêt avec `exceeds_total_payments`, dans la limite de 1 200 échéances (`schedule_too_long`) ; l’assurance s’applique aux échéances datées dans [`starts_on`, `ends_on`), `rate_on_initial` = arrondi(taux × capital initial ÷ 12), `rate_on_remaining` = arrondi(taux × capital avant l’échéance ÷ 12), et reste due pendant un différé total ; les frais ne sont pas des lignes de l’échéancier, ils comptent dans les indicateurs ; la dernière échéance (contractuelle, ou fin effective avancée par un `reduce_term`) solde capital + intérêts dans tous les modes, y compris un différé qui court jusqu’au terme (prêt in fine) ; une annuité à mensualité imposée ne la solde que si le reste (capital + intérêts − mensualité) est inférieur à une mensualité, sinon elle verse la mensualité et le prêt se prolonge avec `exceeds_total_payments`, chaque échéance suivante valant min(mensualité, capital + intérêts) ; en `prorata_actual_365`, la première échéance d’une annuité, sauf si elle solde le prêt, vaut la mensualité de la phase (calculée ou imposée) + (intérêts au prorata − arrondi(capital × taux annuel ÷ 12)), si bien que son capital est celui d’un mois ordinaire : les deux arrondis suivent `rounding_mode`, `payment_below_interest` compare la mensualité imposée aux intérêts d’un mois, le plafond capital + intérêts s’applique, et les échéances de différé ne changent pas (le différé partiel paie les intérêts au prorata, le total les capitalise) ; un remboursement `reduce_term` pendant une annuité, appliqué après l’échéance k, avance la fin effective du prêt (au départ `total_payments`) à min(fin, k + m), m étant le nombre d’échéances qu’il faut encore à la mensualité courante pour rembourser le nouveau capital au taux courant (compté échéance par échéance, même arrondi, chacune plafonnée à capital + intérêts), plus la durée des différés prévus après k (un différé sans durée, jusqu’au terme, laisse la fin inchangée) ; cette fin remplace `total_payments` pour la mensualité d’une phase qui s’ouvre (n = fin − (k − 1)), la projection, le recalcul `reduce_payment` (n = fin − k) et l’échéance qui solde (k ≥ fin), tandis que `exceeds_total_payments` reste rapporté à `total_payments` ; un remboursement anticipé daté après la fin du prêt est refusé (`event_above_balance`).

**Livré en 3A2** (2026-10-12, `superpowers/specs/2026-10-12-loans-server-design.md`) : projets `Nid.RealEstate.Application` et `Nid.RealEstate.Infrastructure`, module `realestate` activé, schéma `realestate` avec les tables `loan`, `loan_phase`, `loan_insurance`, `loan_fee` et `loan_event` (pas encore de `property_item_id` ni de `schedule_source`, qui viendront en 3C et 3F, ni de `loan_bank_schedule_row`) ; `loan_event` n’a pas de colonne `kind` (§2.5) : le remboursement anticipé est le seul événement. Un prêt est un élément du Core (`realestate`, `loan`) ; ses emprunteurs sont ses propriétaires. Il n’est ni supprimé ni archivé : il est **terminé** quand sa dernière échéance et son dernier remboursement anticipé sont passés. API `/api/v1/realestate` :

- `GET /loans?scope=&ended=` : capital, capital restant dû, prochaine échéance assurance comprise, taux courant, fin ; les prêts terminés seulement avec `ended=true` ;
- `GET /loans/{id}` : le contrat et sa `version`, les emprunteurs, la visibilité, les frais et les remboursements avec leur `version` (un remboursement avec sa pénalité), tous les indicateurs du jour (§8), les avertissements ;
- `GET /loans/{id}/schedule` : l’échéancier complet et ses avertissements ;
- `POST /loans/{id}/simulations` : la simulation du §7, en lecture seule (un lecteur qui ne peut pas modifier le prêt peut simuler) ;
- `POST /loans`, `PATCH /loans/{id}` (nom, prêteur, note ; emprunteurs, visibilité et devise sont fixés à la création, comme pour une source de revenus), `PUT /loans/{id}/contract` (le contrat remplacé en entier, phases et assurances comprises) ;
- `POST`, `PUT` et `DELETE` sur `/loans/{id}/fees` et `/loans/{id}/repayments` : un remboursement et sa pénalité (un frais `early_repayment_penalty` à la même date) s’enregistrent, se modifient et se suppriment ensemble.

Toute écriture qui change le calcul reconstruit l’échéancier et est refusée s’il échoue : un prêt enregistré se calcule toujours. Chaque écriture verrouille la ligne du prêt : deux remboursements envoyés ensemble ne peuvent pas dépasser à eux deux le capital restant dû. Montants et taux voyagent en chaînes décimales invariantes (`"200000.00"`, `"0.035"`) ; un montant a au plus deux décimales et reste sous 10¹², un taux au plus six décimales. Chaque refus nomme son champ dans le corps de la requête (`principal`, `phases[0].annualRate`, `insurances[1].endsOn`) ; pour un remboursement enregistré que le nouveau contrat rend impossible, le refus nomme `repayments[i].onDate` quand le contrat déplace `disbursedOn` au-delà de sa date et `repayments[i].amount` quand il le ferait dépasser le capital restant dû (rang compté par date puis identifiant) ; la suppression d’un remboursement refusée par le moteur répond à `phases[k].paymentOverride` ou à `repayments[r].…`, r étant le rang dans la liste complète enregistrée. Codes `realestate.invalid_loan`, `payment_below_interest`, `event_above_balance`, `schedule_too_long`, `loan_conflict`, `fee_conflict`, `repayment_conflict` (409) et `fee_linked` (une pénalité se modifie par son remboursement).

**Totaux annuels de l’échéancier** (`GET /loans/{id}/schedule`, `years[]` ; livrés en 3B1, règle fixée en 3B2, `superpowers/specs/2026-10-13-loan-editing-design.md` §3) : une année additionne la mensualité hors assurance, les intérêts, le capital et l’assurance des échéances dues dans l’année ; son remboursement anticipé est la somme des remboursements **datés** dans l’année ; son capital restant dû est celui du **31 décembre**, lu comme les indicateurs à une date (§8) : un remboursement appliqué après la dernière échéance de l’année mais daté de l’année suivante n’est pas encore fait. Les années listées sont celles des échéances et des remboursements : une année sans échéance (un remboursement avant la première échéance, une année plus tôt ; un rachat daté de janvier après l’échéance de décembre) a 0 en mensualité, intérêts, capital et assurance. Les lignes mensuelles gardent la lecture du moteur : un remboursement hors échéance est porté par la ligne après laquelle il s’applique (avertissement `event_off_payment_date`).

**Lien vers un bien** (livré en 3C1, `superpowers/specs/2026-10-13-properties-server-design.md`) : colonne `loan.property_item_id`, posée par `propertyId` à la création (`POST /loans`) ou par `PATCH /loans/{id}`, retirée par `clearProperty`. Il faut pouvoir modifier le prêt et **lire** le bien ; un identifiant illisible, inconnu ou qui n’est pas un bien répond pareillement `422 realestate.invalid_property` sur `propertyId`, jamais 404. `LoanSummary` et `LoanDetail` portent `propertyId`, et `propertyName` quand la personne peut lire le bien (`null` sinon, l’identifiant restant visible : le prêt dit à qui le lit ce qu’il finance). Le lien est une colonne de la ligne du prêt : le poser ou le retirer change la `version` du contrat, comme une modification du prêteur.

## 4. Exemple vérifié

Calculé en décimal exact : **200 000 € à 3,5 % sur 240 échéances**, une phase `annuity`, arrondi demi supérieur.

| | Résultat |
|---|---|
| Mensualité | **1 159,92 €** |
| Échéance 1 | intérêts 583,33 €, capital 576,59 €, restant dû 199 423,41 € |
| Échéance 60 | intérêts 475,24 €, capital 684,68 €, restant dû 162 253,12 € |
| Échéance 120 | intérêts 344,50 €, capital 815,42 €, restant dû 117 298,78 € |
| **Dernière échéance (240)** | **1 159,78 €** (intérêts 3,37 €, capital 1 156,41 €) |
| Intérêts totaux | **78 380,66 €** |
| Après 60 échéances | 69 595,20 € payés, dont 37 746,88 € de capital et 31 848,32 € d'intérêts |
| **10 000 € remboursés après l'échéance 60, `reduce_term`** | 226 échéances au lieu de 240 ; dernière échéance 832,08 € ; intérêts totaux 71 814,08 € ; **6 566,58 € économisés** |
| **Idem, `reduce_payment`** | mensualité **1 088,43 €** sur 180 échéances ; dernière 1 088,69 € ; intérêts totaux 75 512,86 € ; **2 867,80 € économisés** |

**Prêt à taux zéro** (exemple) : 40 000 €, 240 échéances, phase 1 `deferred_total` sur 60 échéances (rien à payer, taux 0 donc rien de capitalisé), phase 2 `annuity` à 0 % : mensualité 40 000 ÷ 180 = **222,22 €** ; la dernière échéance absorbe le résidu : **222,62 €**.

## 5. Cas d'usage et modélisation

| Cas | Modélisation |
|---|---|
| Prêt à taux fixe classique | Une phase `annuity`, `rate_type = fixed` |
| Prêt à taux zéro avec différé | Phase `deferred_total` (ou `deferred_partial`), puis phase `annuity` à taux 0 |
| Taux révisable | Une phase `annuity` `variable` par période de taux connue ; le futur est projeté au dernier taux |
| **Paliers** (mensualités croissantes) | Plusieurs phases `annuity`, chacune avec sa `payment_override` |
| **Modulation** de mensualité | Nouvelle phase avec `payment_override` ; le terme s'ajuste jusqu'au solde |
| **Renégociation** | Nouvelle phase (nouveau taux, éventuellement `opening_balance_override`) et frais `renegotiation` |
| Plusieurs prêts sur un bien | Plusieurs `loan` avec le même `property_item_id` |
| Prêt sans bien | `property_item_id` vide : prêt personnel, contribue au patrimoine en négatif |

## 6. Tableau de la banque et comparaison

**Le tableau de la banque fait référence.**

- Avec `schedule_source = bank` : le **capital restant dû à la date d** est celui de la dernière ligne de la banque au plus tard à d. **Au-delà de la dernière ligne, on continue par calcul** depuis son capital restant dû, avec les phases du prêt. Les intérêts, capitaux et assurances payés à ce jour viennent des lignes de la banque.
- **Comparaison** `ScheduleDiff(calculé, banque)` : par échéance, écarts de mensualité, d'intérêts, de capital et de capital restant dû. Synthèse : écart maximal, **première date où l'écart de capital restant dû dépasse 1 €**, écart cumulé d'intérêts. Un écart de **0,05 € par ligne ou moins** est de l'arrondi.
- Le rapport est joint à la proposition `realestate.import_amortization` sous forme d'avertissement `schedule_mismatch`. L'utilisateur ajuste alors les paramètres du prêt (`rounding_mode`, `first_period_mode`, `payment_override`, phases) ou **accepte le tableau de la banque** comme référence.
- L'extraction du tableau depuis un PDF suit la sécurité de l'ingestion (`../security/ai-ingestion.md`).

**Livré en 3F2** (`superpowers/specs/2026-10-15-bank-schedule-design.md`) : le tableau se colle en CSV sur la fiche du crédit (section « Tableau de la banque » : séparateur `;`, tabulation ou `,` ; décimales à la virgule ou au point ; espaces des milliers ; dates `jj/mm/aaaa` ou `aaaa-mm-jj` ; colonnes date, mensualité hors assurance, intérêts, capital, assurance facultative, capital restant dû ; une première ligne de titres ignorée) ou arrive par la section d’import `loan_bank_schedule` (§10). « Comparer » (`POST /api/v1/realestate/loans/{id}/bank-schedule/preview`, lecture seule) lit et compare sans rien enregistrer ; `PUT …/bank-schedule` (avec la `version` du crédit) remplace le tableau entier sans changer la référence ; `DELETE …/bank-schedule?version=` le supprime et rend la référence au calcul ; `PATCH /loans/{id}` choisit `scheduleSource` (`computed` ou `bank`, `bank` refusé sans tableau : `realestate.bank_schedule_missing`). Avec `bank`, l’échéancier est celui de la banque jusqu’à sa dernière ligne (les lignes calculées le précèdent s’il commence plus tard), puis le calcul reprend depuis son capital restant dû, dans la phase de cette ligne et avec sa mensualité (une phase suivante s’ouvre comme d’habitude, l’assurance suit le contrat) ; tout ce qui lit un crédit suit — fiche, échéancier (lignes marquées « Banque »), valeur nette du bien, patrimoine et sa courbe, engagements, « À venir » ; la simulation d’un remboursement anticipé reste sur le calcul. Un remboursement anticipé daté entre deux lignes baisse le solde après la première, comme le moteur l’applique. `ScheduleDiff` compare chaque ligne à la ligne calculée de même date ; la fiche donne sa synthèse (`comparison` : écart maximal et sa date, première date où le capital restant dû s’écarte de plus de 1 €, écart cumulé d’intérêts, lignes au-delà de 0,05 €) dès qu’un tableau est enregistré, et l’avertissement `schedule_mismatch` quand le calcul est la référence et s’en écarte de plus de 1 €. Un tableau enregistré correspond toujours à son crédit : une modification du contrat ou d’un remboursement anticipé qu’il ne suivrait plus est refusée (`realestate.bank_schedule_invalid` sur `bankSchedule`, sur `amount` pour un remboursement anticipé) ; il faut d’abord le remplacer ou le supprimer.

## 7. Simulations

Fonctions pures, **lecture seule** (portée `read`) : rien n'est écrit.

`POST /api/v1/realestate/loans/{id}/simulations/early-repayment`

| Entrée | Remarques |
|---|---|
| `on_date`, `amount` | Le montant est au plus le capital restant dû |
| `effect?` | `reduce_term` ou `reduce_payment` ; **sans effet précisé, les deux sont comparés** |
| `penalty?` | Pénalité **saisie par l'utilisateur** : aucun plafond légal n'est codé, aucun conseil |

| Sortie (par effet) | Contenu |
|---|---|
| Mensualité, date de fin, nombre d'échéances | Avant et après |
| Intérêts totaux | Avant et après |
| Intérêts économisés | Avant − après |
| Économie nette | Intérêts économisés − pénalité |

La simulation de renégociation (nouveau taux et frais) est une extension possible.

## 8. Indicateurs d'un prêt à la date d

| Indicateur | Définition |
|---|---|
| **Capital restant dû** | Après la dernière échéance datée au plus tard d ; le capital débloqué si d est entre le déblocage et la première échéance ; 0 avant le déblocage |
| Capital remboursé, intérêts, assurance et frais payés | Somme des échéances et des frais jusqu'à d |
| Phase et taux courants, échéances restantes | |
| Prochaine échéance | Date et montant total (mensualité + assurance) |
| Date de fin | Dernière échéance du calcul |
| **Coût total du crédit** | Σ intérêts + Σ assurance + Σ frais (pénalités comprises), en **payé** et en **restant** |
| Mensualité totale | Mensualité + assurance |

La mensualité totale est la prochaine échéance assurance comprise ; le capital remboursé inclut les intérêts capitalisés pendant un différé total ; des frais datés avant le déblocage comptent dès leur date.

**TAEG** : hors v1. Il se calcule avec la fonction de TRI de l'étape 6 (`../investments-data-model.md` §6) sur les flux du prêt (capital reçu, mensualités, assurance, frais).

## 9. Intégration au foyer

| Sujet | Règle |
|---|---|
| **Patrimoine** | Chaque prêt contribue pour −capital restant dû (converti si la devise diffère de celle du foyer) |
| **Engagements** | Mensualité et assurance de l'échéance courante : intérêts seuls en différé partiel, **0** en différé total |
| **À venir** | Fin d'une phase (**changement de taux, fin de différé**), fin du crédit dans les 12 mois |
| Lien avec un bien | Voir `README.md` §7 et §8 : valeur nette, coût réel |

## 10. Propositions et sections d'import

**Propositions**

| `kind` | Payload | Application |
|---|---|---|
| `realestate.import_amortization` | `{ loan, rows: [ { due_date, payment, interest, principal, insurance?, crd } ] }` avec rapport de comparaison | Enregistre les lignes, passe `schedule_source` à `bank` |
| `realestate.create_loan` | `{ lender, principal, disbursed_on, first_payment_date, total_payments, property?, phases[], insurances[], fees[] }` | Crée le prêt depuis une offre de prêt. Candidat pour l'étape 4 |

**Sections d'import** (`../import-format.md`)

| Section | Champs |
|---|---|
| `loans` | `id`, `name`, `lender?`, `currency`, `principal` (montant), `disbursed_on`, `first_payment_date`, `total_payments`, `property?` (référence), `schedule_source?`, `rounding_mode?`, `first_period_mode?`, `ownership`, `visibility`, `note?` |
| `loan_phases` | `id`, `loan`, `seq`, `mode`, `annual_rate` (chaîne décimale), `rate_type`, `payments?`, `payment_override?` (montant), `opening_balance_override?` (montant), `note?` |
| `loan_insurances` | `id`, `loan`, `mode`, `monthly_amount?` (montant), `annual_rate?`, `starts_on`, `ends_on?`, `note?` |
| `loan_fees` | `id`, `loan`, `kind`, `amount` (montant), `on_date`, `note?` |
| `loan_events` | `id`, `loan`, `on_date`, `amount` (montant), `effect`, `note?` |
| `loan_bank_schedule` | `id`, `loan`, `due_date`, `payment`, `interest`, `principal`, `insurance?`, `crd` : chaînes décimales dans la devise du prêt, écrites avec deux décimales (livrée en 3F2) |

Exemple : un prêt principal et un prêt à taux zéro sur un même bien.

```json
{
  "loans": [
    { "id": "loan-main", "name": "Prêt principal", "lender": "Établissement",
      "currency": "EUR", "principal": { "amount": "200000.00", "currency": "EUR" },
      "disbursed_on": "2024-01-15", "first_payment_date": "2024-02-15",
      "total_payments": 240, "property": "prop-home",
      "ownership": [ { "person": "adult-1", "share_bp": 5000 },
                     { "person": "adult-2", "share_bp": 5000 } ],
      "visibility": "shared" },
    { "id": "loan-ptz", "name": "Prêt à taux zéro", "currency": "EUR",
      "principal": { "amount": "40000.00", "currency": "EUR" },
      "disbursed_on": "2024-01-15", "first_payment_date": "2024-02-15",
      "total_payments": 240, "property": "prop-home",
      "ownership": [ { "person": "adult-1", "share_bp": 5000 },
                     { "person": "adult-2", "share_bp": 5000 } ],
      "visibility": "shared" }
  ],
  "loan_phases": [
    { "id": "lp-1", "loan": "loan-main", "seq": 1, "mode": "annuity",
      "annual_rate": "0.035000", "rate_type": "fixed" },
    { "id": "lp-2", "loan": "loan-ptz", "seq": 1, "mode": "deferred_total",
      "annual_rate": "0", "rate_type": "fixed", "payments": 60 },
    { "id": "lp-3", "loan": "loan-ptz", "seq": 2, "mode": "annuity",
      "annual_rate": "0", "rate_type": "fixed" }
  ],
  "loan_insurances": [
    { "id": "li-1", "loan": "loan-main", "mode": "rate_on_initial",
      "annual_rate": "0.003000", "starts_on": "2024-02-15" }
  ]
}
```

**Livré en 3F1** (`superpowers/specs/2026-10-15-realestate-import-export-design.md`) : `loans` à `loan_events`. `property` nomme un bien du même fichier ; `schedule_source` vaut `computed`, `bank` (livré en 3F2, avec `loan_bank_schedule`, voir ci-dessous) ou manque ; `rounding_mode` vaut `half_up` et `first_period_mode` `full` par défaut ; `annual_rate` est une chaîne décimale (un nombre JSON est accepté). Chaque prêt est validé entier, comme à l’écran (§11) : phases 1, 2, 3… sans trou, dans n’importe quel ordre dans le fichier, prêt à taux zéro compris ; la première erreur est signalée sur la ligne du fichier qui l’apporte. Un remboursement anticipé n’a pas de champ pour sa pénalité : elle s’exporte comme un frais `early_repayment_penalty` et se réimporte sans lien. Un prêt lié à un bien que le lecteur ne peut pas lire s’exporte sans `property`. Modifier le contrat à l’écran garde la référence d’import de chaque phase (par son numéro) et de chaque assurance (par son rang) tant que leur nombre ne change pas ; sinon seules les lignes laissées identiques la gardent (`import-format.md` §12bis).

**Livré en 3F2** (`superpowers/specs/2026-10-15-bank-schedule-design.md`) : la section `loan_bank_schedule` ; les lignes d’un prêt arrivent dans n’importe quel ordre, sont lues par date et le tableau entier est vérifié avec le prêt (§11), la première erreur sur la ligne du fichier qui l’apporte ; `schedule_source: bank` est accepté quand le prêt a des lignes de banque une fois l’import écrit (celles du fichier ou des lignes déjà enregistrées), sinon `realestate.bank_schedule_missing` ; l’export écrit `schedule_source` tel qu’il est enregistré et le tableau (`id` dérivé `lbs-`) ; un tableau remplacé à l’écran garde la référence d’import de chaque ligne par sa date.

## 11. Validation

**Erreurs** :

- `principal` ≤ 0 ; `first_payment_date` < `disbursed_on` ; `total_payments` hors de 1 à 600 ;
- somme des `payments` des phases > `total_payments` ; `seq` non contigu ; plus d'une phase sans `payments` ;
- `annual_rate` < 0 ou ≥ 1 ; mode inconnu ;
- assurance : `fixed_monthly` sans `monthly_amount`, autres modes sans `annual_rate` ;
- frais ≤ 0 ; événement d'un montant supérieur au capital restant dû à sa date ;
- un tableau de la banque (`realestate.bank_schedule_invalid`, sur `rows[i].colonne`, i la ligne du texte collé, titres et lignes vides comptés ; dans un fichier, sur la ligne de `loan_bank_schedule` ; la lecture liste au plus les 50 premières cellules illisibles, le message en donnant le total) : aucune ligne ; une date qui n’est pas une échéance du calcul, des dates non croissantes ou une échéance manquante ; un montant négatif, à plus de deux décimales ou au-delà de 10¹² ; une mensualité différente des intérêts plus le capital (sauf une échéance de différé total qui ne paie rien) ; un capital restant dû qui remonte hors d’un différé total ; la banque comme référence sans tableau (`realestate.bank_schedule_missing`).

**Avertissements** : `projection_uses_last_rate`, `schedule_mismatch`, `event_off_payment_date`, `exceeds_total_payments`, `override_ignored_after_early_repayment`, `visibility_mismatch` (bien et prêt).

## 12. Tests

| Test | Vérifie |
|---|---|
| **Cas doré** (§4) | Toutes les valeurs du tableau : mensualité, échéances 1, 60, 120, 240, intérêts totaux, après 60 échéances |
| Prêt à taux zéro | Différé total puis taux 0 : 222,22 € et dernière échéance 222,62 € |
| Différé partiel | Mensualité égale aux intérêts, capital inchangé |
| Taux variable | Plusieurs phases ; projection au dernier taux avec avertissement |
| Paliers et modulation | `payment_override` par phase ; prolongation au-delà de `total_payments` avertie |
| **Remboursement anticipé** | Les deux effets, avec les valeurs du §4 |
| Arrondis | `half_up` et `truncate` ; première période au prorata |
| Assurance | Les trois modes |
| **Comparaison avec la banque** | Tableau synthétique : écarts de 0,01 € tolérés ; un désaccord de taux détecté à la bonne date |
| Bien et prêt | Valeur nette avec deux prêts ; répartition par personne avec parts différentes |
| Import | Aller-retour export puis import identique |

**Propriétés** (générateur de prêts valides) :

- le capital restant dû n'est jamais négatif et vaut 0 à la fin ;
- Σ capitaux = emprunté + intérêts capitalisés − remboursements anticipés ;
- en annuité, les intérêts par échéance sont non croissants ;
- coût total du crédit = Σ mensualités − emprunté + Σ assurance + Σ frais ;
- la simulation d'un anticipé de montant 0 ne change rien.

Vecteurs de référence : `tests/vectors/loans/` (générateur Python en décimal à 60 chiffres, 220 prêts comparés au centime).

## 13. Limites

- **TAEG** hors v1 (§8).
- **Pas de suivi des paiements réels** : les échéances sont supposées payées comme prévu. Le tableau de la banque corrige le passé.
- Le futur d'un taux variable est une **projection**, pas une prévision.
- **Pas de conseil** : les pénalités de remboursement anticipé sont saisies, jamais calculées d'après un plafond légal.
- Le décompte des jours est fondé sur `taux annuel ÷ 12` ; le prorata du premier mois utilise Actual/365. D'autres conventions de banque sont absorbées par `payment_override` et `opening_balance_override`.

## 14. Questions ouvertes

- ~~Faut-il un `payment_day` explicite distinct de `first_payment_date` ?~~ **Tranché en 3A2 (2026-10-12) : non.** Les échéances suivent `first_payment_date`, le jour d’ancrage ramené à la fin du mois quand il manque.
- Représentation d'un prêt à paliers d'un grand nombre de marches (import d'un tableau plutôt que saisie de phases ?).
- Simulation de renégociation : à ajouter dès l'étape 3 ou plus tard ?
- Mesure de l'écart toléré (1 € sur le capital restant dû) sur de vrais tableaux.
