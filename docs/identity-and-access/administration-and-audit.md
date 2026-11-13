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
| Source | `Auth:AdminSource` (`local-accounts.md` §6). `identity_provider` (défaut en `oidc`) : le groupe admin du fournisseur d'identité (`nid-admins`), recopié à chaque connexion ; aucun adulte n'est admin d'office (`authentication.md` §5). `nid` (défaut en `local` et `both`) : la zone admin et la CLI promeuvent et rétrogradent ; le premier admin vient de la mise en place ou de `create-admin` ; il reste toujours au moins un admin (`core.last_admin`) |
| Plusieurs admins | Possible. Avec la source `nid`, la zone admin les désigne (`PUT /admin/users/{id}/admin`, journalisé `admin_granted` / `admin_revoked`, `source: nid`). Avec la source `identity_provider` : tous ceux que le groupe désigne ; Nid ne promeut ni ne rétrograde personne : la zone admin **montre** le rôle ; un rôle retiré dans le fournisseur d'identité prend effet à la prochaine connexion de la personne, et à la requête suivante de ses sessions ouvertes une fois la base à jour. Le changement est journalisé (`admin_granted` / `admin_revoked`) |
| Périmètre | Technique seulement : n'élargit jamais `Readers` ni `Writers` (`authorization.md` §1) |
| Canal | La session navigateur seulement ; un jeton d'accès personnel n'est jamais admin (principe 5) |
| Contrôle | Marqueur `IAdminOnly`, étape « Admin » du pipeline, `403 platform.admin_required` (`authorization.md` §2 et §4) |
| Interface | `capabilities.admin` pilote l'affichage de la zone (`../frontend/screens.md` §1) |

### 2.2 Zone admin

API `/api/v1/admin`, écrans `/admin/*` (`../frontend/screens.md` §12).

| Section | Contenu | API |
|---|---|---|
| **Utilisateurs** | Adultes (en attente ou actifs, admin ou non) ; pré-enregistrer un adulte (nom, email) ; annuler un adulte en attente (`DELETE /admin/users/{id}` répond `200 { id }`) ; « déconnecter partout » un adulte (incrémente son `session_epoch` ; `POST /admin/users/{id}/revoke-sessions` répond `200` avec l'adulte) | `GET/POST /admin/users`, `DELETE /admin/users/{id}`, `POST /admin/users/{id}/revoke-sessions` |
| **Utilisateurs, comptes locaux** (#215, `local-accounts.md` §14.4) | En `local` et `both` : façon de se connecter de chaque adulte, verrouillage, second facteur (oui / non), lien en attente ; créer ou révoquer un lien de mot de passe (affiché une fois), déverrouiller ; donner ou retirer le rôle admin (source `nid`) ; pré-enregistrer sans email en `local` | `POST/DELETE /admin/users/{id}/password-link`, `POST /admin/users/{id}/unlock`, `PUT /admin/users/{id}/admin` |
| **Foyer** | Plafond d'adultes (`max_adults`, au moins 1 et au moins le nombre d'adultes), devise de référence | `GET/PATCH /admin/household` |
| **Taux de change** | Saisie manuelle, synchronisation à la demande, journal des collectes | `/api/v1/core/fx-rates` (écritures et journal réservés aux admins ; la lecture des taux reste ouverte) |
| **Tâches planifiées** (#266) | Jobs Quartz : cron et prochaine exécution, dernière exécution ; historique d'un job ; une exécution avec son résumé et ses logs. Chaque exécution, planifiée ou à la demande, est journalisée dans `platform.job_run` (résultat `succeeded`, `failed`, `skipped`, `interrupted`), les 100 dernières par job, paginées (50 par page) ; ses logs vont dans `FileLogs:Path/jobs/<job>/<id>.log`, purgés avec elle et filtrés (`authorization.md` §8) | `GET /admin/jobs`, `GET /admin/jobs/{key}`, `GET /admin/jobs/{key}/runs/{id}` |
| **Système** | Version, démarrage, santé (base, fournisseur d'identité, « non utilisé » en `local`), taille de la base ; mode de connexion et source du rôle admin ; avertissement quand `Auth:AllowInsecureHttp` est actif, repris en bandeau sur toute la zone admin (`local-accounts.md` §12) | `GET /admin/system` |
| **Catalogue** (1C2b) | Dernière synchronisation (état, heure, version synchronisée, comptes et rapport par catégorie), version épinglée, historique des synchronisations des 400 derniers jours, sauf la dernière réussie qui reste toujours (paginé), synchronisation à la demande confirmée ; le rapport est filtré pour son lecteur (`../superpowers/specs/2026-09-24-provider-catalog-design.md` §3.6) | `GET /api/v1/subscriptions/catalog/runs`, `POST /api/v1/subscriptions/catalog/sync` |
| **Journal de sécurité** (livré en 1F3a) | Événements techniques globaux (`login_refused`, `access_violation`, les événements de la zone admin, §4.1 ; `admin_command` et `subject_rebound` depuis 1F4), identifiants seulement | `GET /api/v1/admin/security-journal` |
| **Plus tard** | IA : fournisseurs, clés, budget (étape 4), identifiants des workers (étape 4) | |

- **Aucun libellé, aucun montant, aucun compteur d'éléments** : un total d'éléments révélerait que l'autre adulte en a de privés.
- Supprimer un adulte **actif** est hors périmètre : il possède des éléments, cela demanderait un transfert de propriété. Un adulte **en attente** qui possède déjà des éléments (atteint par un import, qui résout les adultes par email y compris en attente) ne peut pas non plus être supprimé, pour la même raison (`core.person_active`).
- Chaque action d'écriture est confirmée dans l'interface et journalisée (§4.1).

## 3. Commandes d'administration (CLI)

La zone admin couvre l'administration courante. La CLI reste le **recours de secours**, pour ce que l'interface ne peut pas faire (plus aucun admin joignable, `sub` changé) et pour les identifiants de worker. Elle s'exécute dans le conteneur `nid` par l'hébergeur : `nid admin <commande>`. **Livrée en 1F4** (`../superpowers/specs/2026-09-26-admin-cli-design.md`) pour `rebind-subject`, `list-persons` et `revoke-sessions` : les cas d'usage de l'API, joués par l'acteur système avec `via: admin-cli`, confirmation pour `rebind-subject`. `revoke-tokens` est livrée en 4A ; les commandes de workers attendent l'étape 4.

| Commande | Usage |
|---|---|
| `rebind-subject --person <nom ou id> --sub <nouveau>` | Rattacher un adulte à un nouvel `sub` OIDC (`authentication.md` §5) |
| `list-persons` | Lister les personnes (identifiant, type, état, admin) |
| `revoke-sessions --person` | Incrémenter `session_epoch` |
| `revoke-tokens --person` | Révoquer tous les jetons d'accès personnels (livré en 4A) |
| `create-worker --name --job-types` | Créer un identifiant de worker ; affiche le secret une fois |
| `rotate-worker`, `revoke-worker` | Rotation et révocation |
| `create-admin --name [--email] [--login]` | Comptes locaux : créer un adulte admin et afficher son lien de mot de passe (`local-accounts.md` §5 et §15) |
| `reset-password --person [--revoke-tokens]` | Comptes locaux : afficher un lien de réinitialisation, lever le verrouillage |
| `grant-admin --person`, `revoke-admin --person` | Rôle admin avec la source `nid` ; le dernier admin ne peut pas être retiré |
| `reset-totp --person` | Désactiver le second facteur et ses codes de secours, fermer les sessions |
| `unlock --person` | Lever le verrouillage d'un compte local |

Chaque commande écrit une entrée d'audit `admin_command` (`via: admin-cli`, paramètres sans secret). Un lien de mot de passe n'est écrit que sur la sortie standard, jamais dans l'audit ni le journal.

## 4. Audit

Le journal d'audit (table `platform.audit_log`, `../architecture.md` §6) reçoit ces champs supplémentaires :

| Champ | Contenu |
|---|---|
| `actor_kind` | `person`, `system`, `worker` |
| `person_id` | La personne concernée |
| `via` | `ui`, `api`, `mcp`, `job`, `import`, `admin-cli` |
| `scopes` | Portées de l'acteur |
| `token_id?` | Jeton utilisé (livré en 4A) |
| `on_behalf_of?` | Personne au nom de qui agit une tâche (arrive à l'étape 4) |
| `request_id` | Corrélation avec les traces |

### 4.1 Événements de sécurité

| Événement | Quand |
|---|---|
| `login`, `logout` | Ouverture et fin de session — livré en 1F3a |
| `login_refused` | Connexion refusée : hors du groupe d'accès, foyer au plafond ou email déjà porté par un autre `sub` (donnée `reason`) — livré en 1F3a, `reason` depuis la migration vers Pocket ID |
| `session_revoked` | « Déconnecter partout », par soi-même ou par un admin — livré en 1F3a ; par `nid admin revoke-sessions` depuis 1F4 |
| `subject_rebound` | `nid admin rebind-subject` : un adulte rattaché à un nouveau `sub` (donnée `had_subject`) — 1F4 |
| `token_created`, `token_revoked` | Cycle de vie des jetons — livrés en 4A |
| `token_expired` | Abandonné (4A) : la liste « Mes jetons » montre les jetons expirés |
| `token_auth_failed` | Échec d'authentification par jeton (fréquence limitée) — livré en 4A |
| `access_denied` | `403` ou `404` sur un élément — livré en 1F3a |
| `access_violation` | La garde de sortie a bloqué une réponse (`authorization.md` §7) — livré en 1F3a |
| `admin_command` | Chaque exécution d'une commande `nid admin`, réussie ou refusée : commande, résultat, personne visée, paramètres sans secret — 1F4 |
| `person_preregistered`, `person_preregistration_cancelled` | Un admin ajoute ou retire un adulte en attente — livré en 1F3a |
| `admin_granted`, `admin_revoked` | Le rôle admin d'un adulte change à sa connexion, selon le groupe admin du fournisseur d'identité (donnée `source: identity_provider`) ; ou, avec la source `nid`, par la zone admin ou la CLI (`source: nid` ou `admin-cli`, `by`) |
| `login_failed` | Comptes locaux : mot de passe ou code faux, compte verrouillé, identifiant inconnu (`reason`, `count`, `address`), au plus un par compte ou adresse et par minute ; jamais l'identifiant tapé (`local-accounts.md` §16) |
| `login_locked` | Un compte local passe verrouillé (`level`, `until`) ; notifié au titulaire |
| `password_changed`, `password_reset` | L'adulte change son mot de passe ou son identifiant ; un lien de réinitialisation est utilisé (sessions fermées) |
| `credential_link_issued`, `credential_link_revoked`, `account_enrolled` | Cycle de vie d'un lien de mot de passe ; un adulte en attente devient actif par son lien |
| `account_unlocked` | Déverrouillage par un admin ou la CLI |
| `totp_enabled`, `totp_disabled`, `totp_reset`, `recovery_code_used`, `recovery_codes_regenerated` | Second facteur d'un compte local |
| `setup_completed` | Mise en place d'une instance neuve terminée par la page `/setup` |
| `household_settings_changed` | Plafond d'adultes ou devise de référence modifiés — livré en 1F3a |

### 4.2 Visibilité

- Les entrées liées à un **élément** suivent les règles d'accès de cet élément (`Readers`) : une entrée n'est visible d'un lecteur que si **tous ses éléments** (`item_ids`) le sont.
- Les événements de sécurité d'une **personne** ne sont visibles que d'elle.
- Le **journal de sécurité de la zone admin** (livré en 1F3a) montre aux admins les événements techniques sans lien avec une personne ou un élément (`login_refused`, `access_violation`, et les événements de la zone admin elle-même ; `admin_command` et `subject_rebound` depuis 1F4), avec des identifiants seulement. Il ne montre pas les connexions ni les jetons d'un autre adulte. Avec les comptes locaux, il montre aussi ce dont un admin a besoin pour agir : `login_failed` sans personne (`unknown_login`), `login_locked`, `password_reset`, les liens de mot de passe, `account_enrolled`, `account_unlocked`, `totp_reset`, `setup_completed` (`local-accounts.md` §16).
- **Une seule vue d'événement de sécurité porte des éléments : le journal de la zone admin, pour un `access_violation` de cause `outside_visible_set`** (#28). Il montre le nom et le type des éléments que l'admin peut voir, l'identifiant seul pour les autres, jamais de contenu. Partout ailleurs, `access_violation` ne montre que la requête, sa cause (`reason` : `no_actor`, `visibility_lookup_failed`, `outside_visible_set`), le nombre d'éléments et le type de l'exception (`error`) ; `access_denied` ne montre que le type d'accès.
- Dans la table (pas dans une vue), `access_violation` fait exception sur un point seulement : sa ligne porte des identifiants d'élément, pas seulement un libellé. La notification « critique » qu'elle déclenche, elle, ne porte aucun élément : son corps nomme la requête seulement (§6).
- L'audit des écritures et les événements de sécurité sont conservés indéfiniment : aucun job de purge (le volume est minuscule pour deux personnes).

## 5. Page « Sécurité »

Dans l'interface, pour chaque adulte :

- **Mes jetons** : créer (nom, préréglage, expiration), révoquer. Livré en 4A.
- **Mes sessions** : « déconnecter partout » (livré en 1F3a, sur soi-même ; révoque aussi les jetons sur demande depuis 4A).
- **Mes derniers événements de sécurité** (livré en 1F3a).
- Un rappel : la déconnexion de Nid ne ferme pas la session de Pocket ID (`authentication.md` §6). En `local`, ce rappel disparaît.
- **Mot de passe** (comptes locaux) : changer son mot de passe ou son identifiant, le mot de passe actuel demandé ; les autres sessions se ferment (`local-accounts.md` §8).
- **Validation en deux étapes** (comptes locaux) : activer le TOTP, voir combien de codes de secours restent, les régénérer, désactiver (`local-accounts.md` §10).

## 6. Alertes

| Événement | Canal |
|---|---|
| `access_violation` | Notification « critique » à tous les adultes actifs, sans élément, qui part même si la requête échoue ; Discord du foyer si configuré — livré (notifications, PR 4, lève U14) |
| Jeton proche de l'expiration | Affiché dans « Mes jetons » et dans un bandeau, 7 jours avant (4A) ; notifié à son titulaire seul, 7 jours avant, sur ses canaux (notifications, PR 4) |
| Sauvegarde, tâche planifiée ou synchronisation en échec | Notification aux admins (`backup_failed` critique, `job_failed` d'action, `sync_failed` d'info), sur leurs canaux ; Discord du foyer pour les deux premières — livré (notifications, PR 4) |
| Échecs d'authentification répétés | Métrique et journal ; pas d'email en v1. Comptes locaux : `login_locked` notifié au titulaire, verrouillage visible dans la zone admin |
| Mot de passe réinitialisé ou changé, lien de réinitialisation créé, second facteur réinitialisé, 2 codes de secours restants | Notification au titulaire seul, sur ses canaux (`local-accounts.md` §16) |
