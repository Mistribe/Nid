# Authentification du navigateur

> Statut : brouillon. Connexion des deux adultes via Authelia, session côté serveur (BFF).
> Références : `README.md` (principes), `../core-data-model.md` §4.4 (provisionnement), `../architecture.md` §7 (API) et §10.2 (hypothèses envers l'hébergeur).

---

## 1. Vue d'ensemble

```
Navigateur ──cookie de session──> nid-api (BFF) ──OIDC (code + PKCE)──> Authelia
```

- Le SPA ne voit jamais de jeton : `nid-api` mène le flux OIDC côté serveur et ne remet au navigateur qu'un **cookie de session**.
- On ne consomme que l'**ID token** (identité). Aucun appel d'API en aval avec un jeton d'accès, aucun refresh token en v1.

## 2. Client OIDC dans Authelia

Configuration fournie **à titre d'exemple** dans `deploy/` ; le secret vient d'un secret Docker.

| Paramètre | Valeur |
|---|---|
| Type | Client **confidentiel** (`public: false`) |
| Authentification au endpoint de jetons | **`client_secret_post`** : le handler ASP.NET envoie le secret dans le corps, `client_secret_basic` échoue en `invalid_client` (`../spikes.md` §8) |
| PKCE | `require_pkce: true`, `pkce_challenge_method: S256` |
| Grant / réponse | `authorization_code` / `code` |
| Scopes | `openid`, `profile`, `email` |
| `redirect_uris` | `{PUBLIC_URL}/auth/callback` |
| Politique d'autorisation | Choix de l'hébergeur ; l'exemple recommande `two_factor` |

- Côté ASP.NET : handler OpenID Connect + cookie, `SaveTokens = false`, `MapInboundClaims = false`, `UsePkce = true`, **`PushedAuthorizationBehavior = Disable`** (Authelia annonce PAR et le handler l'utiliserait, avec la même erreur d'authentification du client). Le retour se fait en `form_post` par défaut : `POST` inter-sites, sans effet sur le cookie de session `Lax`. Vérifié de bout en bout (`../spikes.md` §8).
- **Par défaut, `profile` et `email` sont servis par l'endpoint `userinfo`, pas par l'ID token** (documentation d'Authelia). Le handler OIDC est donc configuré avec `GetClaimsFromUserInfoEndpoint = true`. Alternative : une `claims_policy` d'Authelia avec `id_token: [...]` (`name`, `email`, `preferred_username`) assignée au client. L'`email` est optionnel : son absence n'empêche pas la connexion.

## 3. Session

| Sujet | Décision |
|---|---|
| Mécanisme | Cookie d'authentification ASP.NET, **sans session stockée en base** |
| Contenu | `person_id`, `sub`, `session_epoch`. Chiffré avec les clés Data Protection (volume sauvegardé) |
| Attributs | Nom préfixé `__Host-`, `Secure`, `HttpOnly`, `SameSite=Lax`, `Path=/`, sans `Domain` |
| Durée | Glissante 7 jours, **absolue 30 jours** (configurables) |
| Révocation | `person.session_epoch` : « déconnecter partout » l'incrémente ; tout cookie portant un ancien epoch est refusé. Lecture en cache court (deux utilisateurs) |
| CSRF | En-tête personnalisé obligatoire sur les méthodes non sûres, plus contrôle de `Origin` et de `Sec-Fetch-Site`. Le jeton est fourni par `GET /auth/session`. Aucune configuration CORS (même origine) |

Le préfixe `__Host-` impose `Secure` : le HTTPS est terminé en amont (hypothèse §7) et l'application fait confiance aux en-têtes `X-Forwarded-*` du proxy pour le savoir.

## 4. Routes (`nid-api`)

| Route | Rôle |
|---|---|
| `GET /auth/login?returnUrl=` | Lance l'authentification. `returnUrl` doit être un **chemin relatif** ; toute URL absolue est refusée (redirection ouverte) |
| `GET /auth/callback` | Fin du flux OIDC, création de la session |
| `POST /auth/logout` | Supprime le cookie de Nid (voir §6) |
| `GET /auth/session` | `{ authenticated, person: { id, name, kind }, csrf, preferences, session_epoch }` (`preferences` : thème, mode, motifs des graphiques) |

Une API non authentifiée répond `401` **sans redirection HTML** : le SPA relance lui-même le login.

## 5. Provisionnement et récupération

- **Première connexion** : Core crée la `person` de type `adult` (`../core-data-model.md` §4.4). Une fois deux adultes créés, toute nouvelle identité est refusée avec un message clair (événement `login_refused`).
- **Récupération** : la `person` est retrouvée par `oidc_subject`. Si le `sub` change (Authelia réinstallé, utilisateur recréé), le nouvel `sub` serait refusé par le plafond de deux. La commande d'administration `rebind-subject` (`administration-and-audit.md`) rattache la personne existante au nouvel `sub`. Sans elle, l'accès serait perdu.
- **Pas de rôles** : les deux adultes ont les mêmes droits ; aucun claim de groupe n'est requis. Qui peut atteindre Nid se règle dans Authelia.

## 6. Limites d'Authelia à assumer

D'après la documentation d'Authelia :

- **Pas de déconnexion standard** : ni RP-initiated, ni front-channel, ni back-channel. `POST /auth/logout` ne supprime que le cookie de Nid ; la session Authelia reste ouverte, donc « se reconnecter » réussit sans mot de passe tant qu'elle dure. L'interface le signale et propose le lien vers le portail Authelia pour fermer la session SSO.
- **Pas de révocation poussée** : un compte désactivé dans Authelia garde sa session Nid jusqu'à l'expiration du cookie (30 jours au plus) ou jusqu'à « déconnecter partout ».
- **Pas d'enregistrement dynamique de clients** (RFC 7591) : voir `tokens-and-credentials.md` §5 pour ses conséquences sur le MCP.

## 7. Hypothèses envers l'hébergeur

- **Une seule origine** : le proxy route `/api`, `/auth`, `/mcp` et `/health` vers `nid-api`, et le reste vers `nid-web`, sur le même nom d'hôte. Le cookie de session en dépend, et cela évite toute configuration CORS.
- HTTPS terminé en amont, `X-Forwarded-*` de confiance, `PUBLIC_URL` correct (redirections OIDC, cookie, liens des emails).
- Authelia joignable depuis `nid-api` (découverte OIDC au démarrage, `/health/ready`).

## 8. Points à vérifier à l'intégration

- Le montage de développement : Authelia n'accepte que le HTTPS et un domaine de cookie qui n'est pas `localhost` ; la pile de développement (Aspire, `architecture.md` §10.3) inclut donc un proxy TLS et un domaine à point (`*.localtest.me`). Le flux a été exécuté ainsi (`../spikes.md` §8).
- Comportement du plafond d'adultes lors d'une première connexion simultanée des deux adultes (création atomique de la `person`).
