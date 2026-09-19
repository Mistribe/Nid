# Format d'import et d'export de Nid

> Statut : brouillon. Décrit le fichier JSON standard, ses règles de validation et le protocole d'échange avec le serveur, orchestré par le navigateur. Seules les sections du module Abonnements sont définies ; les autres modules ajouteront les leurs.
> Références : `subscription-data-model.md` (sens des entités), `core-data-model.md` (personnes, propriété, provenance), `architecture.md` §11 (export) et §5.4 (Synapse).

---

## 1. Objet

Un **seul format** sert à trois usages :

- la **reprise initiale** de données existantes (par exemple l'ancien `subscriptions.json`, converti dans ce format) ;
- l'**export complet** à la demande (`Project.md` §13), pour ne jamais dépendre de l'application ;
- le **transfert** ou la restauration logique des données d'un foyer vers une autre instance.

## 2. Principes

- Un fichier JSON UTF-8, **versionné** et auto-descriptif.
- **Même format à l'import et à l'export** : un export rejoué sur une instance vide reconstitue les données du foyer.
- **Le serveur est l'autorité de validation.** Le navigateur peut pré-valider pour le confort, jamais pour décider. Le serveur revalide tout avant d'écrire.
- **Validation complète avant toute écriture**, puis écriture tout-ou-rien en une seule transaction.
- **Le navigateur orchestre** l'import et l'export par une suite d'appels (§11). **Aucune tâche de fond** n'y participe : ni traitement asynchrone ni planificateur.
- **Aperçu** : l'étape de validation (§11) produit un rapport sans aucune écriture dans les données du foyer.
- **Idempotent** : réimporter le même fichier ne crée pas de doublons (§8).
- **Lisible et modifiable à la main**. Le fichier ne contient que des faits saisis : la couverture, les charges et les coûts sont **dérivés**, jamais écrits dans le fichier.
- **Strict sur le fond** : section ou champ inconnu = erreur explicite, pour ne jamais perdre de données sans le dire. **Tolérant sur la forme** : un montant écrit en nombre JSON est accepté (lu en décimal, jamais en flottant) ; l'export écrit des chaînes.

## 3. Enveloppe

```json
{
  "format": "nid.data",
  "version": 1,
  "exported_at": "2026-09-19T14:00:00Z",
  "persons": [],
  "providers": [],
  "subscriptions": [],
  "billing_phases": [],
  "purchases": []
}
```

| Clé | Type | Remarques |
|---|---|---|
| `format` | string | Toujours `nid.data` |
| `version` | int | Version du format. L'import accepte la version courante et la précédente |
| `exported_at` | timestamp UTC? | Renseigné par l'export, ignoré à l'import |
| `persons` | tableau | Voir §5.1 |
| `providers` | tableau? | Fournisseurs **personnalisés** seulement (§5.2) |
| `subscriptions`, `billing_phases`, `purchases` | tableaux | Module Abonnements (§5.3 à §5.5) |

Sections **réservées**, pas encore définies : `envelopes`, `valuations`, `flows`, `properties`, `loans`, `documents`. Un fichier qui les contient est refusé tant que le module correspondant n'existe pas.

## 4. Conventions

- **Casse** : `snake_case` dans le fichier, comme `subscription-data-model.md`. Les DTO de l'API sont indépendants (`architecture.md` §7).
- **Dates** : `AAAA-MM-JJ`. **Intervalles semi-ouverts** : `ends_on` est le premier jour **non** couvert.
- **Montants** : `{ "amount": "12.99", "currency": "EUR" }`, devise ISO 4217 en majuscules. Négatif seulement sur les achats `adjustment` et `refund`.
- **Durées** : `{ "count": 1, "unit": "month" }`, unités `day`, `week`, `month`, `year`.
- **Identifiants** : `id` est une **clé stable du fichier** (`^[a-z0-9][a-z0-9-]*$`), unique dans sa section. Ce n'est pas l'UUID interne de l'application. Les références (`subscription_id`, `person`…) désignent ces clés.
- **`note`** : texte libre optionnel sur les abonnements, phases et achats. Conservé, sans effet métier (par exemple pour garder l'identifiant d'origine).

## 5. Sections

### 5.1 `persons`

| Champ | Type | Remarques |
|---|---|---|
| `ref` | string | Clé locale au fichier |
| `kind` | string | `adult` ou `dependent` |
| `display_name` | string | |
| `email` | string? | Sert à retrouver le compte d'un `adult` (§6) |

### 5.2 `providers`

Uniquement les fournisseurs absents du catalogue. Un fournisseur du catalogue n'est pas listé : `provider_key` suffit.

| Champ | Type | Remarques |
|---|---|---|
| `key` | string | Unique, différente de toute clé du catalogue |
| `name` | string | |
| `website`, `pricing_page` | url? | `http` ou `https` |
| `label_keys` | `[string]` | Au moins une |

### 5.3 `subscriptions`

| Champ | Type | Remarques |
|---|---|---|
| `id` | string | |
| `provider_key` | string? | Clé du catalogue ou de `providers` ; inconnue = erreur |
| `name` | string | |
| `ownership` | `[{ person, share_bp }]` | Références vers `persons`. Somme des `share_bp` = 10 000 |
| `visibility` | string | `shared` (défaut) ou `private` (un seul propriétaire adulte) |
| `service_users` | `[person]` | Références vers `persons`, `dependent` possibles |
| `auto_renew` | bool | |
| `cancelled_at` | date? | |
| `note` | string? | |

### 5.4 `billing_phases`

| Champ | Type | Remarques |
|---|---|---|
| `id`, `subscription_id` | string | |
| `starts_on`, `ends_on?` | date | `ends_on` nul = phase en cours |
| `price` | montant | |
| `every`, `covers?` | durée | `covers` vaut `every` par défaut |
| `max_cycles?` | int | |
| `payer` | `{ type, person? }` | `type` : `person` ou `household` ; `person` est une référence, obligatoire si `type = person` |
| `note` | string? | |

### 5.5 `purchases`

| Champ | Type | Remarques |
|---|---|---|
| `id` | string | |
| `date` | date | |
| `label` | string | |
| `kind` | string | `prepaid_term`, `bundle`, `gift`, `adjustment`, `refund` |
| `price` | montant | `0` pour un cadeau reçu |
| `payer` | `{ type, person? }` | `type` : `person`, `household` ou `external` |
| `allocated_amount` | montant décimal? | `bundle` uniquement ; entre 0 et `price` |
| `grants` | `[{ subscription_id, duration, source, starts_on? }]` | `source` : `paid`, `bonus`, `included`, `trial`, `gift` ; sans `starts_on`, la période s'**empile** après la couverture en cours |
| `note` | string? | |

Le sens de chaque entité, l'algorithme de couverture et le calcul des coûts sont ceux de `subscription-data-model.md`.

## 6. Résolution des personnes

- Chaque `adult` du fichier doit être **associé à un compte existant** lors de l'import (par `email`, ou par une correspondance fournie à l'import). Un import ne crée jamais d'adulte : un adulte naît à sa première connexion (`core-data-model.md` §4.4). Un `adult` non résolu est une erreur.
- Un `dependent` est retrouvé par son nom ; s'il n'existe pas, il est créé.
- Le compte qui lance l'import doit avoir le droit d'écrire sur ce qu'il crée. Les éléments importés sont créés avec les propriétaires et la visibilité du fichier.

## 7. Validation

**Erreurs bloquantes** (rien n'est écrit) :

- version ou format non supporté, section ou champ inconnu ;
- `id` ou `ref` dupliqué dans une section ;
- référence non résolue (personne, abonnement, fournisseur) ;
- somme des `share_bp` différente de 10 000, ou élément `private` avec plusieurs propriétaires ;
- phases d'un même abonnement qui se chevauchent, ou `ends_on` ≤ `starts_on` ;
- durée dont `count` ≤ 0, unité inconnue ;
- prix négatif hors `adjustment` et `refund` ; `allocated_amount` hors d'un `bundle` ou supérieur au prix ;
- devise non ISO 4217, date invalide ;
- `adult` non résolu (§6).

**Avertissements** (l'import continue) :

- période explicite (`starts_on`) qui chevauche une couverture existante : doublon possible ;
- abonnement sans phase, sans achat ni couverture ;
- durée d'une phase qui n'est pas un multiple de sa période ;
- paiement dans une devise sans taux disponible à sa date (`fx_rate_missing` prévisible).

## 8. Idempotence et conflits

- Chaque entité importée conserve son `id` de fichier comme **`import_ref`**, unique par section dans l'instance. Deux fichiers différents qui réutilisent les mêmes `id` sont en conflit : préfixer les `id` de chaque source.
- Réimport :
  - même `id`, contenu identique → `unchanged` ;
  - même `id`, contenu différent → `conflict`, **rien n'est écrasé** ;
  - option `replace` : le contenu de l'entité est mis à jour, l'ancienne valeur restant dans l'audit.

## 9. Provenance, audit et rapport

- Les données importées portent `source = import` et `created_via = import` ; l'auteur est l'adulte qui lance l'import.
- **Audit** : une entrée par import, avec l'identifiant de session, l'empreinte du contenu reçu (SHA-256 des empreintes de lots), la version du format et les compteurs.
- Le navigateur peut **conserver le fichier d'origine** : case « conserver le fichier », qui l'envoie séparément comme `document` (`core-data-model.md` §6) rattaché aux éléments créés. Le serveur ne reçoit plus le fichier d'office.
- **Rapport** (à l'écran et téléchargeable) : par section, nombre d'entités `created`, `unchanged`, `conflict`, plus la liste des avertissements et des erreurs avec leur chemin (`purchases[12].grants[0]`, indice global dans la section).

## 10. Export

L'export est **assemblé par le navigateur** à partir d'appels paginés ; le serveur ne construit jamais le fichier.

| Appel | Rôle |
|---|---|
| `GET /api/v1/platform/export/manifest` | Sections disponibles pour cet utilisateur, nombre d'entités par section, `exported_at` (heure du serveur), `revision` |
| `GET /api/v1/platform/export/{section}?cursor=&limit=` | Page d'au plus 500 entités, curseur opaque, ordre stable |

- Portée `read`. **Chacun exporte ce qu'il peut lire** : les éléments privés d'un adulte n'apparaissent que dans son export. N'apparaissent dans `persons` que les personnes référencées.
- Le navigateur assemble l'enveloppe `nid.data` (§3) et déclenche le téléchargement. Il peut aussi produire **un CSV par section**, converti côté client.
- **Cohérence** : `revision` est un compteur monotone (numéro de séquence de l'audit). À la fin, le navigateur relit le manifeste ; si `revision` a changé, il signale que des données ont bougé pendant l'export et propose de relancer. Aucun verrou n'est posé.
- Les contenus des documents (PDF) ne sont pas dans le fichier ; une section future les référencera par `sha256`.
- L'export ne remplace pas la sauvegarde complète (`architecture.md` §11) : il garantit de pouvoir quitter l'application.

## 11. Import : protocole et mise en œuvre

### 11.1 Session d'import

Le navigateur pilote une **session** ; le serveur valide et stocke les lots en **zone de staging**, puis écrit en une seule transaction.

| Appel | Rôle |
|---|---|
| `POST /api/v1/platform/imports` | Crée une session pour l'adulte connecté. Une seule session active par personne |
| `PUT /imports/{id}/batches/{section}/{n}` | Reçoit un lot avec son empreinte, le valide seul, le stocke. Idempotent |
| `POST /imports/{id}/validate` | Validation **complète** du staging ; renvoie le rapport. Synchrone |
| `POST /imports/{id}/commit` | **Revalide**, puis écrit en une transaction ; renvoie le rapport final. Synchrone. Paramètre `replace` (§8), faux par défaut |
| `GET /imports/{id}` | État, compteurs, rapport de la dernière validation |
| `GET /imports` | Sessions récentes de la personne |
| `DELETE /imports/{id}` | Annule la session et vide le staging |

Portée requise : `write:direct` (un import écrit directement, `identity-and-access/authorization.md` §3). Une session n'est visible que de la personne qui l'a créée.

**États** : `open` (réception de lots) → `validated` ou `invalid` → `committed`. Un nouveau lot après `validated` ramène la session à `open`. Sorties : `cancelled`, `expired`.

### 11.2 Lots

- Au plus **500 entités** et **1 Mo** par lot ; un lot appartient à une section (`persons`, `subscriptions`…) et porte un indice `n`.
- L'**empreinte** est le SHA-256 du contenu JSON canonique du lot, au sens de la **RFC 8785** (JSON Canonicalization Scheme), identique côté navigateur et côté serveur. Rejouer le même lot (`section`, `n`, empreinte) donne le même résultat ; même emplacement avec une autre empreinte : `409`.
- **Ordre libre** à l'envoi : les références entre lots sont vérifiées à la validation complète, pas à la réception.
- Le serveur valide chaque lot à la réception : structure (types du contrat), règles locales à l'entité (montants, durées, devises, unités). Les erreurs reviennent immédiatement, avec leur chemin.

### 11.3 Staging

Deux tables du schéma `platform`, **jamais lues par les modules** métier :

| Table | Champs |
|---|---|
| `import_session` | `id`, `person_id`, `state`, `created_at`, `expires_at`, `counts`, `report` |
| `import_batch` | `session_id`, `section`, `batch_index`, `content_hash`, `payload` (JSONB), `received_at` |

Le staging est vidé à `commit`, à l'annulation et à l'expiration.

### 11.4 Validation et écriture

- **`validate`** : chaque module valide l'ensemble de sa section sur le staging (références entre lots, chevauchements, invariants du Core, droits de la personne, idempotence par `import_ref`, taux de change), et Platform assemble un rapport unique. Rien n'est écrit dans les données du foyer.
- **`commit`** : le serveur **rejoue la validation complète** (l'état a pu changer depuis `validate`), puis appelle les modules dans l'ordre `persons` → `providers` → `subscriptions` → `billing_phases` → `purchases`, dans **une transaction unique**. Le staging de la session est vidé dans la même transaction. Une erreur annule tout.
- Les `DbContext` des modules partagent la connexion et la transaction de l'import. À vérifier au prototypage (même point que l'outbox, `architecture.md` §16).

### 11.5 Bornes

Pour tenir dans une requête ordinaire, sans tâche de fond :

| Borne | Valeur par défaut |
|---|---|
| Entités par import | 50 000 (au-delà : refus, à découper par section) |
| Entités / taille par lot | 500 / 1 Mo |
| Durée maximale de `validate` et de `commit` | 60 s, configurable ; dépassement : annulation complète, erreur `import_too_large` |
| Durée de vie d'une session non validée | 24 h |
| Sessions actives par personne | 1 |

Le **nettoyage** des sessions expirées se fait **à la demande** : à la création d'une session et à l'affichage de la liste. Aucun planificateur.

### 11.6 Reprise

Fermer l'onglet n'annule rien : la session reste jusqu'à son expiration. Pour reprendre, l'utilisateur **re-sélectionne le fichier** ; le navigateur recalcule les lots de façon déterministe (mêmes sections, mêmes tailles, mêmes empreintes) et **saute ceux que le serveur possède déjà**.

### 11.7 Contrat des modules

Chaque module fournit une `IImportSection` dans ses `Contracts` (il possède sa section) :

| Opération | Rôle |
|---|---|
| `ValidateBatch` | Règles locales aux entités d'un lot |
| `ValidateAll` | Validation complète sur le staging de la session |
| `Apply` | Écriture, appelée dans la transaction de `commit` |
| `ExportCount`, `ExportPage` | Manifeste et pages d'export (§10) |

Platform ne connaît aucune entité métier : elle orchestre l'ordre, le rapport, la transaction et l'audit.

### 11.8 Autres clients

Un script ou une commande d'administration suit le même protocole : créer une session, envoyer un lot (un seul suffit pour un petit fichier), valider, valider à nouveau à la confirmation, puis commiter. Il n'existe pas de chemin d'import simplifié : une seule voie à tester et à sécuriser.

### 11.9 Rôle du navigateur

Le navigateur orchestre (assistant décrit dans `frontend/screens.md`) : lecture et conversion locale (CSV → `nid.data`), pré-validation structurelle avec le schéma publié, découpage en lots, envoi avec progression et nouvelles tentatives, affichage des rapports, confirmation avant `commit`. Il ne décide jamais : le rapport du serveur fait foi.

## 12. Exemple minimal

```json
{
  "format": "nid.data",
  "version": 1,
  "persons": [
    { "ref": "adult-1", "kind": "adult", "display_name": "Adulte 1" },
    { "ref": "adult-2", "kind": "adult", "display_name": "Adulte 2" }
  ],
  "subscriptions": [
    {
      "id": "sub-spotify",
      "provider_key": "spotify",
      "name": "Spotify Premium Duo",
      "ownership": [
        { "person": "adult-1", "share_bp": 5000 },
        { "person": "adult-2", "share_bp": 5000 }
      ],
      "visibility": "shared",
      "service_users": ["adult-1", "adult-2"],
      "auto_renew": true
    }
  ],
  "billing_phases": [
    {
      "id": "ph-001",
      "subscription_id": "sub-spotify",
      "starts_on": "2025-06-28",
      "ends_on": null,
      "price": { "amount": "17.20", "currency": "EUR" },
      "every": { "count": 1, "unit": "month" },
      "payer": { "type": "household" }
    }
  ],
  "purchases": [
    {
      "id": "pu-001",
      "date": "2025-10-10",
      "label": "Carte cadeau 3 mois",
      "kind": "gift",
      "price": { "amount": "0.00", "currency": "EUR" },
      "payer": { "type": "external" },
      "grants": [
        { "subscription_id": "sub-spotify", "duration": { "count": 3, "unit": "month" }, "source": "gift" }
      ]
    }
  ]
}
```

## 13. Questions ouvertes

- Association des adultes aux comptes : par `email` du fichier, ou par saisie à l'import (un fichier sans email doit rester importable).
- Schéma JSON de `nid.data` : généré depuis les types source-générés (`JsonSchemaExporter`, .NET 9+) et publié par `GET /api/v1/platform/import/schema` ; le navigateur le compile au build (`frontend/architecture.md`).
- Conversion CSV → `nid.data` : réalisée côté navigateur ; les sections `valuations` et `flows` restent à définir avec les modules Investments et RealEstate.
- Valeurs par défaut des bornes (§11.5) : à ajuster après mesure sur des imports réels.
- Format des documents dans l'export (section `documents` référençant les `sha256`).
- Sections des autres modules : définies quand ces modules le seront.
- Politique de migration entre versions du format : l'import de la version précédente est prévu, sa conversion reste à spécifier au premier changement de version.
