# Autorisation

> Statut : brouillon. Comment l'accès à chaque élément est décidé : acteur, portées, pipeline, règles de lecture et d'écriture, garde de sortie.
> Références : `README.md`, `../core-data-model.md` §4 (matrice de droits, propriété), `../architecture.md` §5.4 (Synapse, pipelines), `access-tests.md` (vérification).

---

## 1. Acteur

Chaque requête Synapse s'exécute pour un `Actor`, posé dans le contexte par un comportement de pipeline. Les handlers ne lisent jamais `HttpContext`.

| Champ | Valeurs |
|---|---|
| `kind` | `person` (adulte), `system` (tâche planifiée, éventuellement au nom d'une personne), `worker` |
| `person_id` | L'adulte concerné ; vide pour une tâche sans données de foyer (taux de change, catalogue) |
| `via` | `ui`, `api`, `mcp`, `job`, `import` |
| `scopes` | `read`, `write`, `write:direct` |
| `is_admin` | Vrai pour une session navigateur dont la `person` est admin, et pour `system` ; **toujours faux pour un jeton**, même celui d'un admin |

Une session navigateur porte toutes les portées ; un jeton (`tokens-and-credentials.md`) en porte un sous-ensemble. Un `dependent` n'est jamais un acteur.

`is_admin` est relu en base à chaque requête, avec le contrôle de `session_epoch` (`authentication.md` §3) : rien du rôle n'est dans le cookie. Il **n'ouvre aucun élément** : `Readers` et `Writers` (§5) l'ignorent.

## 2. Pipeline

Ordre d'exécution des comportements (par `IOrderedPipelineBehavior.Order`, valeur basse = extérieur) :

| Étape | Rôle |
|---|---|
| 1. Trace et journal | Identifiant de requête, OpenTelemetry |
| 2. Acteur | Session, jeton ou tâche → `Actor` |
| 2 bis. Admin | Une requête `IAdminOnly` d'un acteur non admin est refusée : `403 admin_required` |
| 3. Portée | Voir §3 |
| 4. Accès | Contrôle sur l'élément visé (§4 et §5) |
| 5. Idempotence | `Idempotency-Key` |
| 6. Validation, transaction, handler | |
| 7. Garde de sortie | Voir §7 |

**Vérifié** (`../spikes.md` §3) : l'ordre suit `Order`, quel que soit l'ordre d'enregistrement, et un comportement peut court-circuiter. Les comportements communs sont déclarés par le host (`[assembly: SynapseGlobalBehavior]` et son propre `RegisterGroup` enregistré) : `[PipelineBehavior]` ne se propage que vers les assemblies référencées, pas vers les modules qui référencent `Platform`.

## 3. Portées

| Requête | Exige |
|---|---|
| Requête de lecture | `read` |
| Commande | `write` |
| Exécution **directe** d'une commande | `write:direct` |

- `write:direct` inclut `write`, qui inclut `read`.
- Sans `write:direct`, une commande marquée `IProposable` est **convertie en `Proposal`** : le contrôle d'accès de l'étape 4 s'applique comme pour une écriture directe. Cette règle porte la décision D10 (`../architecture.md` §3).
- Une commande non proposable sans `write:direct` est refusée : `403 insufficient_scope`.

## 4. Marqueurs de requête

Définis dans `Core.Contracts`.

| Marqueur | Contrôle |
|---|---|
| `IItemScoped { ItemId, Access }` | `Access` : `Read`, `Write`, `ChangeVisibility`, `ChangeOwners`. Vérifié à l'étape 4 |
| `ICreatesItem { Owners, Visibility }` | Vérifié à la création (§5) |
| `IHouseholdQuery` | Requête de liste : le handler filtre avec `IVisibleItems` (§6) |
| `[NoItemAccess]` | Données de référence : catalogue, taux, réglages |
| `IAdminOnly` | Réservé aux admins (étape 2 bis) : zone admin, saisie manuelle et synchronisation des taux, journal des collectes. Ne touche aucun élément |

Un **test d'architecture** exige que toute requête porte un marqueur ou l'exemption explicite : un oubli fait échouer la CI.

## 5. Règles d'accès

Source unique : deux fonctions pures, testées par propriétés (`access-tests.md`).

| Fonction | Définition |
|---|---|
| `Readers(item)` | `private` → le propriétaire seul ; sinon les deux adultes |
| `Writers(item)` | Un propriétaire `dependent` → les deux adultes ; sinon `private` → le propriétaire ; sinon un seul propriétaire → lui ; sinon → tous les propriétaires |

| Opération | Règle |
|---|---|
| Lire | `Readers` |
| Écrire (valorisation, flux, modification, archivage) | `Writers` |
| **Créer** un élément | L'acteur est l'un des propriétaires, ou tous les propriétaires sont des `dependent`. On ne crée pas un élément au seul nom de l'autre adulte |
| **Changer les propriétaires** | L'acteur est dans `Writers` de l'état **actuel** ; le résultat respecte les invariants du Core. Un adulte ne peut jamais s'ajouter lui-même à l'élément de l'autre. Se retirer d'un élément est un transfert explicite (confirmation dans l'interface) |
| Changer la visibilité | Le propriétaire seul (`../core-data-model.md` §4.3). `private` → `shared` révèle l'élément à l'autre immédiatement |
| Proposition | Création : mêmes droits que l'écriture directe sur la cible. Lecture : `Readers` de la cible. **Décision : `Writers`** de la cible. Sans cible : la personne concernée seule |
| Document | Rattacher à un élément : `Writers`. Lire : le déposant et les `Readers` des éléments liés |

## 6. Erreurs et listes

**Existence non divulguée**

| Situation | Réponse |
|---|---|
| Élément qu'on ne peut pas lire | `404 not_found`, comme s'il n'existait pas |
| Élément lisible mais non modifiable | `403 forbidden` |
| Portée de jeton insuffisante | `403 insufficient_scope` |
| Requête réservée aux admins, acteur non admin | `403 admin_required` (pas de `404` : la zone admin est la même pour tous les foyers, rien à cacher) |

**Listes filtrées**
- `IVisibleItems.For(personne, portée)` renvoie les identifiants lisibles : `Household` (éléments partagés seulement) ou `Me` (partagés et mes privés). Les modules filtrent avec `WHERE item_id = ANY(@ids)` ; le volume est de quelques centaines d'éléments.
- Chaque DTO d'élément porte `access: { can_write }`, calculé **en lot** par Core (pas d'appel par ligne). L'interface l'utilise pour l'état lecture seule.

## 7. Garde de sortie

- Les réponses de requêtes implémentent `IExposesItems` (liste des `ItemId` renvoyés). Un comportement vérifie qu'ils appartiennent tous à l'ensemble visible.
- Si l'un n'y est pas : **aucune donnée n'est renvoyée**, la requête échoue en `500`, un événement `access_violation` est journalisé et un email « critique » part aux deux adultes.
- C'est une **seconde barrière**, pas la première : le contrôle de l'étape 4 et le filtrage des listes restent la règle.

## 8. Autres vecteurs de fuite

| Vecteur | Traitement |
|---|---|
| Totaux du foyer | Excluent les éléments privés ; jamais calculés par « total moins partagé » |
| Résumé mensuel | Construit **au nom de chaque adulte** (acteur `system` avec `person_id`) et envoyé séparément |
| Journal d'audit | Filtré par les mêmes règles (`administration-and-audit.md` §4) |
| Export | Applique déjà la visibilité (`../import-format.md` §10) |
| Journaux techniques | Ni libellés ni montants au niveau info |
| Tableau de bord, MCP | Mêmes requêtes que l'API, donc mêmes contrôles |
| Zone admin | Aucun libellé, aucun montant, **aucun compteur d'éléments** (un total révélerait l'existence d'éléments privés de l'autre adulte) |

## 9. Jobs, workers et import

- Les tâches de maintenance (taux, catalogue) n'ont pas de `person_id` et aucun accès aux éléments.
- **Extraction** : le worker rend son résultat à l'API interne ; Ingestion crée la `Proposal` pour la personne qui a créé le job et **revérifie le droit d'écriture** sur la cible au moment de la création.
- **Import** : exécuté via l'interface ou l'API avec `write:direct` (`../import-format.md`).
