# Authentification du navigateur

> Statut : brouillon. Connexion des adultes via Pocket ID, session côté serveur (BFF), accès et rôle admin par les groupes du fournisseur d'identité, provisionnement des adultes.
> Références : `README.md` (principes), `../core-data-model.md` §4.4 (provisionnement), `../architecture.md` §7 (API) et §10.2 (hypothèses envers l'hébergeur), `../spikes.md` §9septies (le flux exécuté).

---

## 1. Vue d'ensemble

```
Navigateur ──cookie de session──> nid-api (BFF) ──OIDC (code + PKCE)──> Pocket ID
```

- Le SPA ne voit jamais de jeton : `nid-api` mène le flux OIDC côté serveur et ne remet au navigateur qu'un **cookie de session**.
- On ne consomme que l'**ID token** (identité et groupes). Aucun appel d'API en aval avec un jeton d'accès, aucun refresh token en v1.
- **Pocket ID décide** qui entre (groupe d'accès) et qui administre (groupe admin) ; Nid applique ce que le jeton de chaque connexion dit (§5).

## 2. Client OIDC dans Pocket ID

Créé par l'hébergeur dans Pocket ID (`deploy/README.md` §2) ; son secret vient d'un secret Docker.

| Paramètre | Valeur |
|---|---|
| Type | Client **confidentiel** (`isPublic: false`) |
| Identifiant | `nid` par défaut (`NID_OIDC_CLIENT_ID`) |
| Authentification au endpoint de jetons | `client_secret_post` (et `client_secret_basic`) sont acceptés ; le handler ASP.NET envoie le secret dans le corps |
| PKCE | activé, `S256` |
| Grant / réponse | `authorization_code` / `code` |
| Scopes | `openid`, `profile`, `email`, **`groups`** |
| Redirections | `{PUBLIC_URL}/auth/callback` ; déconnexion `{PUBLIC_URL}/signed-out` |
| Consentement | Ignoré (« Skip consent ») : le client est celui du foyer |
| Accès | **Réservé au groupe d'accès** (`nid-users`) : hors de ce groupe, Pocket ID refuse avant d'émettre un code |

- Côté ASP.NET : handler OpenID Connect + cookie, `SaveTokens = false`, `MapInboundClaims = false`, `UsePkce = true`, les réglages par défaut du handler pour le reste (Pocket ID annonce PAR, que le handler utilise sans difficulté). Le retour se fait en `form_post` par défaut : `POST` inter-sites, sans effet sur le cookie de session `Lax`. Vérifié de bout en bout (`../spikes.md` §9septies).
- **`name`, `email`, `preferred_username` et `groups` sont dans l'ID token** (et dans `userinfo`). `groups` est un tableau de noms de groupes ; le handler en fait un claim `groups` par valeur (`FindAll("groups")`). L'`email` est optionnel : son absence n'empêche pas la connexion.
- Noms de groupe : `nid-users` et `nid-admins` par défaut, réglables (`Auth__AccessGroup`, `Auth__AdminGroup`).

## 3. Session

| Sujet | Décision |
|---|---|
| Mécanisme | Cookie d'authentification ASP.NET, **sans session stockée en base** |
| Contenu | `person_id`, `sub`, `session_epoch`. Chiffré avec les clés Data Protection (volume sauvegardé) |
| Attributs | Nom préfixé `__Host-`, `Secure`, `HttpOnly`, `SameSite=Lax`, `Path=/`, sans `Domain` |
| Durée | Glissante 7 jours, **absolue 30 jours** (configurables) |
| Révocation | `person.session_epoch` : « déconnecter partout » (page Sécurité, ou un admin pour un autre adulte) l'incrémente ; tout cookie portant un ancien epoch est refusé. C'est `ActorFilter`, pas ce validateur, qui relit la `person` au point d'entrée HTTP et fournit `is_admin` à l'acteur (`authorization.md` §1) : un rôle admin retiré prend effet à la requête suivante, dès que la base le sait (§5) |
| CSRF | En-tête personnalisé obligatoire sur les méthodes non sûres, plus contrôle de `Origin` et de `Sec-Fetch-Site`. Le jeton est fourni par `GET /auth/session`. Aucune configuration CORS (même origine) |

Le préfixe `__Host-` impose `Secure` : le HTTPS est terminé en amont (hypothèse §7) et l'application fait confiance aux en-têtes `X-Forwarded-*` du proxy pour le savoir.

## 4. Routes (`nid-api`)

| Route | Rôle |
|---|---|
| `GET /auth/login?returnUrl=` | Lance l'authentification. `returnUrl` doit être un **chemin relatif** ; toute URL absolue est refusée (redirection ouverte) |
| `GET /auth/callback` | Fin du flux OIDC, création de la session |
| `POST /auth/logout` | Supprime le cookie de Nid (voir §6) |
| `GET /auth/session` | `{ authenticated, person: { id, name, kind }, csrf, preferences, sessionEpoch, identityProvider }` (`preferences` : thème, mode, motifs des graphiques ; `identityProvider` : l'adresse du fournisseur d'identité, présente aussi hors session, pour le lien du §6) |

Une API non authentifiée répond `401` **sans redirection HTML** : le SPA relance lui-même le login.

## 5. Accès, rôle admin et provisionnement

À chaque connexion, sous un verrou consultatif PostgreSQL (deux premières connexions simultanées ne passent pas ensemble) :

1. **Le groupe d'accès doit figurer dans `groups`.** Sinon : refus (`core.not_in_access_group`, événement `login_refused` avec le `sub` et la raison). Pocket ID refuse déjà avant d'émettre un code ; ce contrôle couvre un client mal réglé.
2. **Le `sub` est connu** : le nom et l'email sont rafraîchis, et `is_admin` prend la valeur « le groupe admin figure dans `groups` ». Un changement de rôle est journalisé (`admin_granted` ou `admin_revoked`, source `identity_provider`).
3. **Sinon, un adulte en attente dont l'email correspond** (sans tenir compte de la casse) : son `oidc_subject` est renseigné, son nom rafraîchi, son rôle pris dans les groupes. Un adulte en attente a été **pré-enregistré** par un admin dans la zone admin (nom, email), par exemple pour lui attribuer des éléments avant sa première connexion.
4. **Sinon, un nouvel adulte est créé**, dans la limite de `household_settings.max_adults` (2 par défaut, réglable dans la zone admin ; les adultes en attente comptent). Au-delà : refus (`core.adult_cap`).
5. **Un email déjà porté par un autre adulte sous un autre `sub`** est refusé (`core.email_taken`) : c'est peut-être la même personne dont l'identifiant a changé, peut-être une autre ; la commande `rebind-subject` tranche (ci-dessous).

La page `/login-refused` explique ces refus et affiche l'email reçu, pour qu'on puisse le faire correspondre.

- **Aucun adulte n'est admin d'office.** Le premier administrateur est celui que l'hébergeur met dans le groupe admin ; tant que personne n'y est, personne n'administre Nid, et c'est le fournisseur d'identité qu'il faut corriger. Plusieurs admins sont possibles ; Nid n'édite pas le rôle (la zone admin le montre, `administration-and-audit.md` §2).
- **Confiance** : qui administre Pocket ID décide de l'accès et du rôle admin, et peut donner à un compte l'email d'un adulte en attente. C'est l'hébergeur, déjà dans la frontière de confiance (`administration-and-audit.md` §1).
- **Récupération** : la `person` est retrouvée par `oidc_subject`. Si le `sub` change (Pocket ID réinstallé, utilisateur recréé), le nouvel `sub` est refusé (`core.email_taken`). La commande d'administration `rebind-subject` (`administration-and-audit.md` §3) rattache la personne existante au nouvel `sub` et ferme ses sessions dans la même écriture (1F4). Sans elle, l'accès serait perdu.
- **Un seul rôle, technique** : `admin`, porté par `person.is_admin` (une copie, relue à chaque requête, de ce que le groupe admin disait à la dernière connexion). Il ne donne aucun droit sur les éléments (`authorization.md` §1). Qui peut atteindre Nid et qui l'administre se règle dans Pocket ID ; qui est adulte, enfant et possède quoi, dans Nid.
- **Les enfants** (`dependent`) ne se connectent jamais : ils n'ont pas de compte, donc pas de claim.

## 6. Limites de Pocket ID à assumer

- **Pas de passkey, pas de connexion** : Pocket ID n'authentifie que par passkey (ou par un jeton à usage unique que son administrateur crée, pour un nouvel appareil ou une récupération).
- **Déconnexion** : `POST /auth/logout` ne supprime que le cookie de Nid ; la session de Pocket ID reste ouverte, donc « se reconnecter » réussit sans geste tant qu'elle dure. L'interface le signale et propose le lien vers le portail pour fermer la session SSO : la déconnexion mène à `/signed-out`, qui ne relance pas le login. Pocket ID annonce un `end_session_endpoint` et la déconnexion par canal arrière : **non exécutés**, ils pourraient remplacer ce comportement (`../spikes.md` §9septies).
- **Pas de révocation poussée par défaut** : un compte retiré du groupe d'accès, ou désactivé dans Pocket ID, garde sa session Nid jusqu'à l'expiration du cookie (30 jours au plus) ou jusqu'à « déconnecter partout » ; un rôle admin retiré ne l'est qu'à la **prochaine connexion** de la personne. La déconnexion par canal arrière combinée au `session_epoch` fermerait cet écart (non faite).
- **Pas d'enregistrement dynamique de clients** (RFC 7591) : voir `tokens-and-credentials.md` §5 pour ses conséquences sur le MCP.
- **Verrou d'instance** : Pocket ID se pose un verrou dans sa base ; restauré depuis un instantané de moins d'une minute, il attend l'expiration du verrou pour démarrer (`deploy/README.md` §6).

## 7. Hypothèses envers l'hébergeur

- **Une seule origine** : le proxy route `/api`, `/auth`, `/mcp` et `/health` vers `nid-api`, et le reste vers `nid-web`, sur le même nom d'hôte. Le cookie de session en dépend, et cela évite toute configuration CORS.
- HTTPS terminé en amont, `X-Forwarded-*` de confiance, `PUBLIC_URL` correct (redirections OIDC, cookie, liens des emails).
- Pocket ID joignable depuis `nid-api` (découverte OIDC au démarrage, `/health/ready`) sur son adresse publique.

## 8. Points à vérifier à l'intégration

- Le montage de développement : Pocket ID veut le HTTPS et un nom d'hôte propre pour ses passkeys ; la pile de développement (Aspire, `architecture.md` §10.3) inclut donc un proxy TLS et un domaine à point (`*.localtest.me`). Le flux a été exécuté ainsi (`../spikes.md` §9septies). Les tests de bout en bout ouvrent la session de Pocket ID par son API (`../frontend/testing.md`).
- Deux premières connexions simultanées sur une base vide : le plafond du foyer s'applique, sans dépassement (création atomique sous verrou).
