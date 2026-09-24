# Banc anti-fuite et tests d'accès

> Statut : brouillon. Comment on prouve, en CI, qu'aucun élément privé ne fuit.
> Références : `README.md` (principe 4), `authorization.md` (règles), `../architecture.md` §13 (stratégie de tests).

---

## 1. Objectif

Le filtrage est applicatif (pas de Row-Level Security). La garantie repose donc sur des tests qui **échouent automatiquement** quand une requête, une commande, un outil MCP ou un export laisse passer un élément qu'il ne devrait pas.

## 2. Jeu de données mixte

Deux adultes A (admin) et B, un dépendant D. Chaque élément porte des valorisations, des flux, des documents et des propositions.

| Élément | Propriétaires | Visibilité | `Readers` | `Writers` |
|---|---|---|---|---|
| `S-A` | A | partagé | A, B | A |
| `S-B` | B | partagé | A, B | B |
| `J-AB` | A + B | partagé | A, B | A, B |
| `P-A` | A | privé | A | A |
| `P-B` | B | privé | B | B |
| `D` | D | partagé | A, B | A, B |
| `M-AD` | A + D | partagé | A, B | A, B |

Le jeu inclut au moins un élément de chaque `module` / `kind`, pour couvrir chaque requête.

## 3. Banc d'exécution

1. **Découverte** : le banc énumère toutes les requêtes enregistrées, via les `RegisterGroup` générés par Synapse.
2. **Requêtes de lecture** : chacune est exécutée en tant que A, puis en tant que B, avec les portées `read` puis complètes.
   - Aucun `ItemId` hors de `IVisibleItems.For(acteur)` ne doit apparaître dans le résultat.
   - Aucun libellé d'un élément privé de l'autre ne doit apparaître (recherche textuelle dans la réponse sérialisée).
3. **Commandes sur élément** : chacune est exécutée sur chaque élément du jeu par chaque acteur. Le résultat attendu est calculé par les fonctions `Readers` / `Writers` : succès, `403 forbidden` ou `404 not_found`.
4. **Portées** : chaque commande est rejouée avec `read`, `write`, `write:direct` ; résultat attendu : refus, conversion en `Proposal`, ou exécution directe (`authorization.md` §3).
5. **Rôle admin** : chaque requête `IAdminOnly` est exécutée par un adulte non admin (résultat attendu : `403 platform.admin_required`), par un admin et par un jeton d'admin (refusé : un jeton n'est jamais admin). Exécutée par un admin, sa réponse sérialisée ne contient **aucun libellé d'élément** ; le jeu de données donne à A le rôle admin, pour prouver que ce rôle n'ouvre pas `P-B`.
6. **Autres surfaces**, passées dans la même boucle :
   - les outils MCP ;
   - l'export (`../import-format.md` §10) ;
   - la construction du résumé mensuel de chaque adulte ;
   - le journal d'audit et la liste des propositions ;
   - les documents (lecture du contenu et des métadonnées).

## 4. Tests de propriétés sur `Readers` et `Writers`

Générateur d'éléments respectant les invariants du Core (`../core-data-model.md` §4.2). Propriétés vérifiées :

- `private` ⇒ `Readers` = {le propriétaire}.
- `Writers ⊆ Readers`.
- Un élément avec un propriétaire `dependent` ⇒ les deux adultes écrivent.
- Un adulte ne peut jamais s'ajouter comme propriétaire à l'élément de l'autre (`ChangeOwners`).
- Passer `private` → `shared` puis `shared` → `private` (propriétaire unique) rend un élément à l'état initial du point de vue des lecteurs.
- Les totaux du foyer ne dépendent d'aucun élément privé.
- `Readers` et `Writers` ne dépendent pas de `is_admin` : promouvoir ou rétrograder un adulte ne change aucun résultat.

## 5. Garde de sortie

- Test dédié : une requête factice qui renvoie un `ItemId` hors de l'ensemble visible doit produire `500`, aucune donnée dans la réponse, un événement `access_violation` et l'envoi de l'alerte.
- Ce test protège le mécanisme lui-même : sans lui, la garde pourrait être désactivée par erreur sans que rien n'échoue.

## 6. En CI

- Le banc est **bloquant**.
- Une nouvelle requête est **automatiquement incluse** par la découverte ; elle échoue si elle n'a pas de marqueur (`authorization.md` §4).
- Test d'architecture : les requêtes de liste implémentent `IExposesItems`.

## 7. Limites

- Le banc prouve la conformité aux règles `Readers` / `Writers` ; il ne prouve pas que ces règles sont les bonnes. Elles sont relues avec la matrice du Core (`../core-data-model.md` §4.3).
- Un contenu de fichier (PDF) n'est pas analysé : seul l'accès au document est vérifié.
- Les fuites par canal auxiliaire (temps de réponse, taille de réponse) sont hors périmètre.
