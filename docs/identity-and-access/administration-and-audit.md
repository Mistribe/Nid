# Administration et audit

> Statut : brouillon. Commandes d'administration, événements de sécurité, frontière de confiance.
> Références : `README.md`, `authentication.md` (récupération), `tokens-and-credentials.md`, `../architecture.md` §6 (audit) et §12 (observabilité).

---

## 1. Frontière de confiance

La confidentialité des éléments privés vaut **entre les deux adultes, pas contre l'hébergeur**.

- Qui a accès à la base, aux sauvegardes, aux journaux ou au shell du conteneur voit tout.
- **Aucun chiffrement applicatif** des éléments privés en v1. Un tel chiffrement (par personne) est hors périmètre ; il compliquerait les calculs, l'export et l'import.
- La protection du disque et des sauvegardes (chiffrement, contrôle d'accès) relève de l'hébergeur (`../architecture.md` §11).

À dire clairement dans la documentation destinée à l'hébergeur.

## 2. Commandes d'administration

CLI exécutée dans le conteneur `nid-api` par l'hébergeur : `nid admin <commande>`. **Aucune API HTTP** pour ces opérations.

| Commande | Usage |
|---|---|
| `rebind-subject --person <nom ou id> --sub <nouveau>` | Rattacher un adulte à un nouvel `sub` OIDC (`authentication.md` §5) |
| `list-persons` | Lister les personnes (identifiant, type, état) |
| `revoke-sessions --person` | Incrémenter `session_epoch` |
| `revoke-tokens --person` | Révoquer tous les jetons d'accès personnels |
| `create-worker --name --job-types` | Créer un identifiant de worker ; affiche le secret une fois |
| `rotate-worker`, `revoke-worker` | Rotation et révocation |

Chaque commande écrit une entrée d'audit `admin_command` (`via: admin-cli`, paramètres sans secret).

## 3. Audit

Le journal d'audit (Platform, `../architecture.md` §6) reçoit ces champs supplémentaires :

| Champ | Contenu |
|---|---|
| `actor_kind` | `person`, `system`, `worker` |
| `person_id` | La personne concernée |
| `via` | `ui`, `api`, `mcp`, `job`, `import`, `admin-cli` |
| `scopes` | Portées de l'acteur |
| `token_id?` | Jeton utilisé |
| `on_behalf_of?` | Personne au nom de qui agit une tâche |
| `request_id` | Corrélation avec les traces |

### 3.1 Événements de sécurité

| Événement | Quand |
|---|---|
| `login`, `logout` | Ouverture et fin de session |
| `login_refused` | Plafond d'adultes atteint, `sub` inconnu |
| `session_revoked` | « Déconnecter partout » ou commande d'administration |
| `token_created`, `token_revoked`, `token_expired` | Cycle de vie des jetons |
| `token_auth_failed` | Échec d'authentification par jeton (fréquence limitée) |
| `access_denied` | `403` ou `404` sur un élément |
| `access_violation` | La garde de sortie a bloqué une réponse (`authorization.md` §7) |
| `admin_command` | Commande d'administration |

### 3.2 Visibilité

- Les entrées liées à un **élément** suivent les règles d'accès de cet élément (`Readers`).
- Les événements de sécurité d'une **personne** ne sont visibles que d'elle.
- `access_violation` fait exception : il contient des identifiants seulement, pas de libellé, et **les deux adultes** en sont avertis par email « critique ».
- L'audit des écritures est conservé indéfiniment (volume minuscule) ; les événements de sécurité au moins un an.

## 4. Page « Sécurité »

Dans l'interface, pour chaque adulte :

- **Mes jetons** : créer (nom, préréglage, expiration), révoquer.
- **Mes sessions** : dernière connexion, « déconnecter partout » (avec l'option de révoquer aussi les jetons).
- **Mes derniers événements de sécurité**.
- Un rappel : la déconnexion de Nid ne ferme pas la session Authelia (`authentication.md` §6).

## 5. Alertes

| Événement | Canal |
|---|---|
| `access_violation` | Email « critique » aux deux adultes |
| Jeton proche de l'expiration | Email à son propriétaire, 7 jours avant |
| Échecs d'authentification répétés | Métrique et journal ; pas d'email en v1 |
