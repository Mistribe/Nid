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
  les fournisseurs et les taux se saisissent à la main.

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

Le compose ne publie aucun port. Votre proxy rejoint deux cibles sur le réseau Docker `nid_edge` :

| Nom public | Cible |
|---|---|
| `nid.<domaine>` | `nid-web:8080` (HTTP) |
| `auth.<domaine>` | `authelia:9091` (HTTP) |

Le proxy doit transmettre `X-Forwarded-Proto` et `X-Forwarded-Host`. Le routage de `/api`, `/auth`, `/mcp` et
`/health` vers l'API est fait par `nid-web` : le proxy n'a rien à router par préfixe.

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
