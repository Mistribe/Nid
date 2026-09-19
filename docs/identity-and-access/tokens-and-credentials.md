# Jetons et identifiants de service

> Statut : brouillon. Jetons d'accès personnels pour le MCP et les automatisations, identifiants des workers d'ingestion.
> Références : `README.md`, `authorization.md` (portées), `../architecture.md` §8 (MCP) et §9.3 (jobs).

---

## 1. Jetons d'accès personnels (PAT)

### 1.1 Table `personal_access_token` (schéma `core`)

| Champ | Type | Remarques |
|---|---|---|
| `id` | uuid | |
| `person_id` | uuid | Un adulte |
| `name` | text | Libellé donné par l'utilisateur (ex. « Claude sur mon laptop ») |
| `prefix` | text | 8 caractères, pour retrouver la ligne sans exposer le secret |
| `token_hash` | char(64) | SHA-256 du jeton complet |
| `scopes` | text[] | `read`, `write`, `write:direct` |
| `created_at`, `expires_at` | timestamptz | `expires_at` obligatoire |
| `last_used_at?`, `revoked_at?` | timestamptz | |

### 1.2 Format et stockage

- Format : `nid_pat_<préfixe>_<secret>`, secret de 32 octets aléatoires en base64url. Le préfixe `nid_pat_` est reconnaissable par les outils de détection de secrets.
- Le secret n'est affiché **qu'une fois**, à la création.
- Stockage par SHA-256 du jeton complet : un secret de 256 bits n'a pas besoin d'un hachage lent. Recherche par préfixe indexé, comparaison en **temps constant**.

### 1.3 Création et gestion

- Création **uniquement depuis une session navigateur**, jamais avec un jeton : un jeton ne crée pas de jeton.
- Chacun gère **ses propres** jetons ; personne ne voit ceux de l'autre.
- Préréglages de portées :

| Préréglage | Portées | Usage |
|---|---|---|
| Lecture seule | `read` | Consultation par un assistant |
| **Lecture + propositions** (défaut) | `read`, `write` | Un assistant qui propose, l'humain valide |
| Écriture directe | `read`, `write`, `write:direct` | Saisie dictée par l'utilisateur lui-même |

- `write:direct` inclut `write`, qui inclut `read`.
- **Expiration** obligatoire : 90 jours par défaut, 365 au plus. Email 7 jours avant. **10 jetons actifs** au maximum par personne.

### 1.4 Utilisation et droits

- `Authorization: Bearer nid_pat_…` sur `/mcp` et `/api`. L'acteur est la personne du jeton ; `via` vaut `mcp` sur `/mcp`, `api` ailleurs.
- **Un jeton ne dépasse jamais les droits de sa personne** : ses portées ne font que restreindre. Les droits sont évalués à **chaque requête**, ils suivent donc les changements (propriété, visibilité).
- Le cookie de session n'est **pas** accepté sur `/mcp`. Les jetons ne sont **pas** acceptés sur `/auth/*` ni pour gérer les jetons.

### 1.5 Révocation et protection

- Révocation **immédiate** : lecture en base à chaque requête, pas de cache.
- « Déconnecter partout » propose aussi de révoquer tous les jetons de la personne.
- **Limitation de débit** : 60 requêtes/minute par jeton ; sur les échecs d'authentification, 20 en 10 minutes par IP (`429`).
- Les jetons sont **masqués** dans les journaux ; un échec d'authentification journalise le préfixe seulement (`token_auth_failed`, fréquence limitée).

## 2. Configuration d'un client MCP

Le client MCP envoie le jeton en en-tête `Authorization: Bearer …` vers `{PUBLIC_URL}/mcp`. Aucun flux OAuth ni enregistrement de client n'est nécessaire.

## 3. Identifiants des workers

Les workers d'ingestion appellent l'API interne (`../architecture.md` §9.3) avec des identifiants de service, distincts des jetons personnels.

### 3.1 Table `worker_credential` (schéma `core`)

| Champ | Type | Remarques |
|---|---|---|
| `id`, `name` | uuid, text | |
| `secret_hash` | char(64) | SHA-256 |
| `job_types` | text[] | Types de jobs que ce worker peut traiter |
| `created_at`, `revoked_at?` | timestamptz | |

### 3.2 Règles

- Format `nid_wrk_…`, remis au conteneur par **secret Docker**.
- Droits : réclamer, signaler (`heartbeat`), terminer ou échouer les jobs **de ses types**, et télécharger le document du job réclamé (jeton court). **Aucun accès** aux éléments ni aux personnes.
- L'API interne écoute sur un port séparé, non routé par le proxy (`../architecture.md` §7).
- **Rotation** par commande d'administration ; l'ancien secret est révoqué immédiatement (la mise à jour du secret côté conteneur provoque une brève interruption, acceptable ici).

## 4. Extensions écartées

- **Jeton d'accès par requête** (JWT courts) : inutile pour deux utilisateurs.
- **mTLS entre nid-api et les workers** : le réseau compose segmenté suffit.

## 5. OAuth pour le MCP : écarté en v1

- Authelia n'implémente **pas l'enregistrement dynamique de clients** (RFC 7591/7592) : chaque client MCP devrait être déclaré à la main dans Authelia, avec des jetons d'accès signés (`access_token_signed_response_alg`).
- Le jeton statique fonctionne avec tous les clients MCP, sans configuration côté Authelia.
- **Voie ouverte** si un client refuse le bearer statique : le SDK MCP C# permet de déclarer les métadonnées de ressource protégée et de valider des JWT ; il faudrait un client Authelia dédié par client MCP. À revoir à ce moment-là.
