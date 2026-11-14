# Déployer Nid

Ce dossier contient le **compose de référence** de Nid : la seule forme livrée (`../docs/architecture.md` §10). Il lance
quatre services : `postgres`, `pocket-id` (le fournisseur d'identité), `nid` (l'application : l'API et l'interface, servies
sur la même origine et le même port) et `backup` (la sauvegarde chiffrée, §6).

**Plus simple : [`standalone/`](standalone/compose.yaml).** Un seul fichier et un `.env`, sans script, et sans fournisseur
d'identité : chaque adulte se connecte avec un identifiant et un mot de passe gardés par Nid (§4.1), avec la validation en
deux étapes en option. `nid` tourne toujours ; Postgres est un profil (`--profile postgres`) qu'on laisse de côté quand on
l'a déjà ailleurs, Pocket ID aussi (`--profile pocket-id`), seulement pour se connecter par passkey.

```bash
cd deploy/standalone
cp .env.example .env                    # NID_URL, NID_CSRF_KEY (openssl rand -hex 32), NID_DB_PASSWORD
docker compose --profile postgres up -d
docker compose logs nid | grep setup    # l'adresse à usage unique qui crée le premier administrateur
```

Pour essayer sur votre propre ordinateur, `NID_URL=http://localhost:8080` suffit, sans proxy. Pas de sauvegarde ni de
`restore.sh` dans le standalone : le reste de ce document décrit la forme de référence.

Les images sont publiques sur `ghcr.io/mistribe/nid` et `ghcr.io/mistribe/nid-backup`, pour amd64 et arm64, sans compte :
`latest`, `X.Y.Z` ou `X.Y` (le dernier correctif). Chacune porte une attestation de provenance signée :
`gh attestation verify oci://ghcr.io/mistribe/nid:latest --repo Mistribe/Nid` vérifie qu'elle sort de ce dépôt.

## 1. Prérequis

- Docker avec compose v2, `openssl` et `bash` sur l'hôte.
- **Deux noms d'hôte**, par exemple `nid.example.org` et `auth.example.org`.
- **Des passkeys** : Pocket ID n'authentifie que par passkey (empreinte, clé de sécurité, gestionnaire de mots de passe). Chaque
  adulte doit pouvoir en créer une, sur un appareil qui voit `https://auth.<domaine>`.
- **Un reverse proxy TLS** fourni par l'hébergeur (Caddy, Traefik, nginx…). Nid ne termine pas le TLS lui-même.
- Un accès sortant HTTPS pour `nid` (catalogue de fournisseurs et taux de change, et Enable Banking si la synchronisation bancaire est activée, §12). Sans lui, Nid fonctionne, mais
  les fournisseurs et les taux se saisissent à la main. `nid` est **le seul service qui sort** : tous les autres
  réseaux du compose sont internes.
- **`nid` doit joindre `https://auth.<domaine>` depuis l'intérieur.** Pour la connexion, il appelle Pocket ID à
  son adresse publique (découverte, jeton, profil). Si votre routeur ne sait pas renvoyer vers lui-même le trafic
  destiné à votre adresse publique (« NAT loopback »), ou si votre DNS diffère à l'intérieur, la connexion échoue.
  Dans ce cas, faites résoudre ce nom vers votre proxy depuis `nid`, par exemple avec un fichier
  `deploy/compose.override.yaml` :

  ```yaml
  services:
    nid:
      extra_hosts: ["auth.example.org:192.168.1.10"]   # l'adresse de votre proxy sur le réseau local
  ```

## 2. Installer

L'installation se fait en deux temps, parce que Pocket ID choisit lui-même le secret du client de Nid et ne le montre
qu'une fois :

```bash
deploy/setup.sh                                          # noms d'hôte, secrets, .env
docker compose -f deploy/compose.yaml up -d pocket-id    # d'abord Pocket ID seul
```

Branchez le proxy (§3), puis **ouvrez tout de suite `https://auth.<domaine>/setup`** : la première personne qui y arrive
crée l'administrateur de Pocket ID et sa passkey. Ne publiez pas l'adresse avant de l'avoir fait. Puis, dans Pocket ID :

1. créez deux groupes : **`nid-users`** (qui peut entrer dans Nid) et **`nid-admins`** (qui administre Nid) ;
2. créez un client OIDC « Nid » : redirection `https://nid.<domaine>/auth/callback`, déconnexion
   `https://nid.<domaine>/signed-out`, PKCE activé, consentement ignoré (« Skip consent »), **réservé au groupe `nid-users`** ;
3. créez les adultes du foyer, chacun dans `nid-users`, et l'administrateur de Nid aussi dans `nid-admins` ;
4. copiez l'identifiant et le secret du client, et rendez-les à Nid :

```bash
deploy/setup.sh --oidc-client-id <identifiant> --oidc-client-secret <secret>
docker compose -f deploy/compose.yaml up -d --build
```

`setup.sh` génère :

- tous les secrets, dans `deploy/secrets/` : un fichier par secret, dans un dossier fermé (`0700`), jamais versionné,
  dont la clé de chiffrement de Pocket ID ;
- le fichier `deploy/.env` ;
- le mot de passe de la sauvegarde, `deploy/backup-secrets/restic_password`, et le dossier `deploy/backups/`. Le script
  affiche ce mot de passe une fois : **notez-le hors de cette machine** (§6).

Vous pouvez le relancer : **il ne remplace jamais un secret existant**, sauf le secret du client quand vous le donnez avec
`--oidc-client-secret`. Les noms de groupe `nid-users` et `nid-admins` sont des valeurs par défaut : pour d'autres noms,
définissez `Auth__AccessGroup` et `Auth__AdminGroup` pour `nid` dans un `deploy/compose.override.yaml`.

Une liste `Outbound:AllowedHosts` configurée remplace celle par défaut : elle doit contenir `www.boursorama.com` et
`query2.finance.yahoo.com` pour la collecte des cours, `bdm.insee.fr` pour celle de l'IRL, `discord.com` et `discordapp.com` pour les webhooks Discord des notifications et `api.enablebanking.com` pour la synchronisation bancaire.

## 3. Brancher le proxy

Le compose ne publie aucun port. Votre proxy rejoint deux cibles sur le réseau Docker `nid_edge` (le préfixe `nid_`
vient du nom du projet compose ; ce réseau est interne, votre proxy garde ses propres réseaux pour l'extérieur) :

| Nom public | Cible |
|---|---|
| `nid.<domaine>` | `nid:8080` (HTTP) |
| `auth.<domaine>` | `pocket-id:1411` (HTTP) |

Le proxy doit **fixer lui-même** `X-Forwarded-Proto`, `X-Forwarded-Host` et `X-Forwarded-For`, en écrasant ce que le
client envoie. Caddy et Traefik le font par défaut ; nginx non (voir l'exemple ci-dessous). `nid` sert l'interface et
l'API sur la même origine : le proxy n'a rien à router par préfixe.

**Quel proxy `nid` croit-il ?** `nid` n'accepte `X-Forwarded-*` que des réseaux de confiance. Par défaut, ce sont les
plages privées et locales (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `127.0.0.0/8`, `fd00::/8`, `::1`) : là où
se trouve votre proxy, sur `nid_edge` ou sur l'hôte. Pour ne croire que lui, donnez son adresse ou son réseau dans un
`deploy/compose.override.yaml` : `Auth__TrustedNetworks__0` (puis `__1`, …) remplace la liste par défaut, et
`0.0.0.0/0` avec `::/0` fait croire n'importe quelle source.

```yaml
services:
  nid:
    environment:
      Auth__TrustedNetworks__0: 172.20.0.5/32   # l'adresse de votre proxy
```

Exemple avec Caddy, dans un conteneur rattaché au réseau `nid_edge` :

```
nid.example.org {
	reverse_proxy nid:8080
}
auth.example.org {
	reverse_proxy pocket-id:1411
}
```

Exemple avec Traefik, en fichier de configuration dynamique :

```yaml
http:
  routers:
    nid: { rule: "Host(`nid.example.org`)", service: nid, tls: {} }
    auth: { rule: "Host(`auth.example.org`)", service: auth, tls: {} }
  services:
    nid: { loadBalancer: { servers: [{ url: "http://nid:8080" }] } }
    auth: { loadBalancer: { servers: [{ url: "http://pocket-id:1411" }] } }
```

**Proxy dans un autre projet compose** : déclarez le réseau comme externe dans ce projet-là.

```yaml
services:
  proxy:
    networks: [default, nid_edge]
networks:
  nid_edge: { external: true }
```

**Proxy installé sur l'hôte** (hors Docker) : il ne peut pas entrer dans un réseau interne. Publiez les deux cibles sur
l'adresse locale avec un fichier `deploy/compose.override.yaml`, qui leur donne un réseau ordinaire pour cela :

```yaml
services:
  nid:
    ports: ["127.0.0.1:8080:8080"]   # nid est déjà sur `egress`, un réseau ordinaire : rien d'autre à lui donner
  pocket-id:
    ports: ["127.0.0.1:1411:1411"]
    networks: [edge, published]
networks:
  published: {}
```

Avec nginx sur l'hôte, pour `nid.example.org` (même chose pour Pocket ID sur le port 1411) :

```nginx
location / {
    proxy_pass http://127.0.0.1:8080;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-Forwarded-Host $host;
    proxy_set_header X-Forwarded-For $remote_addr;
}
```

**Compression** : `nid` compresse déjà (Brotli ou gzip) les fichiers de l'application et les réponses de `/api`.
Votre proxy n'a rien à ajouter, et les exemples ci-dessus ne compressent rien : Caddy ne compresse qu'avec `encode`,
Traefik qu'avec le middleware `compress`, et nginx, même avec `gzip on`, ne compresse que le HTML sans `gzip_types`.
Si votre proxy compresse quand même, n'activez pas la compression pour `/auth` et `/api/v1/core/me/tokens` : leurs
réponses portent un secret (le jeton CSRF, un jeton d'accès à sa création), et une réponse compressée qui mêle un secret
à des données qu'un tiers influence l'expose à l'attaque BREACH. Pour vérifier ce qui arrive au navigateur :
`curl -sI -H "Accept-Encoding: gzip" https://nid.<domaine>/auth/session` ne doit pas montrer `Content-Encoding`.

**Essai sur un poste**, sans proxy : `deploy/compose.local.yaml` ajoute un Caddy à certificats locaux sur
`nid.localtest.me` et `auth.localtest.me`. `deploy/smoke.sh` fait l'essai complet (préparation, démarrage, utilisateurs et client de Pocket ID par son API,
parcours Playwright) ; ce script est jetable, ne le lancez pas sur une installation réelle.

## 4. Qui entre dans Nid, et qui l'administre

`Auth__Mode` choisit comment les adultes se connectent : `local` (identifiant et mot de passe gardés par Nid), `oidc`
(Pocket ID) ou `both` (les deux). Sans réglage, c'est `oidc` quand `Auth__Authority` est renseigné (le compose de
référence), `local` sinon (le standalone) : une installation existante ne change pas. Le détail et les raisons :
[`../docs/identity-and-access/local-accounts.md`](../docs/identity-and-access/local-accounts.md).

### 4.1 Comptes locaux

- **Le premier administrateur** : au premier démarrage d'une base vide, Nid écrit dans son journal une adresse à usage
  unique (`docker compose logs nid | grep setup`) qui crée son compte. Sans accès au journal :
  `nid admin create-admin --name <prénom>` affiche un lien où il choisit son identifiant et son mot de passe.
- **Les autres adultes** : l'administrateur les ajoute dans Administration › Utilisateurs (l'email est facultatif), puis
  crée leur lien d'accès depuis le menu de leur ligne et le leur transmet. Le lien vaut 72 heures, une seule fois.
- **Un mot de passe oublié** : un lien de mot de passe, depuis le même menu, ou `nid admin reset-password --person …` (§8).
  Il ferme les sessions de l'adulte. Après 5 échecs, le compte se verrouille quelques minutes ; « Déverrouiller » le rouvre.
- **Le rôle admin** se donne et se retire depuis le menu de chaque ligne ; il reste toujours au moins un administrateur.
- **La validation en deux étapes** se règle par chaque adulte sur sa page Sécurité (application d'authentification, et
  10 codes de secours). Téléphone et codes perdus : `nid admin reset-totp --person …` (§8).
- **Sans TLS**, Nid refuse un mot de passe, sauf sur `localhost`. Sur un réseau domestique de confiance, sans proxy,
  `NID_ALLOW_INSECURE_HTTP=true` l'accepte en clair : la zone admin l'affiche en bandeau.

### 4.2 Pocket ID

**Pocket ID décide**, par les groupes de la personne, appliqués à chaque connexion :

- **`nid-users`** : l'accès. Hors de ce groupe, Pocket ID refuse la connexion (le client lui est réservé), et Nid la
  refuse aussi si le groupe manque dans la réponse.
- **`nid-admins`** : l'administration de Nid (utilisateurs, foyer, tâches, système). Elle ne donne aucun droit sur
  les éléments des autres. Retirer quelqu'un du groupe lui retire le rôle à sa **prochaine connexion** : ses sessions
  ouvertes gardent leur droit d'accès jusqu'à leur fin (30 jours au plus) ou jusqu'à « Déconnecter partout »
  (Administration › Utilisateurs).

Nid crée l'adulte à sa première connexion, dans la limite du plafond du foyer (2 par défaut, réglable dans Administration ›
Foyer). Aucun adulte n'est admin d'office : tant que personne n'est dans `nid-admins`, personne n'administre Nid, et c'est
Pocket ID qu'il faut corriger. Administration › Utilisateurs montre les adultes et leur rôle, sans le modifier.

Pré-enregistrer un adulte (nom et email) reste possible : cela permet de lui attribuer des éléments avant sa première
connexion (un import, par exemple). Sa première connexion avec cet email le rattache.

**Un adulte dont l'identité change** (Pocket ID réinstallé, compte recréé) est refusé : son email est déjà celui d'un
adulte de Nid, rattaché à l'ancien identifiant. Rattachez-le au nouveau avec `nid admin rebind-subject` (§8).

## 5. Mettre à jour

**Installation d'avant la sauvegarde (1F1)** : le service `backup` n'existe pas encore. Faites `git pull`, puis
`deploy/setup.sh`, qui crée `deploy/backup-secrets/` et le mot de passe de la sauvegarde sans toucher aux autres
secrets, puis l'étape 3. Sans `deploy/backup-secrets/`, `up` s'arrête avec un message plutôt que de le créer.

1. **Sauvegardez** : `docker compose -f deploy/compose.yaml exec backup nid-backup backup-now`.
2. `git pull`.
3. `docker compose -f deploy/compose.yaml up -d --build` (avec `-f deploy/compose.override.yaml` si vous en avez un).

Les migrations de la base s'appliquent au démarrage de `nid`. Les images de base sont épinglées par empreinte ; une
mise à jour de Nid les fait évoluer.

### Depuis `nid-api` et `nid-web` (une seule image `nid`)

L'API et l'interface sont désormais une seule image, `nid`, sur un seul service et un seul port. Les images `nid-api` et
`nid-web` ne sont plus publiées. Les volumes, les secrets et la base ne changent pas : les données et les sauvegardes
se retrouvent telles quelles.

1. **Sauvegardez** (étape 1 ci-dessus).
2. Prenez le nouveau `compose.yaml` (`git pull`, ou le fichier `standalone/`).
3. Dans `deploy/.env` (ou le `.env` du standalone), retirez `NID_API_IMAGE`, `NID_WEB_IMAGE` et `NID_APP_SUBNET`.
   `NID_IMAGE` les remplace pour une autre image ou une étiquette de version ; `NID_WEB_BIND` et `NID_WEB_PORT` gardent
   leur nom. Le réseau interne `app` et son sous-réseau fixe n'existent plus, et `Auth__TrustedNetworks__0` non plus :
   `nid` croit par défaut les plages privées (§3).
4. **Si vous avez un `deploy/compose.override.yaml`** (§1, §2, §3, §11, §12), adaptez-le avant d'aller plus loin : les services
   `nid-api` et `nid-web` s'y appellent maintenant `nid` (`ports`, `extra_hosts`, `environment`, `depends_on` vont sur ce seul
   service), et le réseau `app` n'existe plus (retirez-le des listes `networks`). Un override qui nomme encore `nid-api`
   ou `nid-web` fait échouer `up` (service sans image) ; passez-le toujours avec `-f deploy/compose.override.yaml` (§11).
5. `docker compose -f deploy/compose.yaml pull && docker compose -f deploy/compose.yaml up -d --remove-orphans`
   (ajoutez `-f deploy/compose.override.yaml` si vous en avez un ; `--build` au lieu de `pull` pour la référence construite depuis
   le dépôt). Avec le `standalone/` : `docker compose pull && docker compose up -d --remove-orphans` dans son dossier, avec les
   mêmes profils qu'avant. `--remove-orphans` retire les anciens conteneurs `nid-api` et `nid-web`.
6. **Pointez votre proxy sur `nid:8080`** (§3). Le compose de référence garde l'alias `nid-web` sur `nid_edge` : un proxy
   qui vise encore `nid-web:8080` continue de marcher, le temps de le changer. L'alias disparaîtra dans une version
   suivante.
7. Les commandes de secours se lancent désormais dans `nid` : `docker compose -f deploy/compose.yaml exec nid nid admin …` (§8).

## 6. Sauvegarder et restaurer

### Ce qui est sauvegardé

Chaque nuit à 03:30 (réglable : `NID_BACKUP_AT` et `TZ` dans `deploy/.env`), le service `backup` fait **un instantané
restic chiffré** qui contient tout ce qu'il faut pour repartir :

- la base (`pg_dump`), la base de Pocket ID (copie à chaud de sa base SQLite, et ses images ; pas avec un Pocket ID
  extérieur, §11), les clés Data Protection et les icônes des fournisseurs ;
- les documents téléversés (volume `documents`), avec le stockage fichier seulement (§ « Les documents » ci-dessous) ;
- `deploy/secrets/` (dont la clé de chiffrement de Pocket ID, sans laquelle sa base est illisible) et `deploy/.env` ;
- un manifeste : nombre de lignes par table, migrations de la base, nombre de clés.

Le dépôt local est `deploy/backups/` (réglable : `NID_BACKUP_DIR`). C'est un dossier de l'hôte et non un volume Docker :
un `docker compose down -v` ne l'emporte pas. Le dossier doit exister : s'il manque (un disque externe non monté), `up`
s'arrête au lieu de commencer un dépôt vide. Ses fichiers appartiennent à root ; lisez-les ou copiez-les avec `sudo`.

Rétention : 7 quotidiennes, 4 hebdomadaires, 12 mensuelles (`NID_BACKUP_KEEP`). Le dépôt est vérifié chaque dimanche
(`restic check`).

### Le mot de passe de la sauvegarde

`deploy/backup-secrets/restic_password` **n'est pas dans la sauvegarde** : c'est lui qui l'ouvre. Sans lui, la
sauvegarde est illisible, y compris pour vous. Gardez-en une copie hors de cette machine (gestionnaire de mots de passe,
papier). Ne supprimez jamais ce fichier ; pour le changer, ajoutez une clé (`restic key add`) puis retirez l'ancienne
(`restic key remove`) sur chaque dépôt, voir la documentation de restic.

### Sauvegarder maintenant

```bash
docker compose -f deploy/compose.yaml exec backup nid-backup backup-now
```

Une sauvegarde lancée pendant celle de la nuit attend qu'elle se termine.

### Surveiller

`backup` devient `unhealthy` si sa configuration est invalide (par exemple `NID_BACKUP_AT`), si la dernière sauvegarde a échoué, si la dernière réussite date de plus de 26 h
(`NID_BACKUP_MAX_AGE`) ou si le dépôt distant n'a pas reçu la dernière. La commande suivante dit pourquoi :

```bash
docker compose -f deploy/compose.yaml exec backup nid-backup status
```

`backup` écrit aussi son état (`status.json`) dans le volume `backup-state`, que `nid` lit en lecture seule
(`Nid__Backup__StatusPath=/run/backup-state/status.json`, `Nid__Backup__MaxAgeHours`, repris de `NID_BACKUP_MAX_AGE`, 26 par
défaut). Quand la règle ci-dessus est violée, ou que le fichier est illisible, les admins reçoivent la notification
`backup_failed` (centre, email et Discord selon leurs réglages ; Discord du foyer s'il est configuré). Sans fichier,
aucune alerte : sur une installation existante, il n'apparaît qu'au prochain démarrage de `backup`. Le compose autonome n'a
pas de service `backup` et ne change pas. Vous pouvez aussi surveiller l'état depuis l'hôte, par exemple dans un cron ou
dans votre outil de supervision :

```bash
[ "$(docker inspect --format '{{.State.Health.Status}}' nid-backup-1)" = healthy ] || echo "Sauvegarde de Nid en échec"
```

### Ajouter un dépôt distant

Pour la règle 3-2-1, ajoutez une copie hors du domicile. Dans `deploy/backup-secrets/` :

| Fichier | Contenu |
|---|---|
| `remote_repository` | L'URL restic du dépôt, par exemple `s3:https://s3.example.org/nid-sauvegarde` ou `rest:https://utilisateur:motdepasse@sauvegarde.example.org/nid` (un `rest-server`) |
| `remote_password` | Facultatif : le mot de passe du dépôt distant. Sans lui, celui du dépôt local |
| `remote_credentials` | Pour S3 : un fichier au format AWS (`[default]`, `aws_access_key_id = …`, `aws_secret_access_key = …`) |

Puis ajoutez la surcouche `-f deploy/compose.backup-remote.yaml` à **toutes** vos commandes compose : elle seule
ouvre à `backup` un accès sortant. Sans elle, le dépôt distant est injoignable et `backup` le signale.

```bash
docker compose -f deploy/compose.yaml -f deploy/compose.backup-remote.yaml up -d
```

Chaque nouvel instantané est recopié (`restic copy`), puis la même rétention s'applique au dépôt distant. **Dépôt en
ajout seulement** (`rest-server --append-only`, verrouillage d'objets S3) : mettez `NID_BACKUP_REMOTE_FORGET=false`
dans `deploy/.env`. Rien n'y est alors jamais supprimé par Nid ; élaguez-le depuis une autre machine, avec des
identifiants qui ont le droit de supprimer.

### Essayer une restauration, chaque trimestre

```bash
deploy/restore.sh --verify                  # depuis le dépôt local
deploy/restore.sh --verify --from remote    # depuis le dépôt distant
```

Pocket ID se pose un verrou d'instance dans sa base : restauré à partir d'un instantané de moins d'une minute, il refuse de
démarrer le temps que le verrou expire. Le compose le relance tout seul (`restart: unless-stopped`) et `restore.sh`
attend jusqu'à cinq minutes.

Le script restaure le dernier instantané (ou `--snapshot <id>`) dans un projet jetable, `nid-restore-test`, sans port
publié ni accès sortant. Il vérifie que chaque table a autant de lignes que dans la sauvegarde, que `nid` est prêt,
que Pocket ID lit sa base et que les clés Data Protection ont été reprises. Puis il supprime tout. Il ne touche pas votre
installation. `--compose-file <chemin absolu>` ajoute un fichier compose à la restauration, par exemple un
`extra_hosts` pour joindre le dépôt distant (comme §1 pour `nid`).

### Après un sinistre

Sur une machine neuve, avec les prérequis du §1 :

1. `git clone` du dépôt de Nid, à la même version ou plus récente que la sauvegarde.
2. Remettez `deploy/backup-secrets/restic_password` (et les fichiers du dépôt distant) ; pour le dépôt local,
   remettez aussi `deploy/backups/`.
3. `deploy/restore.sh` (ou `deploy/restore.sh --from remote`). Il vérifie d'abord que la sauvegarde s'ouvre et que
   le code la connaît, puis remet `deploy/secrets/`, `deploy/.env`, les
   volumes et la base, démarre la pile et fait les mêmes vérifications que `--verify`.
4. Branchez le proxy (§3).

Le script refuse une installation qui contient déjà des données. `--force` la remplace entièrement, après que vous
avez tapé `RESTAURER` ; rien n'est supprimé tant que la sauvegarde n'a pas été ouverte et reconnue. Si la sauvegarde vient d'une version de Nid plus récente que le code, il s'arrête : faites
d'abord `git pull`.

### Les documents

Les fichiers téléversés dans Nid sont rangés selon `Documents__Storage__Kind`, à régler dans `environment` de `nid`
(un `compose.override.yaml` suffit) :

- `filesystem` (par défaut) : dans `Documents__Storage__Path` (`/var/lib/nid/documents`, le volume `documents`). Ce volume
  est dans l'instantané restic et revient avec `restore.sh`.
- `s3` : dans un bucket compatible S3, avec `Documents__Storage__ServiceUrl`, `Documents__Storage__Bucket`,
  `Documents__Storage__AccessKey`, `Documents__Storage__SecretKey`, `Documents__Storage__Region` et
  `Documents__Storage__ForcePathStyle` (`true` pour MinIO et la plupart des stockages auto-hébergés). **Les fichiers ne sont
  pas dans l'instantané restic** : activez le versionnement ou la réplication du bucket, c'est lui qui les protège.

`Documents__MaxSizeBytes` fixe la taille maximale d'un fichier. `Documents__McpMaxBytes` (10 Mo par défaut) fixe la taille
maximale d'un fichier que l'outil MCP `read_document` envoie ; au-delà, il n'envoie que les métadonnées.

## 7. Secrets et rotation

| Fichier | Rôle | Changer |
|---|---|---|
| `postgres_password`, `nid_connection_string` | Accès à la base | Pas par le script. Changez le mot de passe dans Postgres, puis les deux fichiers. |
| `oidc_client_secret` | Secret du client de Nid dans Pocket ID | Dans Pocket ID, ajoutez un secret au client (l'ancien reste valable), passez-le à `setup.sh --oidc-client-secret`, redémarrez `nid`, puis supprimez l'ancien secret dans Pocket ID. |
| `csrf_key` | Jetons anti-CSRF | Supprimez le fichier, relancez `setup.sh`, puis redémarrez `nid`. Les pages ouvertes devront être rechargées. |
| `pocket_id_encryption_key` | Chiffre les clés de signature de Pocket ID dans sa base | **Ne la changez pas à la main : sa base deviendrait illisible.** Pour la renouveler, utilisez la commande `encryption-key-rotate` de Pocket ID (sa documentation), puis remplacez le fichier. |
| `enable_banking_private_key` | Clé privée (PEM) de l'application Enable Banking ; n'existe que si vous avez activé la synchronisation bancaire (§12), `setup.sh` ne la crée pas | Remplacez la clé de l'application dans le portail d'Enable Banking, copiez la nouvelle clé privée dans le fichier, puis recréez `nid` (§12, étape 3). |
| `backup-secrets/restic_password` | Ouvre la sauvegarde ; hors de la sauvegarde | Jamais en supprimant le fichier : `restic key add` puis `restic key remove` (§6). |
| `backup-secrets/remote_repository`, `remote_password`, `remote_credentials` | Dépôt distant | Changez le fichier, puis `backup-now`. |

Les clés Data Protection de Nid sont dans le volume `dataprotection-keys`. **Les perdre déconnecte tout le monde**, et
rend illisibles les secrets que Nid chiffre : webhooks Discord et sessions bancaires (chaque banque reliée est alors à
renouveler dans Réglages › Banques). Elles font partie de la sauvegarde (§6).

## 8. Commandes de secours

La zone admin couvre l'administration courante. `nid admin` sert quand elle ne suffit plus : plus aucun admin ne peut se
connecter, ou Pocket ID a été réinstallé et l'identité (`sub`) d'un adulte a changé. Les commandes passent par les mêmes
règles que l'interface, et chacune est enregistrée : les admins la voient dans Administration › Journal de sécurité,
avec l'origine « Ligne de commande ».

```bash
docker compose -f deploy/compose.yaml exec nid nid admin list-persons
docker compose -f deploy/compose.yaml exec nid nid admin revoke-sessions --person alice@example.org
docker compose -f deploy/compose.yaml exec nid nid admin rebind-subject --person alice@example.org --sub <nouveau sub>
```

- `--person` accepte un identifiant (voir `list-persons`), un email (la casse ne compte pas) ou un nom affiché exact.
  Si plusieurs personnes correspondent, la commande refuse et les liste : relancez avec l'identifiant.
- `rebind-subject` demande de taper `oui` ; `--yes` s'en passe, par exemple dans un script. Sans
  terminal (`exec -T`) et sans `--yes`, elles refusent sans rien changer.
- `rebind-subject` ferme aussi les sessions de l'adulte. Le nouveau `sub` est l'identifiant de la personne dans Pocket ID (un UUID, visible dans la
  page de la personne). Sinon, faites tenter une connexion à l'adulte : Nid la refuse (email déjà rattaché) et note le `sub`
  de ce refus :

  ```bash
  docker compose -f deploy/compose.yaml exec -T postgres psql -U nid -d nid -c \
    "select at, data->>'subject' as sub, data->>'reason' as reason from platform.audit_log where action = 'login_refused' order by seq desc limit 5"
  ```
- **Si l'API ne démarre pas**, lancez la commande dans un conteneur à part, sans `nid` (le point d'entrée de l'image
  est déjà l'hôte) : `docker compose -f deploy/compose.yaml run --rm nid admin list-persons`.
- La CLI n'applique jamais les migrations : si la base n'est pas à jour, elle le dit ; démarrez d'abord `nid`.
- `nid admin --help` liste les commandes.

Avec les comptes locaux (§4.1 ; avec le standalone, `docker compose exec nid nid admin …` depuis son dossier) :

```bash
docker compose exec nid nid admin create-admin --name Alice          # un administrateur et son lien d'accès
docker compose exec nid nid admin reset-password --person Alice      # un lien pour choisir un nouveau mot de passe
docker compose exec nid nid admin unlock --person Alice              # lève le verrouillage après trop d'échecs
docker compose exec nid nid admin grant-admin --person Bob           # donne le rôle admin (revoke-admin le retire)
docker compose exec nid nid admin reset-totp --person Bob            # désactive la validation en deux étapes
```

- Le lien s'affiche sur la sortie de la commande seulement, jamais dans le journal ni dans l'audit. Transmettez-le à
  l'adulte : il est valable 72 heures, une seule fois. `reset-password --revoke-tokens` révoque aussi ses jetons d'accès.
- `revoke-admin` refuse de retirer le rôle au dernier administrateur.

## 9. Frontière de confiance

La confidentialité des éléments privés vaut **entre les deux adultes, pas contre l'hébergeur**. Qui a accès à la base,
aux sauvegardes, aux journaux, à ce dossier `deploy/` ou au shell de l'hôte voit tout
(`../docs/identity-and-access/administration-and-audit.md` §1). Protéger le disque et les sauvegardes est votre
responsabilité.

## 10. Ce qui arrive

- **Jetons d'accès et workers** (étape 4), avec leurs commandes `revoke-tokens`, `create-worker`, `rotate-worker`,
  `revoke-worker`.

## 11. Pocket ID ou Postgres déjà installés ailleurs

Le compose lance son propre Pocket ID et son propre Postgres. Si vous avez déjà l'un ou l'autre, éteignez le service par un
profil dans `deploy/compose.override.yaml` et dites à la sauvegarde où ils sont. Dès que vous passez `-f`, compose ne charge
plus l'override tout seul : écrivez toujours `-f deploy/compose.yaml -f deploy/compose.override.yaml`.

### Un Pocket ID extérieur

```yaml
services:
  pocket-id:        { profiles: [bundled] }
  pocket-id-volume: { profiles: [bundled] }
```

Dans `.env` : `NID_BACKUP_POCKET_ID=external` (par défaut `embedded`). `setup.sh --auth-host` reçoit le nom d'hôte du Pocket
ID existant, et les groupes et le client de Nid (§2) s'y créent comme avant.

**La sauvegarde de Nid ne contient alors rien de Pocket ID**, ni sa base ni sa clé de chiffrement : sauvegardez-les
vous-même (son dossier de données et son `ENCRYPTION_KEY`). Sans elles, plus personne ne peut se connecter. `restore.sh`
ne vérifie plus Pocket ID pour un instantané pris ainsi : c'est l'instantané qui décide, pas le `.env` du moment.
Une valeur inconnue dans `NID_BACKUP_POCKET_ID` laisse le service `backup` `unhealthy` avec la raison.

### Un Postgres extérieur

Le serveur doit être de version 18 ou moins : l'image `nid-backup` embarque le client 18, qui lit les serveurs plus
anciens mais pas plus récents. Créez-y une base `nid` et un rôle `nid` (`LOGIN`, sans droit superuser) dont le mot de
passe est celui de `deploy/secrets/postgres_password`, puis faites pointer `deploy/secrets/nid_connection_string` dessus
(`setup.sh` ne la remplace jamais).

```yaml
services:
  postgres: { profiles: [bundled] }
  nid:
    depends_on: { postgres: { required: false } }
    networks: { db: {} }
  backup:
    depends_on: { postgres: { required: false } }
    networks: [data, db]
  backup-restore:
    networks: [data, db]
networks:
  db: { external: true, name: <réseau du serveur Postgres> }
```

Dans `.env` : `NID_DB_HOST=<nom du serveur sur ce réseau>`. La sauvegarde lit alors cette base.

**`restore.sh` ne sait pas restaurer dans un Postgres extérieur** : il démarre le service `postgres` du compose. À la main,
`nid` arrêté : recréez la base (`DROP DATABASE nid; CREATE DATABASE nid OWNER nid;`), puis
`docker compose -f deploy/compose.yaml -f deploy/compose.override.yaml run --rm --no-deps -T backup-restore restore-files <id>`
et `… restore-database <id>` (l'identifiant vient de `… run --rm --no-deps -T backup-restore snapshot-id latest`), puis `up -d`.
Une sauvegarde faite sur un serveur de version N ne se recharge pas sur un serveur plus ancien que N.

## 12. Synchronisation bancaire (facultative)

Nid peut lire le solde des comptes courants et des livrets du foyer par [Enable Banking](https://enablebanking.com),
prestataire agréé (DSP2, Finlande), en **lecture seule** : Nid ne demande que l'accès aux comptes et n'a aucun appel de
paiement. Chaque nuit (04:30) et sur le bouton « Synchroniser », le solde d'un compte associé à une enveloppe devient une
valorisation à valider dans « À valider », ou une valorisation écrite si la personne l'a choisi pour ce compte. Les données
bancaires passent par Enable Banking ; la session de chaque banque est chiffrée dans la base. Conception :
`../docs/superpowers/specs/2026-10-29-bank-sync-design.md` ; revue de risque : `../docs/security/bank-sync-review.md`.

**Désactivée par défaut**, et sans rien à faire dans le compose : sans clé, Nid dit la synchronisation « non configurée ».
Pour l'activer :

1. Dans le portail d'Enable Banking, créez une application **de production en mode restreint** (gratuit, réservé à l'usage
   personnel : seuls les comptes reliés dans le portail sont lisibles) :
   - adresse de retour (« redirect URL ») : `https://nid.<domaine>/api/v1/banking/callback`, exactement ;
   - générez la paire de clés de l'application et **gardez la clé privée** (fichier `.pem`) hors du portail ;
   - reliez les comptes de chaque adulte que Nid doit lire, comme le portail le demande ;
   - notez l'**identifiant de l'application**.
2. Copiez la clé privée dans `deploy/secrets/enable_banking_private_key` (droits `0644` comme les autres fichiers du
   dossier, qui reste en `0700` ; elle entre ainsi dans la sauvegarde), puis déclarez-la à `nid` dans
   `deploy/compose.override.yaml` :

   ```yaml
   services:
     nid:
       secrets: [{ source: enable_banking_private_key, target: EnableBanking__PrivateKey }]
   secrets:
     enable_banking_private_key: { file: ./secrets/enable_banking_private_key }
   ```

3. Recréez `nid` pour qu'il lise la clé (elle n'est lue qu'au démarrage) :
   `docker compose -f deploy/compose.yaml -f deploy/compose.override.yaml up -d --force-recreate nid`. Dès lors, passez
   toujours les deux fichiers (§11), y compris à la restauration : `deploy/restore.sh --compose-file
   "$PWD/deploy/compose.override.yaml"` (chemin absolu). Une sauvegarde prise avant la clé ne la contient pas : restaurez-la
   sans ce fichier, puis refaites l'étape 2.
4. Dans Nid, Administration › Banque : collez l'identifiant de l'application, puis activez la synchronisation. La page
   rappelle l'adresse de retour à déclarer.
5. Chaque adulte relie **ses** banques dans Réglages › Banques (« Relier une banque » ; l'authentification se fait chez la
   banque, dans le navigateur), puis associe chaque compte à une enveloppe courante, d'épargne ou autre de même devise.

Nid n'a pas besoin d'être joignable depuis Internet : le retour de la banque passe par le navigateur. Seul `nid` sort,
vers `api.enablebanking.com`. Le consentement dure ce que la banque permet (souvent 180 jours) : Nid prévient 14 puis 3 jours
avant la fin, puis à l'expiration ; « Renouveler » le prolonge en gardant les comptes. Administration › Banque montre le
journal des synchronisations (compteurs et codes d'erreur, sans nom de compte ni montant).

**À vérifier avant de compter dessus** (spec §11, encore ouvert) : que vos banques sont listées par Enable Banking, que le
second adulte peut relier ses comptes à la même application, et que les livrets sont exposés. Une banque absente reste
saisie à la main (l'import de relevés OFX et CAMT.053 arrive avec l'étape 5B).

Avec [`standalone/`](standalone/compose.yaml), même chose dans son `compose.override.yaml`, avec le chemin de votre fichier :

```yaml
services:
  nid:
    secrets: [{ source: enable_banking_private_key, target: EnableBanking__PrivateKey }]
secrets:
  enable_banking_private_key: { file: ./enable_banking_private_key.pem }
```

## 13. Journaux

Le service `nid` écrit ses journaux dans la console (`docker compose logs nid`) et dans le volume `logs`, monté sur
`/var/lib/nid/logs` : ils survivent à la mise à jour de l'image. Le volume n'entre pas dans les sauvegardes, et
`restore.sh` le vide. Un fichier `nid-AAAAMMJJ.log` par jour, suivi de `_001`, `_002`… quand il dépasse la taille limite.
Les commandes `nid admin` n'y écrivent pas, et ces fichiers ne contiennent rien de plus que la console.

Lire les dernières lignes, puis copier les fichiers sur l'hôte :

```bash
docker run --rm -v nid_logs:/logs:ro busybox:1.37 sh -c 'tail -n 100 /logs/nid-*.log'
```

```bash
docker run --rm -v nid_logs:/logs:ro -v "$PWD":/out busybox:1.37 cp -r /logs /out/nid-logs
```

Pour changer la rétention, dans `.env` (puis `docker compose up -d nid`) :

| Variable | Défaut | Rôle |
|---|---|---|
| `NID_LOG_MAX_FILE_MB` | `50` | Taille maximale d'un fichier |
| `NID_LOG_KEEP_FILES` | `30` | Nombre de fichiers conservés |
| `NID_LOG_KEEP_DAYS` | `14` | Âge maximal d'un fichier, en jours |

Le premier seuil atteint supprime les fichiers les plus anciens : avec les valeurs par défaut, le volume reste sous 1,5 Go.
Si le dossier n'est pas accessible en écriture, Nid démarre quand même et le signale dans la console.

## 14. Une démo publique

Le mode démo fait tourner Nid sans fournisseur d'identité, avec le foyer inventé de `local/demo/` (Alice, Bob et Léa).
Il est fait pour une instance publique jetable, **jamais pour un vrai foyer** : n'importe qui peut y entrer, sans mot
de passe.

- `Nid__Demo__Enabled=true` l'active ; il est désactivé partout ailleurs.
- `/auth/login` mène à la page `/demo`, qui propose « Entrer en tant qu'Alice » (qui administre Nid) ou « Bob ». Pas de
  Pocket ID, et aucune variable `Auth__Authority`, `Auth__ClientId` ou `Auth__ClientSecret`.
- Au démarrage, une base vide est migrée puis remplie avec le foyer de démo, ses dates décalées pour finir le mois
  dernier. Une base qui contient déjà ce foyer est laissée telle quelle ; une base qui contient **un autre adulte**
  empêche Nid de démarrer.
- Tout ce qui sortirait de la démo est coupé : synchronisation bancaire (les réglages `EnableBanking__…` sont ignorés),
  emails (`Nid__Smtp__…` ignorés), jetons d'accès pour un assistant, webhooks Discord et messages de test.
- Chaque écran affiche un bandeau « Démo : les données sont inventées et réinitialisées chaque nuit ».
- Les fichiers d'import ne contiennent pas tout : `NID_URL=https://<domaine de la démo> node local/demo/finish.mjs`
  valide les mois de garde, ajoute les documents, les avis d'impôt, les scénarios et l'indice des prix, connecté en
  Alice. Le relancer ne change rien.

Les variables restantes sont celles de toute installation :

```bash
docker run -d --name nid-demo -p 8080:8080 \
  -e Nid__Demo__Enabled=true \
  -e Auth__PublicUrl=https://demo.example.org \
  -e Auth__CsrfKey="$(openssl rand -hex 32)" \
  -e ConnectionStrings__nid="Host=…;Database=nid;Username=nid;Password=…" \
  ghcr.io/mistribe/nid:latest
```

Pour une démo qui repart de zéro, donnez-lui un Postgres **sans volume persistant**, dans le même groupe de conteneurs :
redémarrer les deux (par exemple chaque nuit) donne une base vide, que Nid remplit de nouveau. Le reverse proxy TLS reste
nécessaire, comme pour toute installation : le cookie de session ne circule qu'en HTTPS.
