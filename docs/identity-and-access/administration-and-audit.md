# Administration et audit

> Statut : brouillon. Rôle admin et zone admin, commandes d'administration, événements de sécurité, frontière de confiance.
> Références : `README.md`, `authentication.md` (récupération), `tokens-and-credentials.md`, `../architecture.md` §6 (audit) et §12 (observabilité).

---

## 1. Frontière de confiance

La confidentialité des éléments privés vaut **entre les deux adultes, pas contre l'hébergeur**.

- Qui a accès à la base, aux sauvegardes, aux journaux ou au shell du conteneur voit tout.
- **Aucun chiffrement applicatif** des éléments privés en v1. Un tel chiffrement (par personne) est hors périmètre ; il compliquerait les calculs, l'export et l'import.
- La protection du disque et des sauvegardes (chiffrement, contrôle d'accès) relève de l'hébergeur (`../architecture.md` §11).

À dire clairement dans la documentation destinée à l'hébergeur.

## 2. Rôle admin et zone admin

### 2.1 Rôle

| Règle | Détail |
|---|---|
| Porteur | `person.is_admin` (schéma `core`) |
| Premier utilisateur | La première `person` créée (base sans adulte) est admin (`authentication.md` §5) |
| Plusieurs admins | Un admin promeut ou rétrograde un autre adulte, y compris en attente ; **le dernier admin ne peut pas être rétrogradé** (`core.last_admin`) — « dernier admin » compte seulement les admins **actifs** : rétrograder le dernier admin actif est refusé même s'il existe un admin en attente, qui ne peut agir avant sa première connexion |
| Périmètre | Technique seulement : n'élargit jamais `Readers` ni `Writers` (`authorization.md` §1) |
| Canal | La session navigateur seulement ; un jeton d'accès personnel n'est jamais admin (principe 5) |
| Contrôle | Marqueur `IAdminOnly`, étape « Admin » du pipeline, `403 platform.admin_required` (`authorization.md` §2 et §4) |
| Interface | `capabilities.admin` pilote l'affichage de la zone (`../frontend/screens.md` §1) |

### 2.2 Zone admin

API `/api/v1/admin`, écrans `/admin/*` (`../frontend/screens.md` §12).

| Section | Contenu | API |
|---|---|---|
| **Utilisateurs** | Adultes (en attente ou actifs, admin ou non) ; pré-enregistrer un adulte (nom, email) ; annuler un adulte en attente (`DELETE /admin/users/{id}` répond `200 { id }`) ; promouvoir, rétrograder ; « déconnecter partout » un adulte (incrémente son `session_epoch` ; `POST /admin/users/{id}/revoke-sessions` répond `200` avec l'adulte) | `GET/POST /admin/users`, `DELETE /admin/users/{id}`, `PUT /admin/users/{id}/admin`, `POST /admin/users/{id}/revoke-sessions` |
| **Foyer** | Plafond d'adultes (`max_adults`, au moins 1 et au moins le nombre d'adultes), devise de référence | `GET/PATCH /admin/household` |
| **Taux de change** | Saisie manuelle, synchronisation à la demande, journal des collectes | `/api/v1/core/fx-rates` (écritures et journal réservés aux admins ; la lecture des taux reste ouverte) |
| **Tâches planifiées** | Jobs Quartz : dernière et prochaine exécution, état | `GET /admin/jobs` |
| **Système** | Version, démarrage, santé (base, fournisseur d'identité), taille de la base | `GET /admin/system` |
| **Journal de sécurité** (1F) | Événements techniques globaux (`login_refused`, `access_violation`, `admin_command`, §4.1), identifiants seulement | avec le journal d'audit |
| **Plus tard** | Catalogue (1C2b), IA : fournisseurs, clés, budget (étape 4), identifiants des workers (étape 4) | |

- **Aucun libellé, aucun montant, aucun compteur d'éléments** : un total d'éléments révélerait que l'autre adulte en a de privés.
- Supprimer un adulte **actif** est hors périmètre : il possède des éléments, cela demanderait un transfert de propriété. Un adulte **en attente** qui possède déjà des éléments (atteint par un import, qui résout les adultes par email y compris en attente) ne peut pas non plus être supprimé, pour la même raison (`core.person_active`).
- Chaque action d'écriture est confirmée dans l'interface et journalisée (§4.1).

## 3. Commandes d'administration (CLI)

La zone admin couvre l'administration courante. La CLI reste le **recours de secours**, pour ce que l'interface ne peut pas faire (plus aucun admin joignable, `sub` changé) et pour les identifiants de worker. Elle s'exécute dans le conteneur `nid-api` par l'hébergeur : `nid admin <commande>`. Livrée en 1F (aucune commande n'existe encore dans le code).

| Commande | Usage |
|---|---|
| `rebind-subject --person <nom ou id> --sub <nouveau>` | Rattacher un adulte à un nouvel `sub` OIDC (`authentication.md` §5) |
| `grant-admin --person <nom ou id>` | Rendre un adulte admin, par exemple quand l'unique admin a perdu son accès |
| `list-persons` | Lister les personnes (identifiant, type, état, admin) |
| `revoke-sessions --person` | Incrémenter `session_epoch` |
| `revoke-tokens --person` | Révoquer tous les jetons d'accès personnels |
| `create-worker --name --job-types` | Créer un identifiant de worker ; affiche le secret une fois |
| `rotate-worker`, `revoke-worker` | Rotation et révocation |

Chaque commande écrit une entrée d'audit `admin_command` (`via: admin-cli`, paramètres sans secret).

## 4. Audit

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

### 4.1 Événements de sécurité

| Événement | Quand |
|---|---|
| `login`, `logout` | Ouverture et fin de session |
| `login_refused` | `sub` inconnu et aucun adulte en attente avec cet email (`not_registered`) |
| `session_revoked` | « Déconnecter partout », par soi-même, par un admin ou par commande d'administration |
| `token_created`, `token_revoked`, `token_expired` | Cycle de vie des jetons |
| `token_auth_failed` | Échec d'authentification par jeton (fréquence limitée) |
| `access_denied` | `403` ou `404` sur un élément |
| `access_violation` | La garde de sortie a bloqué une réponse (`authorization.md` §7) |
| `admin_command` | Commande d'administration |
| `person_preregistered`, `person_preregistration_cancelled` | Un admin ajoute ou retire un adulte en attente |
| `admin_granted`, `admin_revoked` | Promotion ou rétrogradation |
| `household_settings_changed` | Plafond d'adultes ou devise de référence modifiés |

### 4.2 Visibilité

- Les entrées liées à un **élément** suivent les règles d'accès de cet élément (`Readers`).
- Les événements de sécurité d'une **personne** ne sont visibles que d'elle.
- Le **journal de sécurité de la zone admin** (1F) montre aux admins les événements techniques sans lien avec une personne ou un élément (`login_refused`, `access_violation`, `admin_command`, et les événements de la zone admin elle-même), avec des identifiants seulement. Il ne montre pas les connexions ni les jetons d'un autre adulte.
- `access_violation` fait exception : il contient des identifiants seulement, pas de libellé, et **les deux adultes** en sont avertis par email « critique ».
- L'audit des écritures est conservé indéfiniment (volume minuscule) ; les événements de sécurité au moins un an.

## 5. Page « Sécurité »

Dans l'interface, pour chaque adulte :

- **Mes jetons** : créer (nom, préréglage, expiration), révoquer.
- **Mes sessions** : dernière connexion, « déconnecter partout » (avec l'option de révoquer aussi les jetons).
- **Mes derniers événements de sécurité**.
- Un rappel : la déconnexion de Nid ne ferme pas la session Authelia (`authentication.md` §6).

## 6. Alertes

| Événement | Canal |
|---|---|
| `access_violation` | Email « critique » aux deux adultes |
| Jeton proche de l'expiration | Email à son propriétaire, 7 jours avant |
| Échecs d'authentification répétés | Métrique et journal ; pas d'email en v1 |
