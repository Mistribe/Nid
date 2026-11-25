# Modèle de données du module Autres biens

> Statut : livré avec la #186. Décrit la table, les règles, la valeur à une date et la contribution au patrimoine du module `Nid.Assets`.
> Références : `core-data-model.md`, `goals-data-model.md` (même structure), `import-format.md` §5.10, `architecture.md` §5.

## 1. Vue d'ensemble

Un bien est un `item` du Core (module `assets`, genre `asset`) : nom, détenteurs, visibilité, devise et archivage viennent du registre. Ses valeurs datées sont des valorisations du Core (`IValuations`). Le module ne garde que ce que le registre ne dit pas.

Genres : `vehicle`, `precious_metal`, `art`, `collectible`, `other`. Seul `precious_metal` porte un poids et un titre ; seul `vehicle` porte une mise en circulation, un kilométrage (avec la date du relevé), une puissance fiscale et une énergie.

## 2. Table (schéma `assets`)

| Table | Champs | Contraintes |
|---|---|---|
| `asset` | `item_id` (PK), `kind`, `purchase_price` numeric?, `purchased_on` date?, `note` text?, `weight_grams` numeric?, `fineness` numeric(5,1)?, `registered_on` date?, `mileage_km` int?, `mileage_on` date?, `fiscal_hp` smallint?, `energy` text?, colonnes d'import (`import_ref`, `import_hash`, `imported_by`), `xmin` (version) | `ck_asset_purchase` : prix et date ensemble ou ni l'un ni l'autre ; `ck_asset_metal` : poids et titre seulement pour `precious_metal` ; `ck_asset_vehicle` : champs du véhicule seulement pour `vehicle` ; `ck_asset_mileage` : kilométrage et date ensemble ou ni l'un ni l'autre ; `ck_asset_import` : marques d'import cohérentes |

## 3. Règles

- **Une valeur est toujours présente** : un prix d'achat (avec sa date) ou au moins une valorisation, sinon `assets.value_required`. À l'import, la règle ne regarde que le fichier : les valorisations déjà enregistrées ne la satisfont pas (`import-format.md` §5.10).
- **Achat** : prix et date ensemble ou ni l'un ni l'autre (`assets.purchase_incomplete`) ; la date n'est pas future.
- **Métal** : poids et titre seulement pour `precious_metal` (`assets.metal_only`) ; poids > 0 ; titre en ‰, de 0 (exclu) à 1 000, une décimale (`999.9` est conservé tel quel).
- **Véhicule** : les champs du véhicule seulement pour `vehicle` (`assets.vehicle_only`) ; mise en circulation et date du relevé non futures (`assets.invalid_date`) ; kilométrage de 0 à moins de 10 000 000 (`assets.invalid_mileage`), avec sa date ou sans aucun des deux (`assets.mileage_incomplete`) ; puissance fiscale de 1 à 99 (`assets.invalid_fiscal_hp`) ; énergie parmi `petrol`, `diesel`, `hybrid`, `plug_in_hybrid`, `electric`, `lpg`, `other` (`assets.invalid_energy`). Mettre à jour le kilométrage est une modification qui remplace le relevé et sa date (pas d'historique).
- **Location (LOA, LLD)** : pas un bien. L'écran de création propose de la suivre comme un abonnement (`/subscriptions/new?name=…&note=…`) et ne crée rien.
- **Abonnements liés** (#648) : un abonnement (assurance, entretien…) se lie à un bien par `subscriptions.subscription.asset_item_id` (un bien par abonnement, plusieurs abonnements par bien ; pas de clé étrangère entre modules). Le lien s'écrit depuis l'abonnement (`assetId` à la création, `PUT /api/v1/subscriptions/{id}/asset` `{ assetId | null }`, `subscriptions.invalid_asset` si le bien n'est pas lisible ou n'est pas un bien) ; la fiche du bien (`subscriptions`) liste ceux que le lecteur voit, avec prix, période et prochaine échéance, par le port `ISubscriptionsOfItems` des contrats Abonnements. Supprimer le bien ne bloque pas : le lien tombe, l'abonnement reste.
- **Modification** : remplace tous les champs. Passer d'un métal ou d'un véhicule à un autre genre efface les champs qui ne le concernent plus.
- **Clôture** : archiver un bien le tient pour vendu (voir §5) ; supprimer (après aperçu) efface un bien créé par erreur, avec ses valeurs.
- Les valorisations se posent par date : une valeur à une date déjà valorisée la remplace.

## 4. Valeur à une date

`AssetMath.At(date)` : la dernière valorisation au plus tard à `date` ; sinon le prix d'achat à partir de sa date ; sinon aucune valeur. Un bien sans valeur lisible compte comme « sans valeur » dans le tableau de bord, jamais comme zéro silencieux.

## 5. Patrimoine

`AssetsNetWorth` contribue au tableau de bord : une ligne par bien visible, à sa dernière valeur. Chaque part de répartition est `Other` et `Locked`, jamais devinée (`estimated = false`).

- **Depuis le 1er janvier**, le gain est le changement de valeur. Un bien compté pour la première fois cette année entre dans les valeurs de départ de l'année à son premier montant compté : son achat n'est jamais un gain (il passe par `Moved`), et il n'est jamais listé « sans valeur au 1er janvier », contrairement à un bien immobilier.
- **Historique** : à chaque date, la dernière valeur connue ; `Moved` porte les achats (le prix, dès le premier jour compté) et les ventes (moins la dernière valeur comptée, à partir du jour d'archivage). Un bien archivé compte strictement avant son jour d'archivage.
- Aucun versement : un bien s'achète, il ne se verse pas (`NetContributions` vide).

## 6. Import, export, assistant

Section d'import `assets` (`import-format.md` §5.10), ses valorisations dans `valuations` et le champ `asset` des abonnements (§5.3). L'export la reprend. Les outils MCP lisent, créent et valorisent les biens.
