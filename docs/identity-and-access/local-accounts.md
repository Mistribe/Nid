# Comptes locaux

> Statut : spécification, avant le code (#215). Connexion d'un adulte par identifiant et mot de passe, sans fournisseur d'identité, à côté d'OIDC.
> Références : `README.md` (principes), `authentication.md` (OIDC, session, routes), `tokens-and-credentials.md`, `administration-and-audit.md`, `../core-data-model.md` §4.4 (provisionnement), `../security/risk-register.md`.

---

## 1. Pourquoi

Aujourd'hui, se connecter à Nid demande un fournisseur OIDC, en pratique Pocket ID : un second service, un second nom d'hôte, un client OIDC à créer puis son secret à recopier, et une passkey par adulte. Pour un foyer qui veut essayer Nid ou l'installer sur un petit serveur, c'est l'essentiel de l'effort d'installation.

Les **comptes locaux** ajoutent une façon de se connecter intégrée à Nid : un identifiant et un mot de passe, gérés par Nid. L'installation autonome se réduit à Nid et PostgreSQL ; Pocket ID reste disponible pour qui veut le SSO et les passkeys.

Ce document tranche les onze décisions de #215 (§2 à §12), puis donne le modèle de données (§13), les routes (§14), la CLI (§15), les événements de sécurité (§16), la configuration (§17), le plan de test (§18) et le découpage en PR (§19).

**Hors périmètre** : passkeys gérées par Nid (WebAuthn, une suite), réinitialisation par email, passage d'un foyer d'OIDC aux comptes locaux (ou l'inverse) au-delà de la cohabitation des deux modes, connexion par un réseau social, la démo publique (mode démo, #204, inchangé : §2.4).

## 2. Modes et valeurs par défaut

### 2.1 Décision

`Auth:Mode` choisit les façons de se connecter, réglé par l'hébergeur :

| Valeur | Connexion | Rôle admin décidé par (§6) |
|---|---|---|
| `local` | Identifiant et mot de passe | Nid (zone admin) |
| `oidc` | Fournisseur d'identité, comme aujourd'hui | Le groupe admin du fournisseur |
| `both` | L'un ou l'autre, au choix de chaque adulte sur la page de connexion | Nid (zone admin) |

**Quand `Auth:Mode` est absent ou vide**, Nid le déduit : `oidc` si `Auth:Authority` est renseigné, `local` sinon. Ainsi :

- une **nouvelle installation** sans fournisseur d'identité est en `local` sans rien régler ;
- une **installation OIDC existante** (qui a forcément `Auth:Authority`, obligatoire jusqu'ici) reste en `oidc` après la mise à jour, **sans changer sa configuration**, et se comporte exactement comme avant (rôle admin compris).

Le `compose.yaml` autonome ne fixe donc **pas** de valeur par défaut à `Auth__Mode` (`${NID_AUTH_MODE:-}`) : une valeur par défaut `local` dans le fichier ferait basculer une installation existante qui récupère le nouveau `compose.yaml` en gardant son `.env`.

### 2.2 Validation au démarrage

- `oidc` et `both` exigent `Auth:Authority` et `Auth:ClientSecret` (comme aujourd'hui) ; `local` les ignore et ne déclare pas le schéma OpenID Connect (comme le mode démo).
- Une valeur inconnue de `Auth:Mode` arrête le démarrage avec un message qui donne les trois valeurs.
- En `local`, `/health/ready` ne vérifie plus le fournisseur d'identité ; la section Système de la zone admin l'indique « non utilisé ».

### 2.3 Un adulte, une ou deux façons de se connecter

En `both`, un adulte peut avoir un `oidc_subject`, un compte local, ou les deux. **Aucun rattachement automatique** entre les deux : une connexion OIDC dont l'email est celui d'un adulte qui a un compte local mais pas de `sub` est refusée (`core.email_taken`, comme aujourd'hui pour un autre `sub`) ; c'est `nid admin rebind-subject` qui rattache. Un admin peut donner un lien de mot de passe (§7) à un adulte OIDC. Un adulte est **en attente** tant qu'il n'a ni `oidc_subject` ni compte local au mot de passe choisi.

Passer d'un mode à l'autre ne change aucune donnée : les adultes, leurs comptes locaux et leurs `sub` restent ; seules les façons de se connecter offertes changent. Un compte local reste en base en mode `oidc`, inutilisable tant que le mode ne l'offre pas.

### 2.4 Mode démo

Le mode démo (`Nid:Demo:Enabled`, #204) ignore `Auth:Mode` : il ne mappe ni les routes des comptes locaux ni OIDC, et garde sa propre connexion (`/auth/demo/{key}`). Rien de ce document ne s'y applique.

### 2.5 Écarté

- **`both` par défaut** : double la surface d'attaque et la page de connexion pour la plupart des foyers, qui n'en utilisent qu'un.
- **`oidc` par défaut pour une nouvelle installation** : garde l'obstacle que #215 veut lever.
- **Une valeur par défaut dans `compose.yaml`** : ferait basculer silencieusement les installations existantes (§2.1).
- **Rattacher par l'email un compte OIDC à un compte local** : qui administre le fournisseur d'identité prendrait la main sur un compte local sans geste dans Nid.

## 3. Stockage et hachage

### 3.1 Décision

- Un **compte local par adulte**, dans sa propre table `core.local_credential` (§13.1), à côté de `person` et non dedans : les secrets restent hors de la table que tout le code lit, hors de `PersonInfo`, hors de l'export.
- Hachage par le **`PasswordHasher` d'ASP.NET Core Identity** (format V3 : PBKDF2-HMAC-SHA512, sel aléatoire de 128 bits par mot de passe), avec **210 000 itérations** (recommandation OWASP pour PBKDF2-HMAC-SHA512). Le format est auto-descriptif : la fonction, le nombre d'itérations et le sel sont dans la valeur stockée.
- **Relever les paramètres plus tard** : le nombre d'itérations est une constante du code, relevée par une version de Nid. À la connexion suivante, `VerifyHashedPassword` répond `SuccessRehashNeeded` pour un hash plus faible et Nid le recalcule avec les paramètres du moment, dans la même requête. Le jour où la bibliothèque standard .NET offre Argon2id, la même mécanique migre les hashes à la connexion.
- **Temps constant face à un identifiant inconnu** : Nid vérifie alors le mot de passe contre un hash factice calculé au démarrage avec les paramètres du moment, pour que la durée de la réponse ne dise pas si l'identifiant existe.
- **Calculs de hash bornés** : au plus `Auth:Local:MaxConcurrentHashes` (4 par défaut) en même temps ; au-delà, la requête attend jusqu'à 5 secondes puis répond `429`. Un attaquant ne sature pas le processeur d'un petit serveur.
- Aucun mot de passe, hash, code TOTP, secret TOTP, code de secours ou jeton de lien n'apparaît dans les journaux, les traces, l'audit, les notifications ni l'export `nid.data` (§13.5).

### 3.2 Écarté

- **Argon2id** (préférence de l'OWASP) : aucune implémentation dans la bibliothèque standard .NET 10 ; il faudrait un paquet tiers, natif (libsodium) ou managé peu adopté, ce que la politique de dépendances évite (`../security/risk-register.md` INF-3, INF-4). Son coût mémoire (19 Mio par tentative) est aussi un levier de déni de service sur un petit serveur. Le format auto-descriptif permet d'y passer plus tard sans réinitialiser personne.
- **bcrypt** : mot de passe tronqué à 72 octets, paquet tiers.
- **PBKDF2 écrit à la main** (`Rfc2898DeriveBytes.Pbkdf2`) : `PasswordHasher` le fait déjà, avec un format versionné et le signal de rehachage, maintenu par Microsoft.
- **Hachage rapide** (SHA-256, comme les jetons) : convient à un secret aléatoire de 256 bits, pas à un mot de passe choisi par un humain.
- **Colonnes dans `person`** : tout lecteur de `person` verrait les secrets ; l'export et les tests de fuite devraient les exclure un par un.

## 4. Règles de mot de passe

### 4.1 Décision (NIST SP 800-63B)

- **Longueur** : 12 caractères au moins (`Auth:Local:MinPasswordLength`, qui ne peut que monter, jusqu'à 64), **256 au plus** (borne le travail du serveur). Les caractères sont comptés après normalisation Unicode (points de code, pas octets).
- **Tous les caractères** acceptés, espaces et emoji compris. Normalisation **NFKC** avant hachage, pour qu'un même mot de passe tapé sur deux claviers donne le même hash. Pas de `trim`.
- **Aucune règle de composition** (majuscule, chiffre, symbole), aucune expiration périodique, aucun indice, aucune question secrète.
- **Liste de refus locale** : le mot de passe est refusé (`auth.password_too_common`) s'il figure dans une liste de mots de passe courants **embarquée dans l'image** (`src/Hosting/Nid.Host/Auth/common-passwords.txt`, choisie à la main : environ 170 formes connues de 12 caractères et plus ; importer une liste publique de fuites sous licence compatible est une suite), s'il ne fait que répéter peu de caractères ou suivre une rangée du clavier ou l'alphabet, s'il contient l'identifiant, la partie locale de l'email ou un mot de 4 lettres ou plus du nom affiché, ou s'il n'est que `nid` suivi de chiffres et de symboles, sans tenir compte de la casse. Livré en PR 2 (#218) : la règle « contient `nid` » refusait des phrases ordinaires, elle est devenue « `nid` suivi de chiffres ou de symboles ».
- **Formulaire ami des gestionnaires de mots de passe** : champ identifiant `autocomplete="username"`, mot de passe `autocomplete="current-password"` à la connexion et `new-password` au choix, collage permis, bouton « afficher », une seule saisie du nouveau mot de passe quand il est affiché (deux sinon), aucune limite de saisie côté navigateur en deçà de 256.
- Les messages d'erreur disent la règle en mots simples (« au moins 12 caractères », « trop courant, choisissez-en un autre ») ; aucune jauge de force.

### 4.2 Écarté

- **Service de fuites en ligne** (Have I Been Pwned, même par k-anonymat) : une partie du hash du mot de passe sortirait du serveur ; décision du propriétaire, les données du foyer restent chez lui.
- **Règles de composition et expiration** : le NIST les déconseille ; elles produisent des mots de passe prévisibles (`Motdepasse2026!`).
- **Minimum de 8** : trop faible pour un serveur exposé sur Internet sans second facteur obligatoire.

## 5. Premier administrateur

### 5.1 Décision

En `local` ou `both`, aucun fournisseur d'identité ne dit qui administre. Tant que l'instance n'a **aucun adulte** (base neuve), la **mise en place** est ouverte ; elle se fait par l'un de deux chemins :

1. **Page de mise en place** (`/setup`), protégée par un **jeton de mise en place** :
   - À chaque démarrage tant que la mise en place est ouverte, Nid tire un jeton aléatoire de 256 bits (`nid_setup_<base64url>`), le garde **en mémoire seulement** (jamais en base) et écrit dans le journal, au niveau `Warning`, l'adresse complète `{PUBLIC_URL}/setup#nid_setup_…`. Le jeton est dans le **fragment** de l'URL : le navigateur ne l'envoie pas au proxy, qui ne le journalise donc pas ; la page le lit et le poste.
   - La page demande le nom, l'identifiant et le mot de passe de l'hébergeur ; `POST /auth/setup` crée l'adulte, son compte local et `is_admin = true`, sous le verrou consultatif du provisionnement, puis la mise en place se ferme. La page ne fait pas de session : elle mène à la connexion, identifiant prérempli.
   - Une fois un adulte créé (par ce chemin, par la CLI ou par une connexion OIDC en `both`), `POST /auth/setup` répond `404` et le jeton est oublié. Un redémarrage tire un nouveau jeton (l'ancien, resté dans le journal, ne vaut plus rien).
   - Jeton comparé en temps constant ; échecs comptés par adresse comme les autres échecs d'authentification (§9).
2. **`nid admin create-admin`** (§15) dans le conteneur : crée un adulte admin et affiche un **lien de mot de passe** à usage unique (§7) avec lequel il choisit son mot de passe. Fonctionne aussi quand la mise en place est fermée (plus aucun admin joignable).

`GET /auth/session` signale la mise en place ouverte (`setupRequired: true`) ; l'interface dit alors de chercher l'adresse dans le journal du conteneur (`docker compose logs nid`) ou d'utiliser la CLI, sans jamais afficher le jeton.

### 5.2 Écarté

- **Le premier venu devient admin** : un inconnu qui trouve une instance neuve exposée sur Internet la réclamerait avant l'hébergeur.
- **Compte par défaut** (`admin` / mot de passe connu) : le défaut le plus exploité qui soit.
- **Mot de passe initial dans une variable d'environnement** : il resterait dans `.env`, `compose.yaml`, l'historique du shell et `docker inspect`.
- **Jeton stocké en base** : il survivrait dans les sauvegardes ; en mémoire, il ne vit que le temps d'un processus.
- **Mot de passe saisi dans la CLI** : terminal sans écho incertain dans `docker compose exec`, risque de l'écrire en argument (historique). Le lien à usage unique évite le cas.

## 6. Rôle admin hors OIDC

### 6.1 Décision

`Auth:AdminSource` dit qui décide de `person.is_admin` : `identity_provider` (le groupe admin, recopié à chaque connexion OIDC, comme aujourd'hui) ou `nid` (la zone admin). Par défaut : `identity_provider` en `oidc`, `nid` en `local` et en `both`. En `local`, `identity_provider` est refusé au démarrage (il n'y a pas de fournisseur).

Avec la source `nid` :

- La zone admin, section Utilisateurs, **promeut ou rétrograde** un adulte actif ou en attente (`PUT /api/v1/admin/users/{id}/admin`) ; la CLI aussi (`grant-admin`, `revoke-admin`, §15).
- **Il reste toujours au moins un admin** : retirer le rôle au dernier admin est refusé (`core.last_admin`, `409`), contrôlé sous le verrou du provisionnement pour que deux retraits simultanés ne passent pas ensemble. Se rétrograder soi-même est permis s'il reste un autre admin.
- Une connexion OIDC (en `both`) ne touche plus à `is_admin` : le groupe d'accès reste exigé, le groupe admin est ignoré. Un adulte créé par une connexion OIDC arrive sans le rôle ; si le premier adulte d'une instance en `both` arrive par OIDC (la mise en place se ferme dès qu'un adulte existe), `nid admin grant-admin` lui donne le rôle. Livré en PR 4 : `PUT /api/v1/admin/users/{id}/admin`, `grant-admin`, `revoke-admin`, le menu de chaque ligne de la zone admin.
- Chaque changement est journalisé `admin_granted` / `admin_revoked` avec `source: nid` (ou `source: admin-cli`) et l'admin qui l'a fait (`by`).
- Un rôle retiré prend effet à la requête suivante de la personne (`ActorFilter` relit `person`, `authorization.md` §1) : aucune attente de reconnexion.

Le rôle reste purement technique : il n'élargit jamais `Readers` ni `Writers` (`authorization.md` §1), ne passe jamais par un jeton.

### 6.2 Écarté

- **Deux sources à la fois** (le groupe et la zone admin) : à chaque connexion OIDC, le groupe écraserait une décision prise dans Nid, ou l'inverse ; une seule source, choisie, est prévisible.
- **Forcer `nid` en `oidc`** : changerait le comportement d'une installation existante (#215 l'exclut). L'hébergeur peut choisir `Auth:AdminSource=nid` en `oidc` s'il préfère.
- **Admin d'office pour tout adulte** : le rôle donne la main sur les comptes des autres (§8).

## 7. Ajouter un adulte : le lien à usage unique

### 7.1 Décision

- Un admin **pré-enregistre** l'adulte comme aujourd'hui (zone admin, `POST /api/v1/admin/users`). En `local`, l'**email devient facultatif** (un nom suffit) : il n'est la clé de rattachement qu'en OIDC. En `oidc` et `both`, il reste demandé tel qu'aujourd'hui.
- Un admin crée ensuite un **lien de mot de passe** (`POST /api/v1/admin/users/{id}/password-link`) : l'adresse `{PUBLIC_URL}/password#nid_link_<secret>` est affichée **une fois**, à copier et à transmettre par le moyen de son choix (de vive voix, messagerie, papier). Nid n'envoie rien : aucun email n'est nécessaire.
- **Secret** : 256 bits aléatoires, base64url, dans le fragment de l'URL (jamais envoyé au proxy). Stocké par **SHA-256** (`core.credential_link`, §13.2), comme les jetons d'accès : un secret de 256 bits n'a pas besoin d'un hachage lent.
- **Validité** : 72 heures par défaut (`Auth:Local:LinkLifetimeHours`, de 1 à 168), **un seul usage**, un seul lien vivant par adulte : en créer un nouveau révoque le précédent ; l'admin peut aussi le révoquer (`DELETE …/password-link`). La zone admin montre « lien en attente jusqu'au … », jamais le lien.
- **Usage** : la page `/password` lit le fragment, demande au serveur ce que vaut le lien (`POST /auth/link/inspect` : nom de l'adulte et usage, ou `404` pour tout lien inconnu, expiré, utilisé ou révoqué, sans distinction), puis l'adulte choisit son **identifiant** (proposé : son email s'il en a un, sinon son nom en minuscules sans espaces ; modifiable) et son **mot de passe** (§4). `POST /auth/link/complete` crée le compte local, consomme le lien et rend l'adulte actif, dans une transaction.
- **Pas de session ouverte par le lien** : la page mène à la connexion, identifiant prérempli. Une seule route ouvre une session locale (§14.2), avec les contrôles de verrouillage et de second facteur ; le gestionnaire de mots de passe enregistre le couple à cette connexion.
- **Identifiant** : 1 à 254 caractères après `trim` et NFKC, unique sans tenir compte de la casse (`citext`), sans espace ni caractère de contrôle. Il n'est pas un secret mais n'est jamais affiché à un autre adulte que les admins. L'adulte le change sur sa page Sécurité (mot de passe actuel demandé).
- Le plafond d'adultes (`max_adults`) s'applique au pré-enregistrement, comme aujourd'hui.

### 7.2 Écarté

- **Envoi par email** : demanderait un SMTP configuré pour simplement entrer dans Nid ; #215 veut une installation Nid + PostgreSQL.
- **Mot de passe provisoire choisi par l'admin** : l'admin le connaîtrait, l'adulte le garderait souvent.
- **Inscription libre** : n'importe qui atteignant l'instance entrerait.
- **Lien dans la chaîne de requête** (`?token=`) : il finirait dans les journaux du proxy et l'historique.

## 8. Mot de passe oublié

### 8.1 Décision

- Pas de réinitialisation en libre-service (pas d'email, §1). La page de connexion dit : « Mot de passe oublié ? Demandez un lien à un administrateur du foyer. »
- **Un admin crée un lien de réinitialisation** pour un adulte (le même `POST …/password-link`, dont l'usage vaut `reset` dès que l'adulte a un compte local) ; **`nid admin reset-password --person`** le fait depuis le conteneur, pour un admin qui a lui-même oublié le sien.
- Utiliser un lien de réinitialisation, dans une même transaction : remplace le hash, **incrémente `session_epoch`** (toutes les sessions de l'adulte se ferment, comme « déconnecter partout »), remet à zéro les échecs et le verrouillage (§9), consomme le lien. Le second facteur (§10) **reste** : un mot de passe réinitialisé ne contourne pas le TOTP ; `nid admin reset-totp` existe pour qui a perdu son téléphone et ses codes.
- Les **jetons d'accès personnels** ne sont pas révoqués d'office (un assistant cesserait de marcher pour un simple oubli) : la boîte de dialogue de l'admin propose « révoquer aussi ses jetons » (décochée), comme « déconnecter partout » ; la CLI prend `--revoke-tokens`.
- **Changer son mot de passe** soi-même (page Sécurité, mot de passe actuel demandé) incrémente aussi `session_epoch` : les autres sessions se ferment, la session courante est réémise avec le nouvel epoch.
- L'adulte concerné reçoit une **notification** (sur ses canaux) quand un lien de réinitialisation est créé pour lui et quand son mot de passe change, pour qu'une prise de compte ne passe pas inaperçue.
- Confiance : un admin peut prendre la main sur le compte d'un autre adulte (lien de réinitialisation). C'est l'équivalent de l'administrateur de Pocket ID aujourd'hui, dans la frontière de confiance (`administration-and-audit.md` §1) ; l'événement et la notification le rendent visible.

### 8.2 Écarté

- **Questions secrètes** : déconseillées par le NIST, devinables.
- **Lien qui ouvre une session** : contournerait le second facteur et le verrouillage (§7.1).
- **Révocation d'office des jetons** : voir ci-dessus ; laissée au choix.

## 9. Force brute et événements de sécurité

### 9.1 Décision

- **Par compte** : après **5 échecs consécutifs** (`Auth:Local:LockThreshold`), le compte est verrouillé **1 minute**, puis chaque nouveau verrouillage double la durée (1, 2, 4, 8… minutes), **60 minutes au plus** (`Auth:Local:LockMaxMinutes`). Pendant un verrouillage, même le bon mot de passe est refusé. Une connexion réussie remet le compteur et le palier à zéro ; un lien de réinitialisation et `nid admin unlock` aussi. Un mauvais code TOTP ou de secours compte comme un échec.
- **Par adresse** : la limite existante des échecs d'authentification, **20 en 10 minutes par adresse** (partagée avec les jetons, `tokens-and-credentials.md` §1.5), répond `429` avec `Retry-After`. Elle compte les échecs de mot de passe, de TOTP, de jeton de mise en place et de lien.
- **Même réponse** que l'identifiant existe ou non, qu'il soit verrouillé ou non : `401 auth.invalid_credentials`, « Identifiant ou mot de passe incorrect, ou trop de tentatives : réessayez dans quelques minutes. » Même durée (hash factice, §3.1). Le verrouillage n'est pas annoncé à celui qui tape : le titulaire l'apprend par sa notification.
- **Événements** (détail au §16) dans le journal d'audit existant : `login_failed`, `login_locked`, `password_changed`, `password_reset`, plus ceux des liens, du TOTP et de la mise en place. Jamais l'identifiant tapé quand il est inconnu (c'est souvent un mot de passe tapé dans le mauvais champ), jamais un secret. Au plus un `login_failed` par compte (ou, sans compte, par adresse) et par minute, avec le nombre d'échecs agrégés.
- **Alertes** : `login_locked` notifie le titulaire du compte ; la zone admin montre « verrouillé jusqu'à … » et propose de déverrouiller (`POST /api/v1/admin/users/{id}/unlock`). Métrique `nid.auth.login_failures` (par raison) pour l'observabilité.

### 9.2 Écarté

- **Verrouillage définitif** après N échecs : un tiers bloquerait un adulte à volonté (déni de service ciblé).
- **CAPTCHA** : service tiers ou accessibilité dégradée ; le verrouillage croissant et la limite par adresse suffisent pour un foyer.
- **Message « compte verrouillé »** distinct : dirait à un attaquant qu'un identifiant existe.
- **Journaliser l'identifiant inconnu** : il contient souvent un mot de passe.

## 10. Second facteur : TOTP et codes de secours

### 10.1 Décision

- **Facultatif, par adulte**, pour les comptes locaux seulement (en OIDC, le fournisseur s'en charge). Activé sur la page Sécurité.
- **TOTP** (RFC 6238) : SHA-1, 6 chiffres, pas de 30 secondes, ±1 pas toléré, compatible avec toutes les applications courantes. Secret de 160 bits, **chiffré par Data Protection** (but `Nid.LocalAccounts.Totp`, clés sur le volume sauvegardé) : il doit être relu, donc pas haché. **Rejeu refusé** : le dernier pas accepté est gardé, un code d'un pas déjà utilisé est refusé.
- **Activation** en deux temps : Nid tire le secret et l'affiche (QR code dessiné dans le navigateur depuis l'URI `otpauth://`, et le secret en clair pour une saisie à la main) ; il n'est actif qu'après un premier code juste. Un secret non confirmé est abandonné au bout de 15 minutes.
- **10 codes de secours** remis à l'activation, **affichés une fois** (à copier, imprimer ou télécharger en `.txt`) : 80 bits chacun, `xxxx-xxxx-xxxx-xxxx` en base32 sans caractères ambigus, stockés par SHA-256 (§13.3), **un seul usage**. En régénérer (mot de passe demandé) invalide les précédents. La page Sécurité dit combien il en reste ; une notification prévient à 2 restants.
- **Connexion** : après un mot de passe juste, si le TOTP est actif, la réponse demande un code (`next: "totp"`) et pose un cookie d'étape `__Host-nid-mfa` (Data Protection, 5 minutes, personne, epoch, sans session) ; `POST /auth/login/totp` prend un code TOTP ou un code de secours. Trois codes faux font reprendre au mot de passe ; ils comptent dans le verrouillage (§9).
- **Désactiver** : mot de passe et code demandés. **Perte du téléphone et des codes** : `nid admin reset-totp --person` désactive le TOTP, efface les codes et ferme les sessions (`session_epoch`).
- Un admin ne voit jamais le secret ni les codes ; la zone admin montre seulement « second facteur : oui / non ». Pas d'obligation imposée par l'admin en v1.

### 10.2 Écarté

- **Passkeys gérées par Nid (WebAuthn)** : une suite de #215, pas cette étape.
- **Code par SMS ou email** : passerelle tierce ou SMTP obligatoire, SMS détournable.
- **Secret TOTP haché** : impossible, il faut le relire pour calculer le code.
- **TOTP obligatoire pour tous** : ajoute un téléphone à l'installation que #215 veut simplifier ; laissé au choix de chaque adulte.

## 11. Session et CSRF de connexion

### 11.1 Décision

- **La même session qu'aujourd'hui** : cookie d'authentification ASP.NET `__Host-nid` (BFF), mêmes attributs, mêmes durées (glissante 7 jours, absolue 30), mêmes claims (`nid:person_id`, `nid:session_epoch`), révocation par `session_epoch`, CSRF des requêtes authentifiées inchangé (`authentication.md` §3). Une connexion locale met `nid:sub` à `local` et ajoute `nid:amr` (`pwd`, `pwd+otp` ; `oidc` pour une connexion OIDC) que la page Sécurité et l'événement `login` affichent. Le mode démo fait déjà ainsi (`SessionClaims.Create`) : un seul validateur, un seul cookie.
- **CSRF de connexion** : les `POST` sans session (`/auth/login/password`, `/auth/login/totp`, `/auth/setup`, `/auth/link/inspect`, `/auth/link/complete`) reçoivent une protection propre, puisque le jeton CSRF actuel dépend de la session :
  1. **Cookie de pré-session** `__Host-nid-pre` : 32 octets aléatoires, `HttpOnly`, `SameSite=Strict`, 30 minutes, posé par `GET /auth/session` quand personne n'est connecté.
  2. **Jeton** : `GET /auth/session` rend aussi `csrf` = HMAC-SHA256(`Auth:CsrfKey`, `"pre:" + valeur du cookie`) ; ces routes exigent l'en-tête `X-Nid-Csrf` correspondant (comparaison en temps constant).
  3. **Origine** : `Origin` présent et égal à `Auth:PublicUrl` (son absence est refusée sur ces routes), `Sec-Fetch-Site` à `same-origin` s'il est envoyé.
  4. **Corps JSON** seulement (`Content-Type: application/json`) : un formulaire HTML d'un autre site ne peut pas en envoyer sans requête préalable, et aucune configuration CORS ne l'autorise.
- À la connexion réussie, le cookie de pré-session est supprimé et un nouveau ticket de session émis (pas de fixation de session) ; `returnUrl` suit la règle existante (chemin relatif seulement).
- Les jetons d'accès personnels n'atteignent aucune de ces routes ni celles du compte (`IBrowserOnly`, `403 platform.browser_only`).

### 11.2 Écarté

- **Une session stockée en base** : le cookie chiffré et `session_epoch` suffisent déjà (`authentication.md` §3).
- **Pas de protection de la connexion** : un site tiers pourrait connecter un adulte au compte d'un autre sur la même instance, et lui faire déposer ses documents chez lui.
- **`SameSite` seul** : ne couvre pas un sous-domaine voisin compromis (même site) ; l'origine et le jeton le couvrent.

## 12. HTTP sans TLS : option explicite, désactivée par défaut

### 12.1 Décision

- **Par défaut, rien ne change** : HTTPS terminé par un proxy inverse reste la façon documentée et par défaut (`authentication.md` §7). Le cookie garde `__Host-` et `Secure`. Les navigateurs courants acceptent déjà un cookie `Secure` sur `http://localhost` : l'essai sur son propre poste n'a pas besoin de l'option.
- **`Auth:AllowInsecureHttp=true`**, réglé explicitement par l'hébergeur (réseau domestique, adresse IP locale) : les cookies (`nid`, `nid-pre`, `nid-mfa`, sans le préfixe `__Host-` qui exige `Secure`) suivent le schéma de la requête (`CookieSecurePolicy.SameAsRequest`) ; une requête en HTTPS garde `Secure`.
  - Un avertissement est journalisé **à chaque démarrage** (niveau `Warning`) : les mots de passe et le cookie voyagent en clair sur le réseau.
  - La zone admin le montre en bandeau sur toutes ses pages et dans Système (`GET /api/v1/admin/system` : `insecureHttp: true`).
- **Garde-fous sans l'option** :
  - `Auth:PublicUrl` en `http://` arrête le démarrage avec un message qui nomme l'option, sauf pour `localhost`, `127.0.0.1` et `[::1]`.
  - Une requête de connexion par mot de passe arrivée en HTTP (après `X-Forwarded-Proto`) hors boucle locale répond `400 auth.https_required`, et la page dit à l'hébergeur ce qu'il manque : le mot de passe n'est jamais **accepté** sans TLS sans que l'hébergeur l'ait voulu.
- L'option vaut pour tous les modes ; en pratique Pocket ID exige déjà le HTTPS.

### 12.2 Écarté

- **HTTP par défaut** : décision du propriétaire, et ce serait le défaut de tous les tutoriels copiés.
- **Basculer seul en HTTP quand la requête arrive en HTTP** : un proxy mal réglé ferait passer une installation exposée en clair sans que personne le sache.
- **TLS intégré à Nid** (certificat auto-signé ou ACME) : c'est le rôle du proxy inverse, que la documentation fournit déjà (Caddy).

## 13. Modèle de données (schéma `core`)

### 13.1 `local_credential`

| Champ | Type | Remarques |
|---|---|---|
| `person_id` | uuid, PK, FK `person` | Un `adult` ; `on delete cascade` (adulte en attente supprimé) |
| `login` | citext, unique | Identifiant (§7.1) |
| `password_hash` | text | Format V3 de `PasswordHasher` (fonction, itérations, sel, hash) |
| `password_changed_at` | timestamptz | |
| `failed_count` | int | Échecs consécutifs depuis la dernière réussite ou le dernier verrouillage |
| `last_failed_at?` | timestamptz | |
| `lock_level` | int | Nombre de verrouillages successifs (durée = base × 2^(niveau − 1), plafonnée) |
| `locked_until?` | timestamptz | |
| `totp_secret?` | text | Secret chiffré par Data Protection ; vide sans TOTP |
| `totp_pending_since?` | timestamptz | Secret tiré, pas encore confirmé |
| `totp_enabled_at?` | timestamptz | |
| `totp_last_step?` | bigint | Dernier pas TOTP accepté (rejeu) |
| `last_login_at?` | timestamptz | |
| `created_at` | timestamptz | |

`ck_local_credential_adult` : `person.kind = 'adult'` (vérifié à l'écriture par Core, la contrainte ne voit pas `person`).

### 13.2 `credential_link`

| Champ | Type | Remarques |
|---|---|---|
| `id` | uuid | |
| `person_id` | uuid, FK `person` | |
| `purpose` | text | `enroll` (pas encore de compte local) ou `reset` |
| `token_hash` | char(64), unique | SHA-256 du jeton complet |
| `created_by?` | uuid | L'admin ; vide pour la CLI |
| `via` | text | `ui`, `admin-cli` |
| `created_at`, `expires_at` | timestamptz | |
| `used_at?`, `revoked_at?` | timestamptz | |

Index unique partiel : un seul lien vivant (`used_at` et `revoked_at` vides) par `person_id`. Les liens utilisés, révoqués ou expirés restent en base, comme les jetons ; un job de nuit efface ceux de plus de 90 jours.

### 13.3 `recovery_code`

| Champ | Type | Remarques |
|---|---|---|
| `id` | uuid | |
| `person_id` | uuid, FK `person` | |
| `code_hash` | char(64) | SHA-256 du code normalisé (sans tirets, en majuscules) |
| `created_at`, `used_at?` | timestamptz | |

### 13.4 Ce qui change ailleurs

- `person` ne change pas de forme. L'invariant « un `adult` actif a un `oidc_subject` » devient « un `adult` actif a un `oidc_subject` ou un `local_credential` » (`../core-data-model.md` §4.2) ; `PersonInfo.IsPending` suit la même règle.
- `person.email` devient facultatif pour un `adult` en `local` (§7.1).
- Le verrou consultatif du provisionnement couvre aussi la mise en place, la consommation d'un lien `enroll` (plafond d'adultes) et les changements de rôle admin (§6.1).

### 13.5 Export, import, sauvegarde

- L'export `nid.data` **ne contient aucune** de ces tables ; le banc des exports le vérifie (§18.1).
- Un foyer importé dans une instance en `local` y arrive sans comptes locaux : ses adultes sont en attente, un admin leur donne un lien.
- Les sauvegardes (dump PostgreSQL et volume Data Protection) contiennent les hashes et les secrets TOTP chiffrés : dans la frontière de confiance de l'hébergeur (`administration-and-audit.md` §1), protégés par le chiffrement restic (`../security/risk-register.md` INF-2).

## 14. Routes

### 14.1 Session

| Route | Changement |
|---|---|
| `GET /auth/session` | Ajoute `methods` (`["password"]`, `["oidc"]` ou les deux), `setupRequired` (§5) ; hors session, rend un `csrf` de pré-session et pose `__Host-nid-pre` (§11). `identityProvider` vide en `local` |
| `GET /auth/login?returnUrl=` | `oidc` : inchangé. `local` et `both` : redirige vers la page `/login?returnUrl=` du SPA |
| `GET /auth/login/oidc?returnUrl=` | `both` seulement : lance le flux OIDC (ce que `/auth/login` fait en `oidc`) |
| `POST /auth/logout` | Inchangé |

### 14.2 Connexion locale (sans session, CSRF de connexion §11)

| Route | Corps | Réponses |
|---|---|---|
| `POST /auth/login/password` | `{ login, password, returnUrl? }` | `200 { next: "done", returnUrl }` (session ouverte) ou `{ next: "totp" }` (cookie d'étape) ; `401 auth.invalid_credentials` ; `429` ; `400 auth.https_required` |
| `POST /auth/login/totp` | `{ code }` ou `{ recoveryCode }` | `200 { next: "done", returnUrl }` ; `401 auth.invalid_code` ; `401 auth.mfa_expired` (reprendre au mot de passe) ; `429` |
| `POST /auth/setup` | `{ token, name, login, password }` | `201` ; `400` règles de mot de passe ; `401 auth.invalid_setup_token` ; `404` mise en place fermée |
| `POST /auth/link/inspect` | `{ token }` | `200 { purpose, name, suggestedLogin }` ; `404` pour tout lien inutilisable |
| `POST /auth/link/complete` | `{ token, login?, password }` (`login` pour `enroll`) | `204` ; `400` règles ; `409 core.login_taken` ; `404` |

Ces routes ne sont mappées qu'en `local` et `both`, jamais en mode démo. Leurs corps ne sont jamais journalisés ; elles sont hors compression comme le reste de `/auth` (`../security/application.md` §2).

### 14.3 Compte de l'adulte (`IBrowserOnly`, CSRF de session)

| Route | Rôle |
|---|---|
| `GET /api/v1/core/me/credential` | `{ login, passwordChangedAt, totpEnabled, recoveryCodesLeft }`, ou `404` sans compte local |
| `PUT /api/v1/core/me/password` | `{ current, new }` : change le mot de passe, ferme les autres sessions (§8) |
| `PUT /api/v1/core/me/login` | `{ password, login }` : change l'identifiant |
| `POST /api/v1/core/me/totp` | Tire un secret en attente : `{ secret, otpauthUri }` |
| `POST /api/v1/core/me/totp/confirm` | `{ code }` : active le TOTP, rend les 10 codes de secours une fois |
| `DELETE /api/v1/core/me/totp` | `{ password, code }` : désactive le TOTP et efface les codes |
| `POST /api/v1/core/me/totp/recovery-codes` | `{ password }` : régénère les codes, rend les nouveaux une fois |

### 14.4 Zone admin (`IAdminOnly`)

| Route | Rôle |
|---|---|
| `GET /api/v1/admin/users` | Ajoute par adulte : `methods` (`password`, `oidc`), `lockedUntil`, `totpEnabled`, `linkExpiresAt` |
| `POST /api/v1/admin/users` | Email facultatif en `local` |
| `POST /api/v1/admin/users/{id}/password-link` | `{ revokeTokens? }` : `201 { url, purpose, expiresAt }`, l'adresse une fois ; révoque le lien précédent |
| `DELETE /api/v1/admin/users/{id}/password-link` | Révoque le lien en attente |
| `POST /api/v1/admin/users/{id}/unlock` | Lève le verrouillage |
| `PUT /api/v1/admin/users/{id}/admin` | `{ admin: bool }` : avec `Auth:AdminSource=nid` seulement (`409 core.admin_source_identity_provider` sinon) ; `409 core.last_admin` |
| `GET /api/v1/admin/system` | Ajoute `authMode`, `adminSource`, `insecureHttp` |

### 14.5 Pages du SPA

`/login` (identifiant, mot de passe, lien « Se connecter avec le fournisseur d'identité » en `both`, puis l'étape du code), `/setup` (mise en place), `/password` (lien d'arrivée ou de réinitialisation), page Sécurité : sections « Mot de passe » et « Validation en deux étapes » (avec `InfoTip` pour TOTP et codes de secours) ; zone admin › Utilisateurs : actions du menu de ligne « Créer un lien de mot de passe », « Déverrouiller », « Donner / retirer le rôle admin », chacune confirmée. Composants shadcn/ui et grammaire de page habituelle (`../frontend/design-system.md` §6) ; pages chargées à la demande (budget du JavaScript initial).

## 15. Commandes d'administration (CLI)

Mêmes règles que les commandes existantes (`administration-and-audit.md` §3) : cas d'usage de l'API joués par l'acteur système avec `via: admin-cli`, un `admin_command` par exécution (paramètres sans secret, jamais le lien), codes de sortie 0/1/2, messages en français.

| Commande | Usage |
|---|---|
| `create-admin --name <nom> [--email <email>]` | Crée un adulte admin (dans la limite de `max_adults`) et affiche un lien `enroll`, où il choisit son identifiant ; refusée avec `Auth:AdminSource=identity_provider` |
| `reset-password --person <nom ou id> [--revoke-tokens]` | Affiche un lien `reset` (ou `enroll` si l'adulte n'a pas de compte local), lève le verrouillage |
| `grant-admin --person`, `revoke-admin --person` | Rôle admin, source `nid` seulement ; `revoke-admin` refuse le dernier admin |
| `reset-totp --person` | Désactive le TOTP, efface les codes de secours, ferme les sessions |
| `unlock --person` | Lève le verrouillage |

`list-persons` ajoute les colonnes « connexion » (`mot de passe`, `oidc`) et « verrouillé ». Le lien est écrit sur la sortie standard seulement, jamais dans le journal ni dans l'audit.

## 16. Événements de sécurité

Ajoutés à `administration-and-audit.md` §4.1. Données : identifiants seulement, jamais un secret ni un identifiant tapé inconnu.

| Événement | Quand | Données | Journal de la zone admin |
|---|---|---|---|
| `login` | Existant ; ajoute `method` (`password`, `password+totp`, `oidc`) | `method` | Non (inchangé) |
| `login_failed` | Mot de passe, code TOTP ou de secours faux, compte verrouillé, identifiant inconnu ; au plus un par compte (ou adresse) et par minute | `reason` (`bad_password`, `bad_code`, `locked`, `unknown_login`), `count`, `address` | Seulement sans personne (`unknown_login`) |
| `login_locked` | Un compte passe verrouillé | `level`, `until` | Oui |
| `password_changed` | L'adulte change son mot de passe (ou son identifiant : `field: login`) | `field` | Non |
| `password_reset` | Un lien `reset` est utilisé | `link_id` | Oui |
| `credential_link_issued`, `credential_link_revoked` | Un admin ou la CLI crée ou révoque un lien | `purpose`, `link_id`, `by` | Oui |
| `account_enrolled` | Un lien `enroll` est utilisé : l'adulte devient actif | `link_id` | Oui |
| `account_unlocked` | Déverrouillage par un admin ou la CLI | `by` | Oui |
| `totp_enabled`, `totp_disabled` | L'adulte active ou désactive le TOTP | — | Non |
| `totp_reset` | `nid admin reset-totp` | — | Oui |
| `recovery_code_used` | Connexion par un code de secours | `left` | Non |
| `recovery_codes_regenerated` | Nouveaux codes | — | Non |
| `setup_completed` | Mise en place terminée par la page | — | Oui |
| `admin_granted`, `admin_revoked` | Existant ; ajoute `source: nid` ou `admin-cli` et `by` | `source`, `by` | Oui (inchangé) |

Les événements d'une personne restent visibles d'elle seule (page Sécurité) ; le journal de la zone admin ne montre pas les connexions ni les échecs d'un autre adulte, seulement ce dont un admin a besoin pour agir (verrouillages, liens, réinitialisations). Notifications au titulaire : `login_locked`, `password_reset`, `password_changed`, `credential_link_issued` (purpose `reset`), `totp_reset`, codes de secours à 2 restants.

## 17. Configuration

Variables d'environnement sous la forme `Auth__…` (`Auth__Local__LockThreshold`).

| Clé | Défaut | Rôle |
|---|---|---|
| `Auth:Mode` | Déduit : `oidc` si `Auth:Authority` est renseigné, `local` sinon | `local`, `oidc`, `both` (§2) |
| `Auth:AdminSource` | `identity_provider` en `oidc`, `nid` sinon | Qui décide du rôle admin (§6) |
| `Auth:AllowInsecureHttp` | `false` | Cookies sans `Secure` sur HTTP (§12) |
| `Auth:Local:MinPasswordLength` | 12 | De 12 à 64 (§4) |
| `Auth:Local:LinkLifetimeHours` | 72 | De 1 à 168 (§7) |
| `Auth:Local:LockThreshold` | 5 | Échecs avant verrouillage, de 3 à 20 (§9) |
| `Auth:Local:LockMaxMinutes` | 60 | Plafond du verrouillage croissant, de 1 à 1440 (§9) |
| `Auth:Local:MaxConcurrentHashes` | 4 | Calculs de hash simultanés (§3) |

Inchangées : `Auth:PublicUrl`, `Auth:CsrfKey` (sert aussi au CSRF de connexion), les durées de session, `Auth:TrustedNetworks`. `Auth:Authority`, `Auth:ClientId`, `Auth:ClientSecret`, `Auth:AccessGroup` et `Auth:AdminGroup` ne servent qu'en `oidc` et `both` (`AdminGroup` qu'avec la source `identity_provider`). Dans `deploy/standalone/compose.yaml`, `AUTH_URL` et `NID_OIDC_CLIENT_SECRET` deviennent facultatifs et `NID_AUTH_MODE` vide par défaut (§2.1).

## 18. Plan de test

### 18.1 Intégration (`tests/Nid.Host.IntegrationTests`, `tests/Nid.Core.IntegrationTests`)

- **Modes** : sans `Auth:Mode`, `Auth:Authority` renseigné donne `oidc` (routes locales absentes, `/auth/login` lance OIDC, rôle admin pris du groupe : les tests OIDC existants passent sans changement) ; sans `Authority`, `local`. `both` offre les deux et ignore le groupe admin. Valeur inconnue, `local` avec `AdminSource=identity_provider`, `oidc` sans `Authority` : le démarrage échoue. Mode démo : routes locales absentes.
- **Mise en place** : jeton journalisé une fois au démarrage d'une base vide ; mauvais jeton `401` et compté par adresse ; bon jeton crée un admin actif ; ensuite `404`, même avec le bon jeton ; deux `POST /auth/setup` simultanés n'en créent qu'un ; une base avec un adulte n'ouvre pas la mise en place.
- **Connexion** : bon couple ouvre `__Host-nid` avec les claims attendus ; identifiant sans tenir compte de la casse ; mot de passe normalisé NFKC ; identifiant inconnu et mauvais mot de passe donnent la même réponse (statut, corps) ; un hash à 100 000 itérations est recalculé à 210 000 à la connexion ; `returnUrl` absolu refusé.
- **CSRF de connexion** : sans `__Host-nid-pre`, sans en-tête, avec un jeton d'un autre cookie, sans `Origin`, avec un `Origin` étranger, en `application/x-www-form-urlencoded` : `403` sur chaque route du §14.2.
- **Verrouillage** : 5 échecs verrouillent 1 minute (horloge de test), le bon mot de passe est alors refusé avec la réponse générique ; le palier double, plafonné ; une réussite remet à zéro ; `unlock` et un lien `reset` aussi ; 20 échecs en 10 minutes d'une adresse donnent `429` ; `login_failed` agrégé (un par minute) et `login_locked` journalisés, sans identifiant inconnu.
- **Règles** : 11 caractères refusés, 12 acceptés, 257 refusés ; mot de passe de la liste ou contenant l'identifiant refusé ; emoji et espaces acceptés.
- **Liens** : un lien `enroll` crée le compte et rend l'adulte actif ; réutilisé, expiré, révoqué ou remplacé : `404` identique ; un seul lien vivant par adulte ; `reset` remplace le hash, incrémente `session_epoch` (l'ancien cookie est refusé), garde le TOTP ; `revokeTokens` révoque les jetons ; identifiant déjà pris `409`.
- **Rôle admin** : promotion et rétrogradation journalisées avec `source: nid` ; le dernier admin ne peut pas être retiré, même par deux requêtes simultanées ; un admin retiré perd la zone admin à la requête suivante ; avec `AdminSource=identity_provider`, `PUT …/admin` répond `409`.
- **TOTP** : activation après un code juste seulement ; code d'un pas voisin accepté, d'un pas déjà utilisé refusé ; cookie d'étape expiré ; code de secours à usage unique ; régénération invalide les anciens ; `reset-totp` ferme les sessions.
- **Compte** : changer son mot de passe ferme les autres sessions et réémet la courante ; mot de passe actuel faux compté comme échec ; un jeton d'accès personnel reçoit `403 platform.browser_only` sur `/api/v1/core/me/password` et sur le TOTP.
- **HTTP** : `PublicUrl` en `http://` sans l'option arrête le démarrage (sauf boucle locale) ; connexion en HTTP hors boucle locale `400 auth.https_required` ; avec l'option, cookie `nid` sans `Secure` sur HTTP, avertissement journalisé, `insecureHttp` dans `/admin/system`.
- **Aucune fuite** : après un parcours complet, ni les journaux capturés, ni `platform.audit_log`, ni l'export `nid.data` ne contiennent le mot de passe, son hash, le secret TOTP, un code de secours, un jeton de lien ou le jeton de mise en place.
- **CLI** (`AdminCli`) : `create-admin`, `reset-password`, `grant-admin`, `revoke-admin` (dernier admin refusé), `reset-totp`, `unlock` ; chacune écrit un `admin_command` sans le lien.

### 18.2 Playwright (`web/e2e/`)

Une pile `local` (Nid et PostgreSQL seuls, sans Pocket ID) dans `e2e.yml`, à côté de la pile OIDC existante, qui garde tous ses parcours.

- `local-setup.spec.ts` : base vide, l'adresse de mise en place lue dans le journal du conteneur, création de l'admin, connexion, tableau de bord.
- `local-accounts.spec.ts` : l'admin pré-enregistre Bob sans email, crée un lien, Bob choisit identifiant et mot de passe, se connecte, se déconnecte ; mauvais mot de passe : message générique ; lien réutilisé : page « lien inutilisable ».
- `local-reset.spec.ts` : Alice crée un lien de réinitialisation pour Bob ; la session ouverte de Bob est fermée dès sa requête suivante après l'usage du lien ; Bob se connecte avec le nouveau mot de passe.
- `local-totp.spec.ts` : Bob active le TOTP (code calculé dans le test depuis le secret affiché), se reconnecte avec un code, puis avec un code de secours ; le compteur de codes baisse.
- `security.spec.ts` (existant) : sections « Mot de passe » et « Validation en deux étapes » présentes en `local`, absentes en `oidc`.
- Admin : donner puis retirer le rôle admin à Bob ; retirer le sien quand on est seul admin est refusé avec un message clair.

## 19. Découpage en PR

1. **Modes et transport** (`backend`, `infra`) : `Auth:Mode` et sa déduction, `Auth:AdminSource`, validation au démarrage, `Auth:AllowInsecureHttp` et ses garde-fous, `methods` et `setupRequired` dans `/auth/session`, `authMode` dans `/admin/system`. Aucun changement pour une installation OIDC.
2. **Comptes et connexion par mot de passe** (`backend`) : tables `local_credential` et `credential_link`, hachage, règles et liste de refus, `POST /auth/login/password`, CSRF de connexion, verrouillage et limite par adresse, événements, liens (`/auth/link/*`, `password-link`, `unlock`), mise en place par jeton, CLI `create-admin`, `reset-password`, `unlock`, invariant « adulte actif ».
3. **Pages de connexion** (`web`) : `/login`, `/setup`, `/password`, page Sécurité « Mot de passe » (`GET /me/credential`, `PUT /me/password`), zone admin (lien, déverrouillage, pré-enregistrement sans email). Les parcours Playwright de la pile `local` passent à la PR 6, qui crée la pile sans Pocket ID.
4. **Rôle admin dans Nid** (`backend`, `web`) : `PUT /admin/users/{id}/admin`, dernier admin, `grant-admin` / `revoke-admin`, `both` qui ignore le groupe admin.
5. **TOTP et codes de secours** (`backend`, `web`) : table `recovery_code`, routes `me/totp`, étape du code à la connexion, `reset-totp`, notifications.
6. **Installation sans Pocket ID** (`infra`, docs) : `deploy/standalone/` en `local` sans Pocket ID, `README.md` et `deploy/README.md` qui en font le chemin par défaut et présentent Pocket ID comme option ; une pile `local` dans `e2e.yml` avec les parcours Playwright du §18.2 ; puis l'essai d'installation neuve en suivant le guide.

Chaque PR met à jour la documentation de ce dossier pour ce qu'elle livre (« livré en … »), comme les étapes précédentes.
