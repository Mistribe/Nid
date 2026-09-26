# Déployer Nid

Ce dossier contient le **compose de référence** de Nid : la seule forme livrée (`../docs/architecture.md` §10). Il lance
cinq services : `postgres`, `authelia` (le fournisseur d'identité), `nid-api` (l'API), `nid-web` (l'interface, qui
relaie aussi l'API sur la même origine) et `backup` (la sauvegarde chiffrée, §6).

## 1. Prérequis

- Docker avec compose v2, `openssl` et `bash` sur l'hôte.
- **Deux noms d'hôte sous un même domaine parent**, par exemple `nid.example.org` et `auth.example.org` : le cookie
  d'Authelia couvre ce domaine parent.
- **Un reverse proxy TLS** fourni par l'hébergeur (Caddy, Traefik, nginx…). Nid ne termine pas le TLS lui-même.
- Un accès sortant HTTPS pour `nid-api` (catalogue de fournisseurs et taux de change). Sans lui, Nid fonctionne, mais
  les fournisseurs et les taux se saisissent à la main. `nid-api` est **le seul service qui sort** : tous les autres
  réseaux du compose sont internes.
- **`nid-api` doit joindre `https://auth.<domaine>` depuis l'intérieur.** Pour la connexion, il appelle Authelia à
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

```bash
deploy/setup.sh
docker compose -f deploy/compose.yaml up -d --build
```

`setup.sh` demande les deux noms d'hôte et le premier utilisateur (identifiant, nom affiché, email, mot de passe). Il
génère :

- tous les secrets, dans `deploy/secrets/` : un fichier par secret, dans un dossier fermé (`0700`), jamais versionné ;
- la configuration d'Authelia (`deploy/authelia/configuration.yml`, `users.yml`) ;
- le fichier `deploy/.env` ;
- le mot de passe de la sauvegarde, `deploy/backup-secrets/restic_password`, et le dossier `deploy/backups/`. Le script
  affiche ce mot de passe une fois : **notez-le hors de cette machine** (§6).

Vous pouvez le relancer : **il ne remplace jamais un secret existant**. Il réécrit seulement la configuration qui
découle de vos réponses.

## 3. Brancher le proxy

Le compose ne publie aucun port. Votre proxy rejoint deux cibles sur le réseau Docker `nid_edge` (le préfixe `nid_`
vient du nom du projet compose ; ce réseau est interne, votre proxy garde ses propres réseaux pour l'extérieur) :

| Nom public | Cible |
|---|---|
| `nid.<domaine>` | `nid-web:8080` (HTTP) |
| `auth.<domaine>` | `authelia:9091` (HTTP) |

Le proxy doit **fixer lui-même** `X-Forwarded-Proto`, `X-Forwarded-Host` et `X-Forwarded-For`, en écrasant ce que le
client envoie. Caddy et Traefik le font par défaut ; nginx non (voir l'exemple ci-dessous). Le routage de `/api`,
`/auth`, `/mcp` et `/health` vers l'API est fait par `nid-web` : le proxy n'a rien à router par préfixe.

Exemple avec Caddy, dans un conteneur rattaché au réseau `nid_edge` :

```
nid.example.org {
	reverse_proxy nid-web:8080
}
auth.example.org {
	reverse_proxy authelia:9091
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
    auth: { loadBalancer: { servers: [{ url: "http://authelia:9091" }] } }
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
  authelia:
    ports: ["127.0.0.1:9091:9091"]
    networks: [edge, published]
networks:
  published: {}
```

Avec nginx sur l'hôte, pour `nid.example.org` (même chose pour Authelia sur le port 9091) :

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
`nid.localtest.me` et `auth.localtest.me`. `deploy/smoke.sh` fait l'essai complet (préparation, démarrage, parcours
Playwright) ; ce script est jetable, ne le lancez pas sur une installation réelle.

## 4. Premier utilisateur et adultes suivants

- **Premier adulte** : la première personne qui se connecte devient adulte et admin.
- **Adulte suivant** :
  1. ajoutez-le dans Authelia, en complétant `deploy/authelia/users.yml`. Hachez son mot de passe avec
     `docker run --rm authelia/authelia:4.39 authelia crypto hash generate argon2 --password '…'`. Puis redémarrez
     avec `docker compose -f deploy/compose.yaml restart authelia` ;
  2. pré-enregistrez-le avec le même email dans Nid, dans Administration › Utilisateurs.

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

- la base (`pg_dump`), la base d'Authelia (copie à chaud), les clés Data Protection et les icônes des fournisseurs ;
- `deploy/secrets/`, la configuration d'Authelia (`configuration.yml`, `users.yml`) et `deploy/.env` ;
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

Le script restaure le dernier instantané (ou `--snapshot <id>`) dans un projet jetable, `nid-restore-test`, sans port
publié ni accès sortant. Il vérifie que chaque table a autant de lignes que dans la sauvegarde, que `nid-api` est prêt,
qu'Authelia lit sa base et que les clés Data Protection ont été reprises. Puis il supprime tout. Il ne touche pas votre
installation. `--compose-file <chemin absolu>` ajoute un fichier compose à la restauration, par exemple un
`extra_hosts` pour joindre le dépôt distant (comme §1 pour `nid-api`).

### Après un sinistre

Sur une machine neuve, avec les prérequis du §1 :

1. `git clone` du dépôt de Nid, à la même version ou plus récente que la sauvegarde.
2. Remettez `deploy/backup-secrets/restic_password` (et les fichiers du dépôt distant) ; pour le dépôt local,
   remettez aussi `deploy/backups/`.
3. `deploy/restore.sh` (ou `deploy/restore.sh --from remote`). Il vérifie d'abord que la sauvegarde s'ouvre et que
   le code la connaît, puis remet `deploy/secrets/`, la configuration, les
   volumes et la base, démarre la pile et fait les mêmes vérifications que `--verify`.
4. Branchez le proxy (§3).

Le script refuse une installation qui contient déjà des données. `--force` la remplace entièrement, après que vous
avez tapé `RESTAURER` ; rien n'est supprimé tant que la sauvegarde n'a pas été ouverte et reconnue. Si la sauvegarde vient d'une version de Nid plus récente que le code, il s'arrête : faites
d'abord `git pull`.

## 7. Secrets et rotation

| Fichier | Rôle | Changer |
|---|---|---|
| `postgres_password`, `nid_connection_string` | Accès à la base | Pas par le script. Changez le mot de passe dans Postgres, puis les deux fichiers. |
| `oidc_client_secret` | Secret du client `nid` chez Authelia | Supprimez le fichier, relancez `setup.sh`, puis redémarrez `authelia` et `nid-api`. |
| `csrf_key` | Jetons anti-CSRF | Supprimez le fichier, relancez `setup.sh`, puis redémarrez `nid-api`. Les pages ouvertes devront être rechargées. |
| `authelia_session`, `authelia_storage`, `authelia_jwt`, `authelia_hmac`, `authelia_jwks` | Secrets d'Authelia | Voir la documentation d'Authelia. **Changer `authelia_storage` rend sa base illisible.** |
| `backup-secrets/restic_password` | Ouvre la sauvegarde ; hors de la sauvegarde | Jamais en supprimant le fichier : `restic key add` puis `restic key remove` (§6). |
| `backup-secrets/remote_repository`, `remote_password`, `remote_credentials` | Dépôt distant | Changez le fichier, puis `backup-now`. |

Les clés Data Protection de Nid sont dans le volume `dataprotection-keys`. **Les perdre déconnecte tout le monde**, et
rendra illisibles les secrets que Nid chiffrera plus tard. Elles font partie de la sauvegarde (§6).

## 8. Commandes de secours

La zone admin couvre l'administration courante. `nid admin` sert quand elle ne suffit plus : plus aucun admin ne peut se
connecter, ou Authelia a été réinstallé et l'identité (`sub`) d'un adulte a changé. Les commandes passent par les mêmes
règles que l'interface, et chacune est enregistrée : les admins la voient dans Administration › Journal de sécurité,
avec l'origine « Ligne de commande ».

```bash
docker compose -f deploy/compose.yaml exec nid-api nid admin list-persons
docker compose -f deploy/compose.yaml exec nid-api nid admin grant-admin --person alice@example.org
docker compose -f deploy/compose.yaml exec nid-api nid admin revoke-sessions --person alice@example.org
docker compose -f deploy/compose.yaml exec nid-api nid admin rebind-subject --person alice@example.org --sub <nouveau sub>
```

- `--person` accepte un identifiant (voir `list-persons`), un email (la casse ne compte pas) ou un nom affiché exact.
  Si plusieurs personnes correspondent, la commande refuse et les liste : relancez avec l'identifiant.
- `grant-admin` et `rebind-subject` demandent de taper `oui` ; `--yes` s'en passe, par exemple dans un script. Sans
  terminal (`exec -T`) et sans `--yes`, elles refusent sans rien changer.
- `rebind-subject` ferme aussi les sessions de l'adulte. Le nouveau `sub` se lit dans Authelia (l'identifiant de
  l'utilisateur, visible par exemple dans `deploy/authelia/users.yml` ou dans les journaux d'Authelia à sa connexion
  refusée).
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
