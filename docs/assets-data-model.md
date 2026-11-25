# Modèle de données du module Autres biens

> Statut : livré avec la #186. Décrit la table, les règles, la valeur à une date et la contribution au patrimoine du module `Nid.Assets`.
> Références : `core-data-model.md`, `goals-data-model.md` (même structure), `import-format.md` §5.10, `architecture.md` §5.

## 1. Vue d'ensemble

Un bien est un `item` du Core (module `assets`, genre `asset`) : nom, détenteurs, visibilité, devise et archivage viennent du registre. Ses valeurs datées sont des valorisations du Core (`IValuations`). Le module ne garde que ce que le registre ne dit pas.

Genres : `vehicle`, `precious_metal`, `art`, `collectible`, `other`. Seul `precious_metal` porte un poids et un titre ; seul `vehicle` porte une mise en circulation, un kilométrage (avec la date du relevé), une puissance fiscale, une énergie et une perte de valeur annuelle.

## 2. Table (schéma `assets`)

| Table | Champs | Contraintes |
|---|---|---|
| `asset` | `item_id` (PK), `kind`, `purchase_price` numeric?, `purchased_on` date?, `note` text?, `weight_grams` numeric?, `fineness` numeric(5,1)?, `registered_on` date?, `mileage_km` int?, `mileage_on` date?, `fiscal_hp` smallint?, `energy` text?, `depreciation_rate` numeric(5,2)?, colonnes d'import (`import_ref`, `import_hash`, `imported_by`), `xmin` (version) | `ck_asset_purchase` : prix et date ensemble ou ni l'un ni l'autre ; `ck_asset_metal` : poids et titre seulement pour `precious_metal` ; `ck_asset_vehicle` : champs du véhicule seulement pour `vehicle` ; `ck_asset_depreciation` : taux de 0 (exclu) à 100 (exclu) ; `ck_asset_mileage` : kilométrage et date ensemble ou ni l'un ni l'autre ; `ck_asset_import` : marques d'import cohérentes |

## 3. Règles

- **Une valeur est toujours présente** : un prix d'achat (avec sa date) ou au moins une valorisation, sinon `assets.value_required`. À l'import, la règle ne regarde que le fichier : les valorisations déjà enregistrées ne la satisfont pas (`import-format.md` §5.10).
- **Achat** : prix et date ensemble ou ni l'un ni l'autre (`assets.purchase_incomplete`) ; la date n'est pas future.
- **Métal** : poids et titre seulement pour `precious_metal` (`assets.metal_only`) ; poids > 0 ; titre en ‰, de 0 (exclu) à 1 000, une décimale (`999.9` est conservé tel quel).
- **Véhicule** : les champs du véhicule seulement pour `vehicle` (`assets.vehicle_only`) ; mise en circulation et date du relevé non futures (`assets.invalid_date`) ; kilométrage de 0 à moins de 10 000 000 (`assets.invalid_mileage`), avec sa date ou sans aucun des deux (`assets.mileage_incomplete`) ; puissance fiscale de 1 à 99 (`assets.invalid_fiscal_hp`) ; énergie parmi `petrol`, `diesel`, `hybrid`, `plug_in_hybrid`, `electric`, `lpg`, `other` (`assets.invalid_energy`) ; perte de valeur par an (`depreciation_rate`) de 0 à 100 exclus, deux décimales au plus (`assets.invalid_depreciation_rate`), sans valeur par défaut. Mettre à jour le kilométrage est une modification qui remplace le relevé et sa date (pas d'historique).
- **Location (LOA, LLD)** : pas un bien. L'écran de création propose de la suivre comme un abonnement (`/subscriptions/new?name=…&note=…`) et ne crée rien.
- **Abonnements liés** (#648) : un abonnement (assurance, entretien…) se lie à un bien par `subscriptions.subscription.asset_item_id` (un bien par abonnement, plusieurs abonnements par bien ; pas de clé étrangère entre modules). Le lien s'écrit depuis l'abonnement (`assetId` à la création, `PUT /api/v1/subscriptions/{id}/asset` `{ assetId | null }`, `subscriptions.invalid_asset` si le bien n'est pas lisible ou n'est pas un bien) ; la fiche du bien (`subscriptions`) liste ceux que le lecteur voit, avec prix, période (`every`, comme les phases) et prochaine échéance, par le port `ISubscriptionsOfItems` des contrats Abonnements. Supprimer le bien ne bloque pas : le lien tombe, l'abonnement reste.
- **Frais d'un véhicule** (#649) : ce sont des flux du Core (`IFlows`) sur le bien, de genre `expense`, de catégorie `service`, `repair`, `tyres` ou `other` (`assets.invalid_category` sinon), `FactConvention` du module. Seul un `vehicle` en reçoit (`assets.costs_vehicle_only`, à la saisie et à la correction) ; un bien qui change de genre garde les frais déjà saisis (historique, supprimables, exportés) mais n'en prend plus. Montant > 0 avec deux décimales (`assets.invalid_value`), date non future (`core.date_in_future`), note de 2 000 caractères au plus. `GET /api/v1/assets/{id}/costs?year=` répond `{ year, rows, total, years }` : les frais de l'année (celle du foyer par défaut), du plus récent au plus ancien, leur total calculé par le serveur (`IFlows.SumAsync`) et les années qui ont des frais. `POST` / `PUT …/{flowId}` / `DELETE …/{flowId}` écrivent ; une correction remplace le flux (nouvel `id`) et garde sa référence `bank:` et sa facture sauf si elle en donne une autre ou demande `clearDocument: true` (« Aucune facture »). `GET /api/v1/assets/{id}` porte `hasCosts` : un bien qui n'est plus un véhicule mais garde des frais (ou en a reçu d'un fichier) montre encore l'onglet « Frais », en lecture et suppression seulement. La **facture** est `documentId`, un document que l'auteur lit (sinon 404 `document`) ; la fiche montre le lien aux seuls lecteurs du document. La fiche dépose le fichier sur le véhicule (rôle `invoice`) avant de poser `documentId`.
- **Frais depuis une opération bancaire** (#649) : une opération de dépense se relie à un véhicule que la personne écrit (`POST /api/v1/banking/transactions/{id}/link` avec `module: assets`, `category` facultative, `other` par défaut ; `propose_bank_link` porte la même `category`). Le frais est **enregistré au moment du lien**, au nom de la personne, quel que soit l'âge de l'opération et ceux qui lisent le véhicule : `externalRef = bank:{lineId}` (unique par bien dans le Core), clé du lien `flow:{id}` (`FlowTakenAsync` le protège). Une opération n'est donc jamais liée sans son frais, ni saisie deux fois : un frais déjà saisi (même montant, à dix jours près, sans autre opération liée) est seulement lié, et un second lien de la même opération est refusé (`banking.transaction_linked`). Un refus du frais (devise que le véhicule ne tient pas) empêche le lien. Aucune règle de libellé n'est créée pour un véhicule, aucune proposition n'est faite. Banque ne lit que `Nid.Assets.Contracts` (`IWritableVehicles`).
- **Modification** : remplace tous les champs. Passer d'un métal ou d'un véhicule à un autre genre efface les champs qui ne le concernent plus.
- **Clôture** : archiver un bien le tient pour vendu (voir §5) ; supprimer (après aperçu) efface un bien créé par erreur, avec ses valeurs.
- Les valorisations se posent par date : une valeur à une date déjà valorisée la remplace.

## 4. Valeur à une date

`AssetMath.At(date)` : la dernière valorisation au plus tard à `date` ; sinon le prix d'achat à partir de sa date ; sinon aucune valeur. Un bien sans valeur lisible compte comme « sans valeur » dans le tableau de bord, jamais comme zéro silencieux.

**Estimation (#187)** : avec un taux r (% par an, véhicule seulement), la valeur à la date d est `base × (1 − r/100)^(jours / 365)`, où la base est la dernière valorisation au plus tard à d (sinon le prix d'achat) et `jours` l'écart entre la date de la base et d. Arrondi au centime, moitié loin de zéro, jamais au-dessus de la base ; une valorisation plus récente redémarre le calcul. `AssetValue` porte alors `Estimated` et `BaseAmount`, et sa date reste celle de la base. Un bien sans taux garde la règle ci-dessus.

## 5. Patrimoine

`AssetsNetWorth` contribue au tableau de bord : une ligne par bien visible, à sa dernière valeur. Chaque part de répartition est `Other` et `Locked`, jamais devinée (`estimated = false`), sauf un véhicule avec un taux de perte de valeur : sa ligne est alors une estimation (`estimated = true`, `asOf` = aujourd'hui, `valuedOn` = date de la base), et l'historique suit l'estimation à chaque date (la baisse est un changement de valeur ; `Moved` reste la première valeur comptée).

- **Depuis le 1er janvier**, le gain est le changement de valeur. Un bien compté pour la première fois cette année entre dans les valeurs de départ de l'année à son premier montant compté : son achat n'est jamais un gain (il passe par `Moved`), et il n'est jamais listé « sans valeur au 1er janvier », contrairement à un bien immobilier.
- **Historique** : à chaque date, la dernière valeur connue ; `Moved` porte les achats (le prix, dès le premier jour compté) et les ventes (moins la dernière valeur comptée, à partir du jour d'archivage). Un bien archivé compte strictement avant son jour d'archivage.
- Aucun versement : un bien s'achète, il ne se verse pas (`NetContributions` vide).

## 6. Import, export, assistant

Section d'import `assets` (`import-format.md` §5.10), ses valorisations dans `valuations` et le champ `asset` des abonnements (§5.3). L'export la reprend. Les frais d'un véhicule voyagent dans la section `flows` du Core (`item` = `id` du bien ; `expense` et une catégorie de §3 seulement ; la facture, un document, n'est pas exportée avec le flux). Un flux importé sur un bien qui n'est pas un véhicule n'est pas refusé : la convention connaît le genre d'élément, pas le genre de bien. Les outils MCP lisent (`list_asset_costs`), créent et valorisent les biens, et proposent les frais (`add_asset_cost`, `correct_asset_cost`, `delete_asset_cost`).
