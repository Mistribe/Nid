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
| **Catalogue** (1C2b) | Dernière synchronisation (état, heure, version synchronisée, comptes et rapport par catégorie), version épinglée, historique des 10 dernières, synchronisation à la demande confirmée ; le rapport est filtré pour son lecteur (`../superpowers/specs/2026-09-24-provider-catalog-design.md` §3.6) | `GET /api/v1/subscriptions/catalog/runs`, `POST /api/v1/subscriptions/catalog/sync` |
| **Journal de sécurité** (livré en 1F3a) | Événements techniques globaux (`login_refused`, `access_violation`, les événements de la zone admin, §4.1 ; `admin_command` et `subject_rebound` depuis 1F4), identifiants seulement | `GET /api/v1/admin/security-journal` |
| **Plus tard** | IA : fournisseurs, clés, budget (étape 4), identifiants des workers (étape 4) | |

- **Aucun libellé, aucun montant, aucun compteur d'éléments** : un total d'éléments révélerait que l'autre adulte en a de privés.
- Supprimer un adulte **actif** est hors périmètre : il possède des éléments, cela demanderait un transfert de propriété. Un adulte **en attente** qui possède déjà des éléments (atteint par un import, qui résout les adultes par email y compris en attente) ne peut pas non plus être supprimé, pour la même raison (`core.person_active`).
- Chaque action d'écriture est confirmée dans l'interface et journalisée (§4.1).

## 3. Commandes d'administration (CLI)

La zone admin couvre l'administration courante. La CLI reste le **recours de secours**, pour ce que l'interface ne peut pas faire (plus aucun admin joignable, `sub` changé) et pour les identifiants de worker. Elle s'exécute dans le conteneur `nid-api` par l'hébergeur : `nid admin <commande>`. **Livrée en 1F4** (`../superpowers/specs/2026-09-26-admin-cli-design.md`) pour `rebind-subject`, `grant-admin`, `list-persons` et `revoke-sessions` : les cas d'usage de l'API, joués par l'acteur système avec `via: admin-cli`, confirmation pour `rebind-subject` et `grant-admin`. `revoke-tokens` et les commandes de workers attendent l'étape 4.

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

Le journal d'audit (table `platform.audit_log`, `../architecture.md` §6) reçoit ces champs supplémentaires :

| Champ | Contenu |
|---|---|
| `actor_kind` | `person`, `system`, `worker` |
| `person_id` | La personne concernée |
| `via` | `ui`, `api`, `mcp`, `job`, `import`, `admin-cli` |
| `scopes` | Portées de l'acteur |
| `token_id?` | Jeton utilisé (arrive à l'étape 4 avec les jetons) |
| `on_behalf_of?` | Personne au nom de qui agit une tâche (arrive à l'étape 4) |
| `request_id` | Corrélation avec les traces |

### 4.1 Événements de sécurité

| Événement | Quand |
|---|---|
| `login`, `logout` | Ouverture et fin de session — livré en 1F3a |
| `login_refused` | `sub` inconnu et aucun adulte en attente avec cet email (`not_registered`) — livré en 1F3a |
| `session_revoked` | « Déconnecter partout », par soi-même ou par un admin — livré en 1F3a ; par `nid admin revoke-sessions` depuis 1F4 |
| `subject_rebound` | `nid admin rebind-subject` : un adulte rattaché à un nouveau `sub` (donnée `had_subject`) — 1F4 |
| `token_created`, `token_revoked`, `token_expired` | Cycle de vie des jetons — étape 4 |
| `token_auth_failed` | Échec d'authentification par jeton (fréquence limitée) — étape 4 |
| `access_denied` | `403` ou `404` sur un élément — livré en 1F3a |
| `access_violation` | La garde de sortie a bloqué une réponse (`authorization.md` §7) — livré en 1F3a |
| `admin_command` | Chaque exécution d'une commande `nid admin`, réussie ou refusée : commande, résultat, personne visée, paramètres sans secret — 1F4 |
| `person_preregistered`, `person_preregistration_cancelled` | Un admin ajoute ou retire un adulte en attente — livré en 1F3a |
| `admin_granted`, `admin_revoked` | Promotion ou rétrogradation — livré en 1F3a |
| `household_settings_changed` | Plafond d'adultes ou devise de référence modifiés — livré en 1F3a |

### 4.2 Visibilité

- Les entrées liées à un **élément** suivent les règles d'accès de cet élément (`Readers`) : une entrée n'est visible d'un lecteur que si **tous ses éléments** (`item_ids`) le sont.
- Les événements de sécurité d'une **personne** ne sont visibles que d'elle.
- Le **journal de sécurité de la zone admin** (livré en 1F3a) montre aux admins les événements techniques sans lien avec une personne ou un élément (`login_refused`, `access_violation`, et les événements de la zone admin elle-même ; `admin_command` et `subject_rebound` depuis 1F4), avec des identifiants seulement. Il ne montre pas les connexions ni les jetons d'un autre adulte.
- **Aucune vue d'événement de sécurité ne porte d'identifiant d'élément** : `access_violation` ne montre que le nom de la requête, `access_denied` que le type d'accès ; ses identifiants d'élément (`item_ids`) restent dans la table, pour l'hébergeur seul, et n'apparaissent dans **aucune** vue — ni celle de la personne concernée, ni le journal de sécurité de la zone admin.
- Dans la table (pas dans une vue), `access_violation` fait exception sur un point seulement : sa ligne porte des identifiants d'élément, pas seulement un libellé. L'alerte email « critique » aux deux adultes attend un canal de notification (§6).
- L'audit des écritures et les événements de sécurité sont conservés indéfiniment : aucun job de purge (le volume est minuscule pour deux personnes).

## 5. Page « Sécurité »

Dans l'interface, pour chaque adulte :

- **Mes jetons** : créer (nom, préréglage, expiration), révoquer. Attend l'étape 4 (jetons d'accès personnels).
- **Mes sessions** : « déconnecter partout » (livré en 1F3a, sur soi-même ; l'option de révoquer aussi les jetons attend l'étape 4).
- **Mes derniers événements de sécurité** (livré en 1F3a).
- Un rappel : la déconnexion de Nid ne ferme pas la session Authelia (`authentication.md` §6).

## 6. Alertes

| Événement | Canal |
|---|---|
| `access_violation` | Email « critique » aux deux adultes — **reporté** : attend un canal de notification (`INotificationChannel`), hors périmètre de 1F3a (U14) |
| Jeton proche de l'expiration | Email à son propriétaire, 7 jours avant — étape 4 |
| Échecs d'authentification répétés | Métrique et journal ; pas d'email en v1 |
