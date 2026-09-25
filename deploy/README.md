# Déployer Nid

Ce dossier contient le **compose de référence** de Nid : la seule forme livrée (`../docs/architecture.md` §10). Il lance
quatre services : `postgres`, `authelia` (le fournisseur d'identité), `nid-api` (l'API) et `nid-web` (l'interface, qui
relaie aussi l'API sur la même origine).

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
- le fichier `deploy/.env`.

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

1. **Sauvegardez** (la sauvegarde automatique arrive avec 1F2).
2. `git pull`.
3. `docker compose -f deploy/compose.yaml up -d --build`.

Les migrations de la base s'appliquent au démarrage de `nid-api`. Les images de base sont épinglées par empreinte ; une
mise à jour de Nid les fait évoluer.

## 6. Secrets et rotation

| Fichier | Rôle | Changer |
|---|---|---|
| `postgres_password`, `nid_connection_string` | Accès à la base | Pas par le script. Changez le mot de passe dans Postgres, puis les deux fichiers. |
| `oidc_client_secret` | Secret du client `nid` chez Authelia | Supprimez le fichier, relancez `setup.sh`, puis redémarrez `authelia` et `nid-api`. |
| `csrf_key` | Jetons anti-CSRF | Supprimez le fichier, relancez `setup.sh`, puis redémarrez `nid-api`. Les pages ouvertes devront être rechargées. |
| `authelia_session`, `authelia_storage`, `authelia_jwt`, `authelia_hmac`, `authelia_jwks` | Secrets d'Authelia | Voir la documentation d'Authelia. **Changer `authelia_storage` rend sa base illisible.** |

Les clés Data Protection de Nid sont dans le volume `dataprotection-keys`. **Les perdre déconnecte tout le monde**, et
rendra illisibles les secrets que Nid chiffrera plus tard. Elles font partie de la sauvegarde.

## 7. Frontière de confiance

La confidentialité des éléments privés vaut **entre les deux adultes, pas contre l'hébergeur**. Qui a accès à la base,
aux sauvegardes, aux journaux, à ce dossier `deploy/` ou au shell de l'hôte voit tout
(`../docs/identity-and-access/administration-and-audit.md` §1). Protéger le disque et les sauvegardes est votre
responsabilité.

## 8. Ce qui arrive

- **Sauvegarde et restauration testée** (1F2) : restic, dump de la base, volumes `authelia`, `dataprotection-keys` et
  `assets`.
- **Journal d'audit** (1F3).
- **Commande de secours `nid admin`** (1F4), par exemple pour rendre un adulte admin si plus aucun admin n'est
  joignable.
