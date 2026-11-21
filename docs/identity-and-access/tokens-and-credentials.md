# Jetons et identifiants de service

> Statut : brouillon. Jetons d'accès personnels pour le MCP et les automatisations, appairage des workers (§3, renvoie à `../ai/README.md`), secrets des comptes locaux (§6).
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
| **Lecture + propositions** (défaut) | `read`, `write` | Un assistant qui soumet des propositions (`write`) et ne peut plus écrire directement ; l’humain valide. Seule exception : les outils MCP `upload_document` (sous `Documents:McpMaxBytes`) et `request_document_upload` (URL à usage unique, sous `Documents:MaxSizeBytes`) déposent un document (aucun chiffre ne change) ; le dépôt REST garde `write:direct` (`documents-data-model.md` §11) |
| Écriture directe | `read`, `write`, `write:direct` | Seul préréglage qui écrit directement : saisie dictée par l'utilisateur lui-même |

- `write:direct` inclut `write`, qui inclut `read`.
- **Expiration** obligatoire : 90 jours par défaut, 365 au plus. Avertissement 7 jours avant, affiché dans l’interface (remplace l’email prévu au départ, voir « Livré en 4A »). **10 jetons actifs** au maximum par personne.

**Livré en 4A** (2026-10-16, `superpowers/specs/2026-10-16-personal-access-tokens-design.md`) :

- Pas d’événement `token_expired` : la liste « Mes jetons » montre les jetons expirés.
- Faute de canal d’email, l’expiration proche (7 jours) s’affiche dans l’interface : dans « Mes jetons » et dans un bandeau.
- Les jetons révoqués ou expirés **restent en base**.
- Un jeton n’atteint ni `/auth/*`, ni la gestion des jetons, ni « Déconnecter partout », ni l’import (marqueur `IBrowserOnly`, `403 platform.browser_only`).
- Un jeton porte **ses propres types de revendications** (`nid:token_person`, `nid:token_id`, `nid:token_scope`), jamais ceux de la session.
- Les échecs d’authentification sont limités par adresse (20 en 10 minutes, `429`) et au plus un `token_auth_failed` est journalisé par adresse et par minute.
- Précisions d’exécution : la forme du jeton est vérifiée avec une ancre `\z` (un `$` acceptait un saut de ligne final) ; l’exemption du CSRF dépend de la présence d’un en-tête `Bearer nid_pat_` (la requête est authentifiée par le jeton seul : se fonder sur les revendications faisait répondre `403` au lieu de `401` à un mauvais jeton sur un `POST`) ; le schéma de connexion par défaut reste le cookie (le retour OIDC ne doit jamais ouvrir de session sur le schéma des jetons) ; la route `/security` est chargée à la demande (budget du JavaScript initial).
- `/mcp` arrive avec 4B (§1.4).

### 1.4 Utilisation et droits

- `Authorization: Bearer nid_pat_…` sur `/mcp` et `/api`. L'acteur est la personne du jeton ; `via` vaut `mcp` sur `/mcp`, `api` ailleurs.
- **Un jeton ne dépasse jamais les droits de sa personne** : ses portées ne font que restreindre. Les droits sont évalués à **chaque requête**, ils suivent donc les changements (propriété, visibilité).
- Le cookie de session n'est **pas** accepté sur `/mcp`. Les jetons ne sont **pas** acceptés sur `/auth/*` ni pour gérer les jetons.

### 1.5 Révocation et protection

- Révocation **immédiate** : lecture en base à chaque requête, pas de cache.
- « Déconnecter partout » propose aussi de révoquer tous les jetons de la personne.
- **Limitation de débit** : 60 requêtes/minute par jeton ; sur les échecs d'authentification, 20 en 10 minutes par IP (`429`).
- Les jetons sont **masqués** dans les journaux ; un échec d'authentification journalise le préfixe seulement (`token_auth_failed`, fréquence limitée).

### 1.6 Abonnement de calendrier (#80)

Une application de calendrier ne sait pas envoyer d'en-tête : le secret est dans l'URL. Réglages › Calendrier crée, régénère ou supprime **un flux par personne**.

- Une ligne de `personal_access_token` au preset `calendar` (portées `{calendar}`), avec ses options `calendar_scope` (`household`, `me`) et `calendar_amounts` (`ck_personal_access_token_calendar` : ces colonnes, et elles seules, sur un flux). `calendar_modules` (#243, `text[]`) restreint le flux à certains modules de « À venir » (`CalendarModules`) ; nul, il les liste tous, y compris un module ajouté plus tard : c'est la valeur des flux antérieurs et celle qu'enregistre une sélection de tous les modules. Elle compte dans les 10 jetons actifs, figure dans « Mes jetons » avec sa dernière utilisation, se révoque de là comme les autres, et « Déconnecter partout » la révoque aussi. Validité : 365 jours depuis la création ou la dernière régénération ; la notification `token_expiring` la couvre.
- Secret `nid_cal_<préfixe>_<secret>`, même format et même hachage que `nid_pat_` : l'authentification par `Bearer` ne lit que `nid_pat_` et `FindByPrefixAsync` ignore les flux, si bien que ce secret n'ouvre ni `/api` ni `/mcp`. Il n'est affiché qu'une fois, dans l'adresse rendue à la création ; régénérer révoque l'ancien dans la même transaction.
- `GET /api/v1/calendar/<secret>.ics`, sans session : un secret inconnu, falsifié, révoqué ou expiré, ou une personne qui n'est plus un adulte actif, répond `404`. Sinon la lecture se fait comme cette personne, avec la seule portée `read`, par le même pipeline que toute lecture (garde de sortie comprise) : « À venir » sur 12 mois (modules, échéance du PER, calendrier fiscal) dans la portée du flux. Le Foyer ne montre que les éléments partagés, Moi y ajoute les siens : jamais les éléments privés de l'autre adulte.
- iCalendar (RFC 5545) : un événement d'un jour par échéance, `UID` stable (type, élément, date, haché), le libellé de « À venir », un lien vers l'élément. **Pas de montant** sauf option : un calendrier abonné est souvent copié chez un tiers.
- Le chemin n'est jamais journalisé : la ligne de début de requête d'ASP.NET est filtrée (niveau `Warning`), les traces OpenTelemetry excluent `/api/v1/calendar` (`url.path` porterait le secret). Réponse `Cache-Control: private, no-store`.

## 2. Configuration d'un client MCP

Le client MCP envoie le jeton en en-tête `Authorization: Bearer …` vers `{PUBLIC_URL}/mcp` (Streamable HTTP). Aucun flux OAuth ni enregistrement de client n'est nécessaire. La page Sécurité (« Brancher un assistant IA », #309) donne ces configurations, prêtes à copier, avec le vrai jeton juste après sa création. Là où le client sait lire l'environnement, le jeton vient de la variable `NID_TOKEN`, jamais d'un fichier ; **ne jamais committer un jeton**.

Syntaxe vérifiée le 2026-11-19 dans la documentation de chaque éditeur.

**Claude Desktop**, deux chemins :

- *Connecteur personnalisé* (Personnaliser › Connecteurs › Ajouter › Connecteur personnalisé) : URL `{PUBLIC_URL}/mcp`, authentification « Sans connexion », en-tête de requête `authorization` = `Bearer <jeton>` (bêta ; valeur non réaffichée, retirer et rajouter pour la changer). L'appel part des serveurs d'Anthropic : Nid doit être joignable depuis Internet en https sur le port 443. Sur un abonnement Team ou Enterprise, un connecteur d'organisation partage son en-tête avec tous les membres : un jeton personnel s'ajoute depuis un compte personnel.
- *Extension Nid* (`.mcpb`, #309) : « Télécharger l'extension » sur la page Sécurité (`GET /api/v1/mcp/nid.mcpb`, adulte connecté au navigateur seulement, `401` sinon, jeton compris). Un zip au format MCPB 0.3 : un `manifest.json` rempli pour l'instance (sa version, `{PUBLIC_URL}/mcp` comme adresse par défaut, l'instance comme politique de confidentialité), et `mcp-remote` vendu dans `node_modules`, qui sert de pont, construit dans l'image depuis un fichier de verrouillage (`src/Hosting/Nid.Host/mcpb/`, Renovate « MCP extension »), jamais téléchargé à l'exécution ; la construction de l'image vérifie le manifeste avec `mcpb validate`. Le champ `token` est `sensitive` (trousseau du système) ; Claude Desktop le met dans la variable `NID_AUTH` (`env`), que `mcp-remote` développe lui-même dans son en-tête (`Authorization:${NID_AUTH}`) : jamais dans `args` ; aucun jeton dans le fichier. L'extension tourne sur l'ordinateur, avec le Node.js de Claude Desktop : elle joint un Nid du réseau local. Sans `npm ci` dans ce dossier, un poste de développement répond `404`.
- *Configuration manuelle* (`claude_desktop_config.json`, repliée sous l'extension) : un pont local `mcp-remote`, **version fixée**, qui tourne sur l'ordinateur et joint donc un Nid du réseau local ; Node.js requis, jeton en clair dans le fichier :

```json
{
  "mcpServers": {
    "nid": {
      "command": "npx",
      "args": ["-y", "mcp-remote@0.16.5", "{PUBLIC_URL}/mcp", "--header", "Authorization:${NID_AUTH}"],
      "env": { "NID_AUTH": "Bearer <jeton>" }
    }
  }
}
```

L'en-tête passe par `env` : une espace dans un argument casse sous Windows.

**Claude Code** :

```bash
claude mcp add --transport http nid {PUBLIC_URL}/mcp --header "Authorization: Bearer <jeton>"
```

Claude Code range cette configuration dans la portée `local` par défaut (propre à l’utilisateur et au projet, hors du dépôt). Pour partager la configuration d’un projet, le fichier `.mcp.json` lit le jeton dans l’environnement :

```json
{
  "mcpServers": {
    "nid": {
      "type": "http",
      "url": "{PUBLIC_URL}/mcp",
      "headers": { "Authorization": "Bearer ${NID_TOKEN}" }
    }
  }
}
```

**Cursor** (`~/.cursor/mcp.json` ou `.cursor/mcp.json`) :

```json
{ "mcpServers": { "nid": { "url": "{PUBLIC_URL}/mcp", "headers": { "Authorization": "Bearer ${env:NID_TOKEN}" } } } }
```

**VS Code / GitHub Copilot**, format portable (`.mcp.json` à la racine de l'espace de travail, ou `~/.copilot/mcp-config.json`) ; le format `.vscode/mcp.json` (objet `servers`) reste lu par VS Code mais ne passe pas les variables d'entrée aux sessions Copilot :

```json
{ "mcpServers": { "nid": { "type": "http", "url": "{PUBLIC_URL}/mcp", "headers": { "Authorization": "Bearer ${NID_TOKEN}" } } } }
```

**Gemini CLI** (la forme documentée prend le jeton tel quel : `settings.json` le garde en clair ; pas de `_` dans le nom du serveur) :

```bash
gemini mcp add --transport http --header "Authorization: Bearer <jeton>" nid {PUBLIC_URL}/mcp
```

**Codex CLI** (`~/.codex/config.toml`) :

```toml
[mcp_servers.nid]
url = "{PUBLIC_URL}/mcp"
bearer_token_env_var = "NID_TOKEN"
```

ChatGPT n'est pas couvert tant que ses connecteurs ne confirment pas un en-tête statique (§5 : pas d'OAuth sur `/mcp` en v1).

## 3. Identifiants des workers

**Remplacé par l'appairage (#171).** Les workers n'appellent plus `nid` : `nid` appelle des workers appariés, avec
clés épinglées des deux côtés, mutual TLS et messages signés (`../ai/README.md` §3 et §4, #166). Un worker ne détient
aucun identifiant qui ouvre `nid`, et `nid` n'expose aucune route aux workers.

- **Appairage** : au premier démarrage, le worker génère sa paire de clés et affiche un code à usage unique et
  l'empreinte de sa clé ; un admin saisit l'URL et le code dans Réglages › IA, confirme l'empreinte, puis les deux
  côtés épinglent la clé de l'autre.
- **Clé de Nid** : chiffrée par Data Protection, comme les clés d'API des fournisseurs (`ai.ai_key`).
- **Rotation et révocation** : depuis les réglages, par un admin, effectives à l'appel suivant ; chaque opération est
  auditée (`../ai/README.md` §12).
- La table `worker_credential` (secret `nid_wrk_…`, `job_types`) et les droits `claim` / `heartbeat` / `complete` sont
  abandonnés ; le registre est `ai.model_worker` (`../ai/README.md` §13).

## 4. Extensions écartées

- **Jeton d'accès par requête** (JWT courts) : inutile pour deux utilisateurs.
- **mTLS entre nid et les workers** : écarté à l'origine (le réseau compose segmenté suffisait) ; **retenu depuis #171**, un worker pouvant être sur une autre machine ou hors des lieux (§3).

## 5. OAuth pour le MCP : écarté en v1

- Pocket ID n'annonce **pas d'enregistrement dynamique de clients** (RFC 7591/7592) : chaque client MCP devrait être déclaré à la main dans Pocket ID, avec des jetons d'accès que Nid saurait valider.
- Le jeton statique fonctionne avec tous les clients MCP, sans configuration côté fournisseur d'identité.
- **Voie ouverte** si un client refuse le bearer statique : le SDK MCP C# permet de déclarer les métadonnées de ressource protégée et de valider des JWT ; il faudrait un client Pocket ID dédié par client MCP. À revoir à ce moment-là.

## 6. Secrets des comptes locaux (#215)

Détail dans `local-accounts.md` ; ce qui les distingue des jetons ci-dessus :

| Secret | Format | Stockage | Durée, usage |
|---|---|---|---|
| Mot de passe | Choisi par l'adulte, 12 à 256 caractères | `local_credential.password_hash` : `PasswordHasher` d'ASP.NET Core Identity (PBKDF2-HMAC-SHA512, 210 000 itérations, sel par mot de passe), recalculé à la connexion quand les paramètres montent | Sans expiration ; changé par l'adulte ou par un lien de réinitialisation |
| Lien de mot de passe | `nid_link_<secret>`, 256 bits, dans le fragment de `{PUBLIC_URL}/password#…` | `credential_link.token_hash` : SHA-256, comme un jeton personnel | 72 heures, un seul usage, un seul vivant par adulte |
| Jeton de mise en place | `nid_setup_<secret>`, 256 bits | **En mémoire seulement**, tiré à chaque démarrage tant qu'aucun adulte n'existe, écrit dans le journal | Jusqu'à la fin de la mise en place ou au prochain démarrage |
| Secret TOTP | 160 bits | `local_credential.totp_secret`, chiffré par Data Protection (il doit être relu) | Jusqu'à sa désactivation |
| Codes de secours | 10 codes de 80 bits, `xxxx-xxxx-xxxx-xxxx` | `recovery_code.code_hash` : SHA-256 | Un seul usage ; régénérés en bloc |

- Un mot de passe est le seul secret **choisi par un humain** : lui seul a un hachage lent. Les autres sont aléatoires et assez longs pour un SHA-256.
- Aucun de ces secrets n'est accepté comme `Bearer` : ils n'ouvrent ni `/api` ni `/mcp`. Un jeton d'accès personnel, lui, n'atteint ni les routes de connexion ni la gestion du mot de passe et du TOTP (`IBrowserOnly`).
- La limite des échecs d'authentification par adresse (§1.5, 20 en 10 minutes) est **partagée** : elle compte aussi les échecs de mot de passe, de code, de lien et de jeton de mise en place.
- Aucun n'apparaît dans les journaux, les traces, l'audit ni l'export `nid.data`.
