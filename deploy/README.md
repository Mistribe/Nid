# Déployer Nid

Ce dossier contient le **compose de référence** de Nid : la seule forme livrée (`../docs/architecture.md` §10). Il lance
cinq services : `postgres`, `pocket-id` (le fournisseur d'identité), `nid-api` (l'API), `nid-web` (l'interface, qui
relaie aussi l'API sur la même origine) et `backup` (la sauvegarde chiffrée, §6).

## 1. Prérequis

- Docker avec compose v2, `openssl` et `bash` sur l'hôte.
- **Deux noms d'hôte**, par exemple `nid.example.org` et `auth.example.org`.
- **Des passkeys** : Pocket ID n'authentifie que par passkey (empreinte, clé de sécurité, gestionnaire de mots de passe). Chaque
  adulte doit pouvoir en créer une, sur un appareil qui voit `https://auth.<domaine>`.
- **Un reverse proxy TLS** fourni par l'hébergeur (Caddy, Traefik, nginx…). Nid ne termine pas le TLS lui-même.
- Un accès sortant HTTPS pour `nid-api` (catalogue de fournisseurs et taux de change). Sans lui, Nid fonctionne, mais
  les fournisseurs et les taux se saisissent à la main. `nid-api` est **le seul service qui sort** : tous les autres
  réseaux du compose sont internes.
- **`nid-api` doit joindre `https://auth.<domaine>` depuis l'intérieur.** Pour la connexion, il appelle Pocket ID à
  son adresse publique (découverte, jeton, profil). Si votre routeur ne sait pas renvoyer vers lui-même le trafic
  destiné à votre adresse publique (« NAT loopback »), ou si votre DNS diffère à l'intérieur, la connexion échoue.
  Dans ce cas, faites résoudre ce nom vers votre proxy depuis `nid-api`, par exemple avec un fichier
  `deploy/compose.override.yaml` :

  ```yaml
  services:
    nid-api:
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
définissez `Auth__AccessGroup` et `Auth__AdminGroup` pour `nid-api` dans un `deploy/compose.override.yaml`.

## 3. Brancher le proxy

Le compose ne publie aucun port. Votre proxy rejoint deux cibles sur le réseau Docker `nid_edge` (le préfixe `nid_`
vient du nom du projet compose ; ce réseau est interne, votre proxy garde ses propres réseaux pour l'extérieur) :

| Nom public | Cible |
|---|---|
| `nid.<domaine>` | `nid-web:8080` (HTTP) |
| `auth.<domaine>` | `pocket-id:1411` (HTTP) |

Le proxy doit **fixer lui-même** `X-Forwarded-Proto`, `X-Forwarded-Host` et `X-Forwarded-For`, en écrasant ce que le
client envoie. Caddy et Traefik le font par défaut ; nginx non (voir l'exemple ci-dessous). Le routage de `/api`,
`/auth`, `/mcp` et `/health` vers l'API est fait par `nid-web` : le proxy n'a rien à router par préfixe.

Exemple avec Caddy, dans un conteneur rattaché au réseau `nid_edge` :

```
nid.example.org {
	reverse_proxy nid-web:8080
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
    nid: { loadBalancer: { servers: [{ url: "http://nid-web:8080" }] } }
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
  nid-web:
    ports: ["127.0.0.1:8080:8080"]
    networks: [edge, app, published]
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

Le sous-réseau interne `172.30.10.0/24` est fixé par le compose : s'il chevauche un réseau que vous utilisez déjà,
`docker compose up` refuse de démarrer avec « Pool overlaps ».

**Essai sur un poste**, sans proxy : `deploy/compose.local.yaml` ajoute un Caddy à certificats locaux sur
`nid.localtest.me` et `auth.localtest.me`. `deploy/smoke.sh` fait l'essai complet (préparation, démarrage, utilisateurs et client de Pocket ID par son API,
parcours Playwright) ; ce script est jetable, ne le lancez pas sur une installation réelle.

## 4. Qui entre dans Nid, et qui l'administre

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
3. `docker compose -f deploy/compose.yaml up -d --build`.

Les migrations de la base s'appliquent au démarrage de `nid-api`. Les images de base sont épinglées par empreinte ; une
mise à jour de Nid les fait évoluer.

## 6. Sauvegarder et restaurer

### Ce qui est sauvegardé

Chaque nuit à 03:30 (réglable : `NID_BACKUP_AT` et `TZ` dans `deploy/.env`), le service `backup` fait **un instantané
restic chiffré** qui contient tout ce qu'il faut pour repartir :

- la base (`pg_dump`), la base de Pocket ID (copie à chaud de sa base SQLite, et ses images ; pas avec un Pocket ID
  extérieur, §11), les clés Data Protection et les icônes des fournisseurs ;
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

Nid n'envoie pas encore d'alerte par email. Surveillez cet état depuis l'hôte, par exemple dans un cron ou dans votre
outil de supervision :

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
publié ni accès sortant. Il vérifie que chaque table a autant de lignes que dans la sauvegarde, que `nid-api` est prêt,
que Pocket ID lit sa base et que les clés Data Protection ont été reprises. Puis il supprime tout. Il ne touche pas votre
installation. `--compose-file <chemin absolu>` ajoute un fichier compose à la restauration, par exemple un
`extra_hosts` pour joindre le dépôt distant (comme §1 pour `nid-api`).

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

## 7. Secrets et rotation

| Fichier | Rôle | Changer |
|---|---|---|
| `postgres_password`, `nid_connection_string` | Accès à la base | Pas par le script. Changez le mot de passe dans Postgres, puis les deux fichiers. |
| `oidc_client_secret` | Secret du client de Nid dans Pocket ID | Dans Pocket ID, ajoutez un secret au client (l'ancien reste valable), passez-le à `setup.sh --oidc-client-secret`, redémarrez `nid-api`, puis supprimez l'ancien secret dans Pocket ID. |
| `csrf_key` | Jetons anti-CSRF | Supprimez le fichier, relancez `setup.sh`, puis redémarrez `nid-api`. Les pages ouvertes devront être rechargées. |
| `pocket_id_encryption_key` | Chiffre les clés de signature de Pocket ID dans sa base | **Ne la changez pas à la main : sa base deviendrait illisible.** Pour la renouveler, utilisez la commande `encryption-key-rotate` de Pocket ID (sa documentation), puis remplacez le fichier. |
| `backup-secrets/restic_password` | Ouvre la sauvegarde ; hors de la sauvegarde | Jamais en supprimant le fichier : `restic key add` puis `restic key remove` (§6). |
| `backup-secrets/remote_repository`, `remote_password`, `remote_credentials` | Dépôt distant | Changez le fichier, puis `backup-now`. |

Les clés Data Protection de Nid sont dans le volume `dataprotection-keys`. **Les perdre déconnecte tout le monde**, et
rendra illisibles les secrets que Nid chiffrera plus tard. Elles font partie de la sauvegarde (§6).

## 8. Commandes de secours

La zone admin couvre l'administration courante. `nid admin` sert quand elle ne suffit plus : plus aucun admin ne peut se
connecter, ou Pocket ID a été réinstallé et l'identité (`sub`) d'un adulte a changé. Les commandes passent par les mêmes
règles que l'interface, et chacune est enregistrée : les admins la voient dans Administration › Journal de sécurité,
avec l'origine « Ligne de commande ».

```bash
docker compose -f deploy/compose.yaml exec nid-api nid admin list-persons
docker compose -f deploy/compose.yaml exec nid-api nid admin revoke-sessions --person alice@example.org
docker compose -f deploy/compose.yaml exec nid-api nid admin rebind-subject --person alice@example.org --sub <nouveau sub>
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
  est déjà l'hôte) : `docker compose -f deploy/compose.yaml run --rm nid-api admin list-persons`.
- La CLI n'applique jamais les migrations : si la base n'est pas à jour, elle le dit ; démarrez d'abord `nid-api`.
- `nid admin --help` liste les commandes.

## 9. Frontière de confiance

La confidentialité des éléments privés vaut **entre les deux adultes, pas contre l'hébergeur**. Qui a accès à la base,
aux sauvegardes, aux journaux, à ce dossier `deploy/` ou au shell de l'hôte voit tout
(`../docs/identity-and-access/administration-and-audit.md` §1). Protéger le disque et les sauvegardes est votre
responsabilité.

## 10. Ce qui arrive

- **Alerte par email** si la sauvegarde échoue ou vieillit.
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
  nid-api:
    depends_on: { postgres: { required: false } }
    networks: [app, data, egress, db]
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
`nid-api` arrêté : recréez la base (`DROP DATABASE nid; CREATE DATABASE nid OWNER nid;`), puis
`docker compose -f deploy/compose.yaml -f deploy/compose.override.yaml run --rm --no-deps -T backup-restore restore-files <id>`
et `… restore-database <id>` (l'identifiant vient de `… run --rm --no-deps -T backup-restore snapshot-id latest`), puis `up -d`.
Une sauvegarde faite sur un serveur de version N ne se recharge pas sur un serveur plus ancien que N.
